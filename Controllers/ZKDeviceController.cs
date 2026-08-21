using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Controllers;

/// <summary>
/// 熵基（ZKTeco）考勤机 PUSH 通讯协议对接：这几个路径是写死在设备固件里的，不能改名字。
/// 整体流程：设备开机联网 → GET /iclock/cdata 初始化握手 → 定期 GET /iclock/getrequest 心跳
/// （服务器可以顺便夹带要下发给设备的命令）→ 有打卡数据时 POST /iclock/cdata 主动推上来。
/// 这几个接口设备直接匿名调用，没有登录 Cookie，靠设备序列号（SN）白名单做来源校验，不走 [Authorize]。
/// </summary>
[ApiController]
public class ZKDeviceController(
    AttendanceDbContext db,
    IZKDeviceSyncService syncService,
    IOptions<ZKDeviceOptions> options,
    IOptions<AppSettingsOptions> appOptions,
    IWebHostEnvironment env,
    ILogger<ZKDeviceController> logger) : ControllerBase
{
    private static readonly Encoding Gbk = Encoding.GetEncoding("GBK");
    private readonly ZKDeviceOptions _opt = options.Value;

    /// <summary>设备序列号是否在数据库白名单里、且是启用状态（"考勤机管理"页面维护这张表）。</summary>
    private async Task<bool> IsKnownDeviceAsync(string? sn, CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(sn) &&
        await db.ZKDevices.AnyAsync(d => d.SN == sn && d.IsActive, ct);

    /// <summary>记录这台设备最近一次成功通信的时间，供后台页面显示在线/离线。</summary>
    private Task TouchLastSeenAsync(string sn, CancellationToken ct = default) =>
        db.ZKDevices.Where(d => d.SN == sn)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, DateTime.Now), ct);

    /// <summary>初始化握手：设备开机/联网后第一个请求，服务器返回配置块，设备收到后开始定期心跳。</summary>
    [HttpGet("/iclock/cdata")]
    public async Task<IActionResult> Init([FromQuery] string? SN, [FromQuery] string? options, [FromQuery] string? language,
        [FromQuery] string? pushver, [FromQuery] string? PushOptionsFlag, CancellationToken ct)
    {
        logger.LogInformation("考勤机初始化请求：SN={SN}, options={Options}, pushver={PushVer}", SN, options, pushver);
        if (!await IsKnownDeviceAsync(SN, ct))
        {
            logger.LogWarning("未知设备序列号尝试初始化：{SN}", SN);
            return Content("UNKNOWN DEVICE", "text/plain", Gbk);
        }
        await TouchLastSeenAsync(SN!, ct);

        var sb = new StringBuilder();
        sb.Append("GET OPTION FROM: ").Append(SN);
        sb.Append("\nATTLOGStamp=None");
        sb.Append("\nOPERLOGStamp=None");
        sb.Append("\nATTPHOTOStamp=None");
        sb.Append("\nErrorDelay=30");
        sb.Append("\nDelay=").Append(_opt.HeartbeatIntervalSeconds);
        sb.Append("\nTimeZone=8");
        sb.Append("\nRealtime=1");           // 有打卡就立即推，不等心跳周期
        sb.Append("\nEncrypt=None");
        sb.Append('\n');
        return Content(sb.ToString(), "text/plain", Gbk);
    }

    /// <summary>数据上传：设备真正推数据的地方，用 table 区分数据类型。</summary>
    [HttpPost("/iclock/cdata")]
    public async Task<IActionResult> Upload([FromQuery] string? SN, [FromQuery] string? table, CancellationToken ct)
    {
        if (!await IsKnownDeviceAsync(SN, ct))
        {
            logger.LogWarning("未知设备序列号尝试上传数据：{SN}", SN);
            return Content("UNKNOWN DEVICE", "text/plain", Gbk);
        }
        await TouchLastSeenAsync(SN!, ct);

        var bodyBytes = await ReadBodyBytesAsync();

        try
        {
            if (string.Equals(table, "ATTLOG", StringComparison.OrdinalIgnoreCase))
            {
                var text = Gbk.GetString(bodyBytes);
                var rows = ParseAttLog(text);
                await syncService.ProcessAttLogAsync(SN!, rows, ct);
                logger.LogInformation("考勤机 {SN} 推送打卡记录 {Count} 条", SN, rows.Count);
            }
            else if (string.Equals(table, "ATTPHOTO", StringComparison.OrdinalIgnoreCase))
            {
                await SaveAttPhotoAsync(SN!, bodyBytes);
            }
            else
            {
                logger.LogInformation("考勤机 {SN} 推送了暂不处理的数据类型：table={Table}", SN, table);
            }
        }
        catch (DbUpdateException ex)
        {
            // 落库重试了几次还是冲突（多半是打卡高峰期撞车撞得太狠），这批数据没能保存成功。
            // 故意不回 "OK"——让设备以为这次没传成功，它自己的重传机制会在下次上传时把这批数据再推一遍，
            // 不用另外建一张"待重试"表。返回 "OK" 反而会让这批数据永久静默丢失（设备以为传成功了就不会再传）。
            logger.LogError(ex, "处理考勤机 {SN} 推送的数据失败（table={Table}），已重试仍冲突，让设备下次重传", SN, table);
            return Content("ERROR", "text/plain", Gbk);
        }
        catch (Exception ex)
        {
            // 其它异常（比如数据格式解析失败）重传也没用，还是回 "OK" 避免设备陷入无意义的死循环重传
            logger.LogError(ex, "处理考勤机 {SN} 推送的数据失败（table={Table}）", SN, table);
        }

        return Content("OK", "text/plain", Gbk);
    }

    /// <summary>心跳：设备定期来问"有没有要我做的事"，顺便把排队的命令带给它。
    /// 命令序号直接用数据库主键 Id（不是"这次心跳里的第几条"），这样设备在 /iclock/devicecmd 回执里
    /// 带回来的 ID 才能直接对应回具体是哪条命令。没确认过、且超过 CommandConfirmTimeoutMinutes 还没确认的
    /// 命令会被当成"上次没送达"重新下发；单次心跳最多带 MaxCommandsPerHeartbeat 条，多的留到下次心跳再发，
    /// 避免批量导入/批量停用时一次性命令太多让设备处理不过来。</summary>
    [HttpGet("/iclock/getrequest")]
    public async Task<IActionResult> Heartbeat([FromQuery] string? SN, CancellationToken ct)
    {
        if (!await IsKnownDeviceAsync(SN, ct))
            return Content("UNKNOWN DEVICE", "text/plain", Gbk);
        await TouchLastSeenAsync(SN!, ct);

        var retryBefore = DateTime.Now.AddMinutes(-_opt.CommandConfirmTimeoutMinutes);
        var pending = await db.ZKDeviceCommands
            .Where(c => c.SN == SN && !c.Confirmed && (c.SentAt == null || c.SentAt < retryBefore))
            .OrderBy(c => c.CreatedAt)
            .Take(_opt.MaxCommandsPerHeartbeat)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return Content("OK", "text/plain", Gbk);

        var sb = new StringBuilder();
        foreach (var cmd in pending)
        {
            sb.Append("C:").Append(cmd.Id).Append(':').Append(cmd.CommandText).Append("\r\n\r\n");
            cmd.Sent   = true;
            cmd.SentAt = DateTime.Now;
        }
        await db.SaveChangesAsync(ct);

        // 命令里可能带中文姓名（DATA UPDATE USERINFO 的 Name 字段），必须用设备协议实际用的 GBK 编码返回，
        // 用 ASCII 的话中文字节会被替换成 '?'，设备上显示的姓名就会变成一串问号
        return Content(sb.ToString(), "text/plain", Gbk);
    }

    /// <summary>备用心跳：设备上传大量数据时改发这个，服务器只能回 OK，不能夹带命令。</summary>
    [HttpGet("/iclock/ping")]
    public IActionResult Ping() => Content("OK", "text/plain", Gbk);

    /// <summary>
    /// 设备执行完命令后，回报执行结果——按 ID（心跳下发时用的就是 ZKDeviceCommand.Id）把命令标记为已确认，
    /// 只有 Return=0 才算真正执行成功。回执 body 具体格式目前没有官方文档确认，这里按通用参考实现处理
    /// （每行一条 "ID=x&Return=y&..." 这样用 & 连接的 key=value），部署后需要拿真实设备的日志核对格式
    /// 是否对得上——如果对不上，只用改 <see cref="ParseDeviceCmdResult"/> 这一个方法，不影响其它部分。
    /// </summary>
    [HttpPost("/iclock/devicecmd")]
    public async Task<IActionResult> DeviceCmd([FromQuery] string? SN, CancellationToken ct)
    {
        if (!await IsKnownDeviceAsync(SN, ct))
            return Content("UNKNOWN DEVICE", "text/plain", Gbk);
        await TouchLastSeenAsync(SN!, ct);

        var bodyBytes = await ReadBodyBytesAsync();
        var text = Gbk.GetString(bodyBytes);
        logger.LogInformation("考勤机 {SN} 命令执行结果：{Result}", SN, text);

        try
        {
            var confirmedIds = ParseDeviceCmdResult(text);
            if (confirmedIds.Count > 0)
            {
                await db.ZKDeviceCommands
                    .Where(c => confirmedIds.Contains(c.Id))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.Confirmed, true)
                        .SetProperty(c => c.ConfirmedAt, DateTime.Now), ct);
            }
        }
        catch (Exception ex)
        {
            // 解析不出来就按"没确认"处理，命令会在超时后自动重新下发，不会因为解析失败而卡住整个流程
            logger.LogWarning(ex, "解析考勤机 {SN} 的命令执行回执失败", SN);
        }

        return Content("OK", "text/plain", Gbk);
    }

    /// <summary>从回执文本里挑出 Return=0（执行成功）的那些行，取出对应的命令 ID。</summary>
    private static List<int> ParseDeviceCmdResult(string text)
    {
        var ids = new List<int>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('&')
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

            if (fields.TryGetValue("ID", out var idStr) && int.TryParse(idStr, out var id) &&
                fields.TryGetValue("Return", out var ret) && ret == "0")
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// 读原始请求体字节。这里特意先 EnableBuffering 再从头读，不能直接读 Request.Body——
    /// 如果设备这次请求带的 Content-Type 恰好是 application/x-www-form-urlencoded 之类的表单类型，
    /// ASP.NET Core 框架自己会在进到这个方法之前就把请求体当表单读掉一次，直接读 Request.Body
    /// 会读到空的，导致这次上传的数据被无声丢掉（只记"0 条"，不会报错，很难发现）。
    /// </summary>
    private async Task<byte[]> ReadBodyBytesAsync()
    {
        Request.EnableBuffering();
        Request.Body.Position = 0;
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 解析 ATTLOG 文本：每行一条记录，制表符分隔，字段顺序 PIN、Time、Status、Verify（按标准
    /// PUSH/ADMS 协议的通用格式）。如果拿到完整版协议文档后发现字段顺序不一样，改这里就行，
    /// 不影响其它部分。
    /// </summary>
    private static List<ZKAttLogRow> ParseAttLog(string text)
    {
        var rows = new List<ZKAttLogRow>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 2) continue;
            var pin = fields[0].Trim();
            if (string.IsNullOrEmpty(pin)) continue;
            if (!DateTime.TryParse(fields[1].Trim(), out var time)) continue;
            var status = fields.Length > 2 && int.TryParse(fields[2].Trim(), out var s) ? s : 0;
            var verify = fields.Length > 3 && int.TryParse(fields[3].Trim(), out var v) ? v : 0;
            rows.Add(new ZKAttLogRow(pin, time, status, verify));
        }
        return rows;
    }

    /// <summary>
    /// 保存考勤照片：ATTPHOTO 请求体前半段是 "key=value" 的说明头（用 \n 分隔，直到 "CMD=uploadphoto"
    /// 这个标记），说明头之后紧跟的是照片本身的原始二进制字节，长度由头里的 size 字段给出。
    /// </summary>
    private async Task SaveAttPhotoAsync(string sn, byte[] bodyBytes)
    {
        var headEnd = IndexOfMarker(bodyBytes, "CMD=uploadphoto"u8.ToArray());
        if (headEnd < 0) { logger.LogWarning("考勤机 {SN} 上传的考勤照片格式不识别，未找到 CMD=uploadphoto 标记", sn); return; }

        var headText = Gbk.GetString(bodyBytes, 0, headEnd);
        var head = headText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

        if (!head.TryGetValue("Size", out var sizeStr) && !head.TryGetValue("size", out sizeStr))
        {
            logger.LogWarning("考勤机 {SN} 上传的考勤照片缺少 size 字段", sn);
            return;
        }
        if (!int.TryParse(sizeStr, out var photoSize) || photoSize <= 0 || photoSize > bodyBytes.Length)
        {
            logger.LogWarning("考勤机 {SN} 上传的考勤照片 size 字段不合法：{Size}", sn, sizeStr);
            return;
        }

        var photoBytes = bodyBytes[^photoSize..];   // 照片是整个请求体末尾的 size 个字节

        var uploadPath = appOptions.Value.UploadPath.Trim('/', '\\');
        var webRoot    = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var dir        = Path.Combine(webRoot, uploadPath, "zkdevice", DateTime.Today.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);
        var fileName = $"{sn}_{DateTime.Now:HHmmss}_{Guid.NewGuid():N}.jpg";
        await System.IO.File.WriteAllBytesAsync(Path.Combine(dir, fileName), photoBytes);
    }

    private static int IndexOfMarker(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
