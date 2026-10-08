using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Models.Exceptions;

namespace AttendanceSystem.Pages.Attendance;

/// <summary>
/// 远程打卡页：给出差/到不了考勤机的员工用，手机定位 + 现场人脸照片，跟员工自己录入的参考照片
/// 做 1:1 比对（阿里云人脸识别），通过才允许打卡。只有管理员单独开了"允许远程打卡"的员工能用，
/// 且要先在"人脸信息"页录过参考照片，两个条件缺一不可，避免变成绕开考勤机防代打卡的后门。
/// </summary>
[Authorize]
public class RemotePunchModel(
    AttendanceDbContext db,
    IWebHostEnvironment env,
    IAliyunFaceClient faceClient,
    IAttendanceService attendanceService,
    IOptions<AliyunFaceOptions> faceOptions,
    IOptions<AppSettingsOptions> appOptions,
    ILogger<RemotePunchModel> logger) : AppPageModel
{
    [BindProperty] public double? Latitude  { get; set; }
    [BindProperty] public double? Longitude { get; set; }
    [BindProperty] public double? Accuracy  { get; set; }   // 浏览器定位精度半径（米），页面上尽量取最好的一次

    /// <summary>页面上摄像头实时预览截的一帧，前端用 canvas.toDataURL() 编码成
    /// "data:image/jpeg;base64,xxxx" 这样的字符串传上来，不再是文件上传。</summary>
    [BindProperty] public string? CapturedPhotoData { get; set; }

    public bool AllowRemotePunch { get; set; }
    public bool HasFaceReference { get; set; }
    public AttendanceRecordDto? TodayRecord { get; set; }

    public string  Message          { get; set; } = string.Empty;
    public bool    IsSuccess        { get; set; }
    public bool    ShowMessage      { get; set; }
    public string? ErrorMessage     { get; set; }
    public bool    ShowFallbackHint { get; set; }   // 人脸识别没通过时，提示可以改走补卡申请

    /// <summary>人脸抓拍的 base64 字符串上限（约等于解码后 3.75MB 原始图片）——一帧摄像头截图正常情况下
    /// 远小于这个数，这里卡一个上限只是为了在真正调用 Convert.FromBase64String 解码（会一次性分配等大小
    /// 的内存缓冲区）之前挡掉恶意构造的超大字符串，避免有人直接绕过前端拿超大 payload 打这个接口刷内存。</summary>
    private const int MaxCapturedPhotoDataLength = 5 * 1024 * 1024;

    /// <summary>是不是一张像样的 JPEG：至少 1KB（480 宽的真实人脸照片远大于此），且以 JPEG 文件头 FF D8 开头。</summary>
    public static bool IsUsableJpeg(byte[] bytes) =>
        bytes.Length >= 1024 && bytes[0] == 0xFF && bytes[1] == 0xD8;

    public async Task OnGetAsync() => await LoadStateAsync();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            // 权限和人脸信息都要重新从数据库查一遍，不能信页面上带回来的状态
            var user = await db.Users.FindAsync(CurrentUserId)
                ?? throw new BusinessException("账号不存在");
            if (!user.AllowRemotePunch)
                throw new BusinessException("您暂未开通远程打卡权限，请联系管理员");
            if (string.IsNullOrEmpty(user.FaceReferencePhotoUrl))
                throw new BusinessException("请先到「人脸信息」页录入参考照片再使用远程打卡");

            await EnsureNotRateLimitedAsync();

            var (type, todayBeforePunch) = await ResolvePunchTypeAsync();

            // 上班卡的合理性校验放在所有（付费的）人脸识别之前：夜班刚打完下班卡又点了一次、跨天班次打得离上班时间太早，
            // 直接提示原因，不生成错的上班卡，也不花识别费用（2026-09-28 线上 13 条夜班记录被这样弄乱）
            if (type == PunchType.ClockIn && await attendanceService.GetClockInRejectionAsync(CurrentUserId, DateTime.Now) is { } clockInRejection)
                throw new BusinessException(clockInRejection);

            await EnforceVerifyIntervalAsync(type, todayBeforePunch);

            // 没办入职（没填入职日期或还没到入职日）本来就打不了卡（AttendanceService.PunchAsync 会拒绝），
            // 但那一步在人脸识别之后，会白花一次付费的阿里云调用——提前判断，措辞跟 PunchAsync 一致（2026-09-24 第 11 轮审查）
            if (user.HireDate is null || user.HireDate.Value > DateOnly.FromDateTime(DateTime.Today))
                throw new BusinessException("您尚未办理入职（入职日期未设置或未到），暂不能打卡，请联系管理员");

            if (!Latitude.HasValue || !Longitude.HasValue)
                throw new BusinessException("未能获取定位，请检查浏览器定位权限后重试");

            // 先定位、再人脸：如果所在考勤组配置了允许打卡的地点，要先确认人在范围内，
            // 不在范围内就直接拒绝，不用再去调（付费的）阿里云人脸识别接口
            var (locationValid, locationMessage) =
                await attendanceService.ValidateLocationAsync(user.AttendanceGroupId, Latitude, Longitude, Accuracy);
            if (!locationValid)
                throw new BusinessException(locationMessage ?? "打卡位置不在允许范围内");

            var liveBytes = DecodeCapturedPhoto();

            var refBytes = await LoadReferencePhotoAsync(user, ct);

            var result = await VerifyFaceAsync(refBytes, liveBytes, ct);

            // 这里记的是"人脸是否比对成功"，不是"打卡是否成功"——下面即使 PunchAsync 因为业务原因
            // （比如重复打卡）失败，人脸比对本身成功这个事实也不会变，两者是两码事，别看名字像混着看
            await LogAttemptAsync(result.IsMatch, result.IsMatch ? null : result.FailReason);

            if (!result.IsMatch)
            {
                ErrorMessage     = result.FailReason ?? "人脸识别未通过";
                ShowFallbackHint = true;
            }
            else
            {
                await SaveAttemptPhotoAsync(liveBytes);

                var punchResult = await attendanceService.PunchAsync(CurrentUserId, new PunchRequestDto
                {
                    PunchType  = type,
                    Latitude   = Latitude,
                    Longitude  = Longitude,
                    Accuracy   = Accuracy,
                    DeviceInfo = "MobileFace"
                }, skipLocationCheck: true);

                IsSuccess   = punchResult.Success;
                Message     = punchResult.Message;
                ShowMessage = true;
                if (!IsSuccess) ErrorMessage = punchResult.Message;
            }
        }
        catch (InvalidOperationException ex)
        {
            // 本文件（以及它调用的 AttendanceService/AliyunFaceClient）里 InvalidOperationException
            // 的 Message 都是特意写好给员工看的中文提示，可以直接显示
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            // 其它类型是内部框架异常（比如 PunchAsync 重试耗尽后抛出的 DbUpdateException），
            // 不能把原始报错文本直接显示给员工——真实异常已经在 AttendanceService.PunchAsync 里
            // 记了日志，这里只给统一的友好文案（2026-09-21 代码审查发现：以前这里不分青红皂白
            // 把 ex.Message 显示给员工，而 PunchAsync 当时完全没有日志，出问题只能靠员工截图报错）
            logger.LogError(ex, "远程打卡失败，UserId={UserId}", CurrentUserId);
            ErrorMessage = AttendanceSystem.Helpers.ErrorReport.Describe(ex, "打卡失败，请稍后重试或联系管理员", HttpContext);
        }

        await LoadStateAsync();
        return Page();
    }

    /// <summary>限流：最近一段时间失败次数太多就先挡住（详细说明见方法体）。</summary>
    private async Task EnsureNotRateLimitedAsync()
    {
        // 限流：最近一段时间失败次数太多就先挡住，防止拿别人照片反复试/刷阿里云调用量。
        // 被成本闸门拦截的行会打上 BlockedReason 标记，这里要排除掉——不然"被闸门拦一次"
        // 会变成"算一次失败"，反而更容易触发这个限流，形成连锁反应。
        var windowStart = DateTime.Now.AddMinutes(-faceOptions.Value.AttemptWindowMinutes);
        var recentFailures = await db.FaceVerifyAttempts.CountAsync(a =>
            a.UserId == CurrentUserId && !a.Success && a.BlockedReason == null && a.CreatedAt >= windowStart);
        if (recentFailures >= faceOptions.Value.MaxAttemptsPerWindow)
            throw new BusinessException(
                $"识别失败次数过多，请 {faceOptions.Value.AttemptWindowMinutes} 分钟后再试，或联系管理员改用补卡申请");
    }

    /// <summary>
    /// 不用员工手选上班/下班，系统按今天的打卡情况自动判断；返回判断出的类型和"打卡前"的今日记录。详细口径见方法体里的注释。
    /// </summary>
    private async Task<(PunchType Type, AttendanceRecordDto? TodayBeforePunch)> ResolvePunchTypeAsync()
    {
        // 不用员工手选上班/下班，系统按"今天打过上班卡没有"自动判断：还没打过 → 算上班；
        // 已经打过、且离排班的应下班时间够近了 → 算下班；已经打过上班卡、但离下班还早的（比如
        // 午休期间又打了一次），算"午间打卡"，不碰下班时间和状态——不然像考勤机同步那边一样，
        // 员工中午随手打一次卡就会被当成"下班"，账号上临时显示一段"早退"（同一个 bug 之前
        // 在考勤机同步那边修过，这里是同一个道理，见 AttendanceService.IsEligibleClockOutCandidate）。
        // 没排班时不知道应下班时间，只能按老办法直接当下班。
        // 提到成本闸门前面算，是因为下面的"两次打卡间隔"要用到——上班卡刚打完马上接着打下班卡
        // （夜班跨天很常见）不该被当成"重复打卡"拦下来。
        var todayBeforePunch = await attendanceService.GetTodayAttendanceAsync(CurrentUserId);
        var today = DateOnly.FromDateTime(DateTime.Today);
        // 夜班跨天打卡：GetTodayAttendanceAsync 在"今天"还没有记录、但"昨天"排的是跨天班次且还没
        // 打下班卡时，会续上昨天那条记录（WorkDate 仍是昨天）——这里判断"离下班时间够不够近"也要
        // 用同一个 workDate 去查排班，不然凌晨查"今天"的排班表查不到（跨天班次记在昨天），
        // 会被当成"没排班"直接判定成下班，跟考勤机同步那边刚修过的是同一个 bug。
        var workDate = todayBeforePunch?.WorkDate ?? today;
        var todayShift = (await attendanceService.GetShiftAssignmentAsync(CurrentUserId, workDate))?.ShiftSchedule;
        var type = todayBeforePunch?.ClockInTime is null ? PunchType.ClockIn
            : AttendanceService.IsEligibleClockOutCandidate(DateTime.Now, workDate, todayShift) ? PunchType.ClockOut
            : PunchType.MidCheck;
        return (type, todayBeforePunch);
    }

    /// <summary>成本闸门：防"手快连点"造成的重复付费调用；命中就拒绝并记一条拦截记录。</summary>
    private async Task EnforceVerifyIntervalAsync(PunchType type, AttendanceRecordDto? todayBeforePunch)
    {
        // 成本闸门：跟上面的失败限流是两回事（那个防冒充，这个防"手快连点"造成的重复付费调用）。
        // 命中这里直接拒绝，不产生任何（付费的）阿里云调用；拦截行为会写一条 BlockedReason 记录
        // （供以后做统计看板用），查上面失败限流时会排除这类行，不会互相影响。
        // 每日次数上限已取消（2026-09-21 按业务要求，人脸识别打卡不再限制每人每天的次数）。
        var isComplementaryClockOut = type == PunchType.ClockOut && todayBeforePunch?.ClockOutTime is null;
        if (!isComplementaryClockOut)
        {
            var lastSuccessAt = await db.FaceVerifyAttempts
                .Where(a => a.UserId == CurrentUserId && a.Success)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => (DateTime?)a.CreatedAt)
                .FirstOrDefaultAsync();
            if (lastSuccessAt.HasValue && (DateTime.Now - lastSuccessAt.Value).TotalSeconds < faceOptions.Value.MinSecondsBetweenVerifications)
            {
                await LogBlockedAsync("间隔未到");
                // 措辞不能断言"已打卡成功"——这里判断的只是"上次人脸比对成功"，如果那次比对成功后
                // 打卡本身其实失败了（比如撞上了别的业务规则），这句话就会跟事实不符
                throw new BusinessException($"距离上次识别未满 {faceOptions.Value.MinSecondsBetweenVerifications} 秒，请稍后再试");
            }
        }
    }

    /// <summary>把前端传来的抓拍 Data URL 解码成 JPEG 字节，并拦掉空数据、超大数据和非 JPEG。</summary>
    private byte[] DecodeCapturedPhoto()
    {
        if (string.IsNullOrWhiteSpace(CapturedPhotoData))
            throw new BusinessException("未能拍到人脸画面，请确认摄像头已开启后重试");
        if (CapturedPhotoData.Length > MaxCapturedPhotoDataLength)
            throw new BusinessException("拍摄的照片数据过大，请重试");

        byte[] liveBytes;
        try
        {
            // 前端传的是 "data:image/jpeg;base64,xxxx" 这样的 Data URL，逗号前面是描述头，取逗号后面才是真正的图片数据
            var comma = CapturedPhotoData.IndexOf(',');
            var base64 = comma >= 0 ? CapturedPhotoData[(comma + 1)..] : CapturedPhotoData;
            liveBytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new BusinessException("拍摄的照片数据不完整，请重试");
        }

        // 空数据/非 JPEG 直接拒绝，不去调（付费的）阿里云：摄像头还没出画面就点打卡时，前端截到的是 0×0 画布，
        // toDataURL 返回 "data:,"，解码出来是 0 字节——以前这种情况会原样传给阿里云，被报"图片无法下载"并白白重试 3 次
        // （2026-10-04 阿里云确认服务端收到的图片是空白的）
        if (!IsUsableJpeg(liveBytes))
            throw new BusinessException("没有拍到有效的画面（摄像头可能还没出画面），请等预览画面出现后再点打卡；如果一直没有画面，请刷新页面并允许摄像头权限后重试");
        return liveBytes;
    }

    /// <summary>读取参考照片：优先用瘦身版（_verify），老账号没有就退回留档版。</summary>
    private async Task<byte[]> LoadReferencePhotoAsync(User user, CancellationToken ct)
    {
        var refPath = Path.Combine(PrivateFileStorage.GetRoot(env), user.FaceReferencePhotoUrl!.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        // 优先用专门给比对用的瘦身版（"_verify" 后缀，体积只有留档版的三四分之一，每次打卡都传，越小越好）；
        // 老账号在这次改动上线前录入的参考照片没有这个瘦身版文件，这时退回用留档版，兼容历史数据。
        var verifyPath = Path.Combine(Path.GetDirectoryName(refPath)!,
            $"{Path.GetFileNameWithoutExtension(refPath)}_verify{Path.GetExtension(refPath)}");
        var actualRefPath = System.IO.File.Exists(verifyPath) ? verifyPath : refPath;
        if (!System.IO.File.Exists(actualRefPath))
            throw new BusinessException("参考照片文件缺失，请重新到「人脸信息」页录入");
        var refBytes = await System.IO.File.ReadAllBytesAsync(actualRefPath, ct);
        return refBytes;
    }

    /// <summary>调阿里云人脸比对；服务本身出问题（不是员工识别失败）时记日志、给"改走补卡"的出口，并抛出带错误编号的业务提示。</summary>
    private async Task<FaceVerifyResult> VerifyFaceAsync(byte[] refBytes, byte[] liveBytes, CancellationToken ct)
    {
        FaceVerifyResult result;
        try
        {
            result = await faceClient.VerifyAsync(refBytes, liveBytes, ct);
        }
        catch (Exception ex) when (ex is AliyunFaceApiException or InvalidOperationException)
        {
            // AliyunFaceApiException=接口调用失败（网络/签名/服务端错误，AliyunFaceClient 内部已经按
            // 超时/重试/熔断处理过，走到这里说明确实是服务本身有问题）；InvalidOperationException=
            // 没配置 AccessKeyId/AccessKeySecret（VerifyAsync 内部 CreateClient() 抛的，不在它自己的
            // try/catch 范围内）。这两种是"服务本身出问题"，不算员工的识别失败，不计入下面那个
            // 限流计数器（限流本意是防止有人拿别人照片反复试，不该因为阿里云接口抽风几次就把
            // 正常员工锁 10 分钟、还提示"识别失败次数过多"这种听起来像是员工自己的问题的话）。
            // 这类失败不会写进 FaceVerifyAttempt 表（本意如此，见上），所以必须在这里单独记一条日志，
            // 不然只能靠员工截图报错，服务器这边完全查不到发生过什么。
            logger.LogWarning(ex, "远程打卡人脸识别服务调用失败，UserId={UserId}", CurrentUserId);
            ShowFallbackHint = true;   // 服务本身出问题时，也该给员工"改走补卡申请"的出口，不只是识别没通过才给
            // 给员工看的是"具体原因（Aliyun 异常自带的中文说明）+ 错误编号"，不是笼统的"暂时不可用"：
            // AliyunFaceApiException 的 Message 本来就是不含 SDK 原文的中文类别说明（超时/熔断/接口调用失败……）；
            // 没配置密钥这类 InvalidOperationException 只说"服务未配置完整"。完整异常按同一个编号记日志。
            var reason = ex is AliyunFaceApiException ? ex.Message : "人脸识别服务没有配置完整，请联系管理员";
            throw new BusinessException(
                AttendanceSystem.Helpers.ErrorReport.Describe(ex, $"人脸识别服务暂时不可用：{reason}", HttpContext));
        }
        return result;
    }

    private async Task LoadStateAsync()
    {
        var user = await db.Users.Where(u => u.Id == CurrentUserId)
            .Select(u => new { u.AllowRemotePunch, u.FaceReferencePhotoUrl })
            .FirstOrDefaultAsync();

        AllowRemotePunch = user?.AllowRemotePunch ?? false;
        HasFaceReference = !string.IsNullOrEmpty(user?.FaceReferencePhotoUrl);

        if (AllowRemotePunch && HasFaceReference)
            TodayRecord = await attendanceService.GetTodayAttendanceAsync(CurrentUserId);
    }

    private Task LogAttemptAsync(bool success, string? failReason)
    {
        db.FaceVerifyAttempts.Add(new FaceVerifyAttempt
        {
            UserId     = CurrentUserId,
            Success    = success,
            FailReason = failReason
        });
        return db.SaveChangesAsync();
    }

    /// <summary>记一条"被成本闸门拦截"的行——不是真的调了阿里云接口，Success 恒为 false，
    /// 靠 BlockedReason 这一列跟"真失败"（FailReason 非空、BlockedReason 为空）区分开，
    /// 查失败限流/今日次数这些统计时都要排除掉这类行，见上面调用处的说明。</summary>
    private Task LogBlockedAsync(string reason)
    {
        logger.LogInformation("远程打卡被成本闸门拦截（{Reason}），UserId={UserId}", reason, CurrentUserId);
        db.FaceVerifyAttempts.Add(new FaceVerifyAttempt
        {
            UserId        = CurrentUserId,
            Success       = false,
            BlockedReason = reason
        });
        return db.SaveChangesAsync();
    }

    /// <summary>现场照片留痕，按日期分文件夹存，跟考勤机 ATTPHOTO 那套清理逻辑用的是同一个模式，
    /// 每天 03:00 的后台清理任务会顺带把这里超过 30 天的也清掉。</summary>
    private async Task SaveAttemptPhotoAsync(byte[] liveBytes)
    {
        var uploadPath = appOptions.Value.UploadPath.Trim('/', '\\');
        var dir        = Path.Combine(PrivateFileStorage.GetRoot(env), uploadPath, "faces", "attempts", DateTime.Today.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);
        var fileName = $"{CurrentUserId}_{DateTime.Now:HHmmss}_{Guid.NewGuid():N}.jpg";
        await System.IO.File.WriteAllBytesAsync(Path.Combine(dir, fileName), liveBytes);
    }
}
