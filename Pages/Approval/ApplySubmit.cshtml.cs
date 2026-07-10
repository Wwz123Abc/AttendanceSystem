using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Approval;

/// <summary>提交申请页：填写并提交补卡/请假/加班申请（可传附件），并显示我已提交的申请列表。</summary>
[Authorize]
public class ApplySubmitModel(
    IApprovalService approvalService,
    IWebHostEnvironment env,                    // 用来定位 wwwroot 目录存附件
    IOptions<AppSettingsOptions> appOptions) : AppPageModel
{
    public List<ApprovalRequestDto> MyApplications { get; set; } = [];
    /// <summary>我可以选的审批人名单（取自我所在考勤组的配置）；为空表示没配置，提交后自动退回直属上级</summary>
    public List<ApproverOptionDto> ApproverOptions { get; set; } = [];
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage   { get; set; }
    /// <summary>部分附件因超大/类型不支持被跳过时的提示（不算失败，随成功消息一起显示）。</summary>
    public string? AttachmentWarning { get; set; }

    // 表单字段（全用字符串接收，提交后再转成对应类型）
    [BindProperty] public string  ApprovalType  { get; set; } = "Leave";   // 申请类型
    [BindProperty] public int?    ApproverUserId { get; set; }             // 选的审批人
    [BindProperty] public string? LeaveType     { get; set; }
    [BindProperty] public string? LeaveStart    { get; set; }
    [BindProperty] public string? LeaveEnd      { get; set; }
    [BindProperty] public string? PunchDate     { get; set; }
    [BindProperty] public string? PunchTypeVal  { get; set; }
    [BindProperty] public string? PunchTime     { get; set; }
    [BindProperty] public string? OvertimeStart { get; set; }
    [BindProperty] public string? OvertimeEnd   { get; set; }
    [BindProperty] public string? BusinessTripStart       { get; set; }
    [BindProperty] public string? BusinessTripEnd         { get; set; }
    [BindProperty] public string? BusinessTripDestination { get; set; }
    [BindProperty] public string? Reason        { get; set; }
    [BindProperty] public List<IFormFile> Attachments { get; set; } = [];  // 上传的附件文件

    /// <summary>打开页面：加载我提交过的申请、我可以选的审批人名单。</summary>
    public async Task OnGetAsync()
    {
        MyApplications  = await approvalService.GetMyApprovalsAsync(CurrentUserId);
        ApproverOptions = await approvalService.GetAvailableApproversAsync(CurrentUserId);
    }

    /// <summary>点“提交申请”时执行：存附件 → 按类型组装数据 → 提交。</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        // 审批人名单要先查出来：一是校验员工选的人在不在名单里，二是提交失败时页面要重新显示这份名单
        ApproverOptions = await approvalService.GetAvailableApproversAsync(CurrentUserId);

        try
        {
            // 校验时间是否合理（前端已经限制过选择范围，这里是服务器端的权威兜底，不能只信前端）
            var dateError = ValidateDates();
            if (dateError != null)
            {
                ErrorMessage = dateError;
                MyApplications = await approvalService.GetMyApprovalsAsync(CurrentUserId);
                return Page();
            }

            // 配了审批人名单的话，必须从名单里选一个，不能不选
            if (ApproverOptions.Count > 0 && (ApproverUserId is null || !ApproverOptions.Any(a => a.UserId == ApproverUserId)))
            {
                ErrorMessage = "请选择审批人";
                MyApplications = await approvalService.GetMyApprovalsAsync(CurrentUserId);
                return Page();
            }

            if (!Enum.TryParse<Models.Enums.ApprovalType>(ApprovalType, out var approvalType))
                throw new InvalidOperationException("请选择正确的申请类型");
            if (!string.IsNullOrWhiteSpace(Reason) && Reason.Trim().Length > 1000)
                throw new InvalidOperationException("申请理由不能超过 1000 个字");
            if (!string.IsNullOrWhiteSpace(BusinessTripDestination) && BusinessTripDestination.Trim().Length > 200)
                throw new InvalidOperationException("出差目的地不能超过 200 个字");

            // 先保存附件，拿到它们的访问地址
            var attachmentUrls = new List<string>();
            if (Attachments.Count > 0)
                attachmentUrls = await SaveAttachmentsAsync();

            var dto = new SubmitApprovalDto
            {
                ApprovalType   = approvalType,
                Reason         = Reason,
                AttachmentUrls = attachmentUrls,
                ApproverUserId = ApproverUserId
            };

            // 按申请类型，把对应的字段填进去
            if (ApprovalType == "Leave")
            {
                dto.LeaveType      = Enum.TryParse<Models.Enums.LeaveType>(LeaveType, out var lt) ? lt : Models.Enums.LeaveType.AnnualLeave;
                dto.LeaveStartTime = string.IsNullOrEmpty(LeaveStart) ? null : DateTime.Parse(LeaveStart);
                dto.LeaveEndTime   = string.IsNullOrEmpty(LeaveEnd)   ? null : DateTime.Parse(LeaveEnd);
            }
            else if (ApprovalType == "PunchReplenishment")
            {
                dto.PunchDate = string.IsNullOrEmpty(PunchDate) ? null : DateOnly.Parse(PunchDate);
                dto.PunchType = Enum.TryParse<Models.Enums.PunchType>(PunchTypeVal, out var ptv) ? ptv : null;
                dto.PunchTime = string.IsNullOrEmpty(PunchTime) ? null : TimeOnly.Parse(PunchTime);
            }
            else if (ApprovalType == "Overtime")
            {
                dto.OvertimeStartTime = string.IsNullOrEmpty(OvertimeStart) ? null : DateTime.Parse(OvertimeStart);
                dto.OvertimeEndTime   = string.IsNullOrEmpty(OvertimeEnd)   ? null : DateTime.Parse(OvertimeEnd);
            }
            else if (ApprovalType == "BusinessTrip")
            {
                dto.BusinessTripStartTime   = string.IsNullOrEmpty(BusinessTripStart) ? null : DateTime.Parse(BusinessTripStart);
                dto.BusinessTripEndTime     = string.IsNullOrEmpty(BusinessTripEnd)   ? null : DateTime.Parse(BusinessTripEnd);
                dto.BusinessTripDestination = string.IsNullOrWhiteSpace(BusinessTripDestination) ? null : BusinessTripDestination.Trim();
            }

            await approvalService.SubmitApprovalAsync(CurrentUserId, dto);
            SuccessMessage = "申请提交成功！" + (AttachmentWarning is null ? "" : $"（{AttachmentWarning}）");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"提交失败：{ex.Message}";
        }

        MyApplications = await approvalService.GetMyApprovalsAsync(CurrentUserId);   // 刷新列表
        return Page();
    }

    /// <summary>
    /// 校验各类申请的时间是否合理：
    /// 请假——开始不能选过去、结束必须晚于开始；补卡——补的是过去漏打的卡，日期不能选未来；
    /// 加班——结束必须晚于开始（加班允许事后补报，不限制“过去”）；
    /// 出差——开始不能选过去、结束必须晚于开始（出差是提前申请的，不允许补报过去的出差）。
    /// 不合理就返回一句中文提示，合理则返回 null。
    /// </summary>
    private string? ValidateDates()
    {
        if (ApprovalType == "Leave")
        {
            if (string.IsNullOrEmpty(LeaveStart) || string.IsNullOrEmpty(LeaveEnd))
                return "请选择请假的开始和结束时间";
            if (!DateTime.TryParse(LeaveStart, out var start) || !DateTime.TryParse(LeaveEnd, out var end))
                return "请假时间格式不正确";
            if (start < DateTime.Now)
                return "请假开始时间不能早于现在";
            if (end <= start)
                return "请假结束时间必须晚于开始时间";
        }
        else if (ApprovalType == "PunchReplenishment")
        {
            if (string.IsNullOrEmpty(PunchDate))
                return "请选择补卡日期";
            if (!DateOnly.TryParse(PunchDate, out var date))
                return "补卡日期格式不正确";
            if (date > DateOnly.FromDateTime(DateTime.Today))
                return "补卡日期不能晚于今天";
        }
        else if (ApprovalType == "Overtime")
        {
            if (string.IsNullOrEmpty(OvertimeStart) || string.IsNullOrEmpty(OvertimeEnd))
                return "请选择加班的开始和结束时间";
            if (!DateTime.TryParse(OvertimeStart, out var start) || !DateTime.TryParse(OvertimeEnd, out var end))
                return "加班时间格式不正确";
            if (end <= start)
                return "加班结束时间必须晚于开始时间";
        }
        else if (ApprovalType == "BusinessTrip")
        {
            if (string.IsNullOrEmpty(BusinessTripStart) || string.IsNullOrEmpty(BusinessTripEnd))
                return "请选择出差的开始和结束时间";
            if (!DateTime.TryParse(BusinessTripStart, out var start) || !DateTime.TryParse(BusinessTripEnd, out var end))
                return "出差时间格式不正确";
            if (start < DateTime.Now)
                return "出差开始时间不能早于现在";
            if (end <= start)
                return "出差结束时间必须晚于开始时间";
        }
        return null;
    }

    /// <summary>点“撤销”时执行。</summary>
    public async Task<IActionResult> OnPostCancelAsync(int id)
    {
        await approvalService.CancelApprovalAsync(CurrentUserId, id);
        return RedirectToPage();
    }

    /// <summary>附件允许的文件类型，和页面上传框的 accept 属性保持一致（防止绕过页面直接 POST 任意文件类型，比如可执行文件）。</summary>
    private static readonly string[] AllowedAttachmentExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".pdf", ".doc", ".docx", ".xls", ".xlsx"];

    /// <summary>把上传的附件存到 wwwroot 下，返回它们的访问地址列表；跳过的文件（超大/类型不对）会记下原因，最后拼进提示里。</summary>
    private async Task<List<string>> SaveAttachmentsAsync()
    {
        var uid        = CurrentUserId;
        var uploadPath = appOptions.Value.UploadPath.Trim('/', '\\');   // 上传根目录，取自配置
        var webRoot    = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var dir        = Path.Combine(webRoot, uploadPath, "approvals", uid.ToString());
        Directory.CreateDirectory(dir);   // 没有就创建目录

        var urls    = new List<string>();
        var skipped = new List<string>();
        foreach (var file in Attachments.Take(5)) // 最多存 5 个文件
        {
            if (file.Length > 10 * 1024 * 1024) { skipped.Add($"{file.FileName}（超过 10MB）"); continue; }
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedAttachmentExtensions.Contains(ext)) { skipped.Add($"{file.FileName}（不支持的文件类型）"); continue; }

            var fileName = $"{Guid.NewGuid():N}{ext}";     // 用随机名，避免重名覆盖
            var path     = Path.Combine(dir, fileName);
            await using var fs = System.IO.File.Create(path);
            await file.CopyToAsync(fs);                     // 写入文件
            urls.Add($"/{uploadPath}/approvals/{uid}/{fileName}");
        }
        if (skipped.Count > 0)
            AttachmentWarning = "以下附件未能上传，已跳过：" + string.Join("；", skipped);
        return urls;
    }
}
