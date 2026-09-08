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
    IOptions<AppSettingsOptions> appOptions) : AppPageModel
{
    [BindProperty] public double? Latitude  { get; set; }
    [BindProperty] public double? Longitude { get; set; }

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

    public async Task<IActionResult> OnPostAsync()
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

            // 限流：最近一段时间失败次数太多就先挡住，防止拿别人照片反复试/刷阿里云调用量
            var windowStart = DateTime.Now.AddMinutes(-faceOptions.Value.AttemptWindowMinutes);
            var recentFailures = await db.FaceVerifyAttempts.CountAsync(a =>
                a.UserId == CurrentUserId && !a.Success && a.CreatedAt >= windowStart);
            if (recentFailures >= faceOptions.Value.MaxAttemptsPerWindow)
                throw new InvalidOperationException(
                    $"识别失败次数过多，请 {faceOptions.Value.AttemptWindowMinutes} 分钟后再试，或联系管理员改用补卡申请");

            if (!Latitude.HasValue || !Longitude.HasValue)
                throw new InvalidOperationException("未能获取定位，请检查浏览器定位权限后重试");

            // 先定位、再人脸：如果所在考勤组配置了允许打卡的地点，要先确认人在范围内，
            // 不在范围内就直接拒绝，不用再去调（付费的）阿里云人脸识别接口
            var (locationValid, locationMessage) =
                await attendanceService.ValidateLocationAsync(user.AttendanceGroupId, Latitude, Longitude);
            if (!locationValid)
                throw new InvalidOperationException(locationMessage ?? "打卡位置不在允许范围内");

            if (string.IsNullOrWhiteSpace(CapturedPhotoData))
                throw new InvalidOperationException("未能拍到人脸画面，请确认摄像头已开启后重试");
            if (CapturedPhotoData.Length > MaxCapturedPhotoDataLength)
                throw new InvalidOperationException("拍摄的照片数据过大，请重试");

            // 不用员工手选上班/下班，系统按"今天打过上班卡没有"自动判断：
            // 还没打过 → 算上班；已经打过 → 算下班（下班之后还能反复再打，PunchAsync 里下班卡
            // 本来就是"每次都覆盖成最新时间"，所以最后一次打卡的时间点会成为最终的下班时间）
            var todayBeforePunch = await attendanceService.GetTodayAttendanceAsync(CurrentUserId);
            var type = todayBeforePunch?.ClockInTime is null ? PunchType.ClockIn : PunchType.ClockOut;

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
            if (!System.IO.File.Exists(refPath))
                throw new InvalidOperationException("参考照片文件缺失，请重新到「人脸信息」页录入");
            var refBytes = await System.IO.File.ReadAllBytesAsync(refPath);

            FaceVerifyResult result;
            try
            {
                result = await faceClient.VerifyAsync(refBytes, liveBytes);
            }
            catch (Exception ex) when (ex is AliyunFaceApiException or InvalidOperationException)
            {
                // AliyunFaceApiException=接口调用失败（网络/签名/服务端错误）；InvalidOperationException=
                // 没配置 AccessKeyId/AccessKeySecret（VerifyAsync 内部 CreateClient() 抛的，不在它自己的
                // try/catch 范围内）。这两种是"服务本身出问题"，不算员工的识别失败，不计入下面那个
                // 限流计数器（限流本意是防止有人拿别人照片反复试，不该因为阿里云接口抽风几次就把
                // 正常员工锁 10 分钟、还提示"识别失败次数过多"这种听起来像是员工自己的问题的话）。
                throw new InvalidOperationException("人脸识别服务暂时不可用，请稍后重试：" + ex.Message);
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
