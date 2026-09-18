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

    public async Task OnGetAsync() => await LoadStateAsync();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            // 权限和人脸信息都要重新从数据库查一遍，不能信页面上带回来的状态
            var user = await db.Users.FindAsync(CurrentUserId)
                ?? throw new InvalidOperationException("账号不存在");
            if (!user.AllowRemotePunch)
                throw new InvalidOperationException("您暂未开通远程打卡权限，请联系管理员");
            if (string.IsNullOrEmpty(user.FaceReferencePhotoUrl))
                throw new InvalidOperationException("请先到「人脸信息」页录入参考照片再使用远程打卡");

            // 限流：最近一段时间失败次数太多就先挡住，防止拿别人照片反复试/刷阿里云调用量。
            // 被成本闸门拦截的行会打上 BlockedReason 标记，这里要排除掉——不然"被闸门拦一次"
            // 会变成"算一次失败"，反而更容易触发这个限流，形成连锁反应。
            var windowStart = DateTime.Now.AddMinutes(-faceOptions.Value.AttemptWindowMinutes);
            var recentFailures = await db.FaceVerifyAttempts.CountAsync(a =>
                a.UserId == CurrentUserId && !a.Success && a.BlockedReason == null && a.CreatedAt >= windowStart);
            if (recentFailures >= faceOptions.Value.MaxAttemptsPerWindow)
                throw new InvalidOperationException(
                    $"识别失败次数过多，请 {faceOptions.Value.AttemptWindowMinutes} 分钟后再试，或联系管理员改用补卡申请");

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

            // 成本闸门：跟上面的失败限流是两回事（那个防冒充，这个控成本/防刷）——挡"手快连点"
            // 和正常员工也不该出现的异常高频打卡。命中这里直接拒绝，不产生任何（付费的）阿里云调用；
            // 拦截行为会写一条 BlockedReason 记录（供以后做统计看板用），但查上面失败限流、
            // 和下面"今日次数"时都会排除这类行，不会互相影响。
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
                    throw new InvalidOperationException("刚刚已打卡成功，无需重复打卡");
                }
            }

            // "今日次数"这两道闸门也要放过当天还没打的这一次下班卡——不然员工早上/中午多试了几次
            // （光线不好、角度不对）攒够了次数，到真正该下班打卡的时候反而被这里挡住，只能走管理员
            // 手动补卡，等于成本闸门制造出新的"缺卡"记录，跟这道闸门本身的目的（省钱）背道而驰
            // （发现于 2026-09-18：把每日上限从 20/40 收紧到 6/12 之后，这个场景变得容易触发）。
            var todayStart = DateTime.Today;
            if (!isComplementaryClockOut)
            {
                var todayAttempts = await db.FaceVerifyAttempts.CountAsync(a =>
                    a.UserId == CurrentUserId && a.BlockedReason == null && a.CreatedAt >= todayStart);
                if (todayAttempts >= faceOptions.Value.MaxAttemptsPerDay)
                {
                    await LogBlockedAsync("今日尝试次数上限");
                    throw new InvalidOperationException("今日远程打卡尝试次数已达上限，请联系管理员");
                }

                var todaySuccesses = await db.FaceVerifyAttempts.CountAsync(a =>
                    a.UserId == CurrentUserId && a.Success && a.BlockedReason == null && a.CreatedAt >= todayStart);
                if (todaySuccesses >= faceOptions.Value.MaxSuccessfulVerificationsPerDay)
                {
                    await LogBlockedAsync("今日成功次数上限");
                    throw new InvalidOperationException("今日远程打卡次数已达上限，确有特殊情况请联系管理员或走补卡申请");
                }
            }

            if (!Latitude.HasValue || !Longitude.HasValue)
                throw new InvalidOperationException("未能获取定位，请检查浏览器定位权限后重试");

            // 先定位、再人脸：如果所在考勤组配置了允许打卡的地点，要先确认人在范围内，
            // 不在范围内就直接拒绝，不用再去调（付费的）阿里云人脸识别接口
            var (locationValid, locationMessage) =
                await attendanceService.ValidateLocationAsync(user.AttendanceGroupId, Latitude, Longitude, Accuracy);
            if (!locationValid)
                throw new InvalidOperationException(locationMessage ?? "打卡位置不在允许范围内");

            if (string.IsNullOrWhiteSpace(CapturedPhotoData))
                throw new InvalidOperationException("未能拍到人脸画面，请确认摄像头已开启后重试");
            if (CapturedPhotoData.Length > MaxCapturedPhotoDataLength)
                throw new InvalidOperationException("拍摄的照片数据过大，请重试");

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
                throw new InvalidOperationException("拍摄的照片数据不完整，请重试");
            }

            var refPath = Path.Combine(PrivateFileStorage.GetRoot(env), user.FaceReferencePhotoUrl!.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            // 优先用专门给比对用的瘦身版（"_verify" 后缀，体积只有留档版的三四分之一，每次打卡都传，越小越好）；
            // 老账号在这次改动上线前录入的参考照片没有这个瘦身版文件，这时退回用留档版，兼容历史数据。
            var verifyPath = Path.Combine(Path.GetDirectoryName(refPath)!,
                $"{Path.GetFileNameWithoutExtension(refPath)}_verify{Path.GetExtension(refPath)}");
            var actualRefPath = System.IO.File.Exists(verifyPath) ? verifyPath : refPath;
            if (!System.IO.File.Exists(actualRefPath))
                throw new InvalidOperationException("参考照片文件缺失，请重新到「人脸信息」页录入");
            var refBytes = await System.IO.File.ReadAllBytesAsync(actualRefPath, ct);

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
                // 不拼接 ex.Message：AliyunFaceApiException/InvalidOperationException 本身已经是给用户看的
                // 中文提示，完整异常详情已经记进上面的日志，不需要在页面上再展示一遍原始报错文本。
                throw new InvalidOperationException("人脸识别服务暂时不可用，请稍后重试");
            }

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
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadStateAsync();
        return Page();
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
