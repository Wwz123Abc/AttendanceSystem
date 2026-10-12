namespace AttendanceSystem.Models.DTOs;

/// <summary>总审批记录页的"审批进度"：还没审批完的申请，以及每个审批人手上压了多少。</summary>
public class ApprovalProgressDto
{
    /// <summary>每个审批人一行：现在轮到他审批的有几张、排在后面级别的有几张、最久的挂了几天。</summary>
    public List<ApproverProgressRowDto> Approvers { get; set; } = [];

    /// <summary>还没审批完的申请（待审批 / 审批中），一张一行，带上"现在该谁审"。</summary>
    public List<PendingApprovalItemDto> Items { get; set; } = [];
}

/// <summary>审批进度里的一位审批人。</summary>
public class ApproverProgressRowDto
{
    public int       ApproverUserId { get; set; }
    public string    Name           { get; set; } = "";
    public string    EmployeeNo     { get; set; } = "";
    public string    RoleText       { get; set; } = "";
    public string?   DeptName       { get; set; }
    public bool      IsActive       { get; set; }
    public DateTime? LastLoginAt    { get; set; }

    /// <summary>现在正轮到他审批的申请数（前面几级都已处理完）。</summary>
    public int CurrentCount { get; set; }

    /// <summary>他是后续级别、要等前面的人审完才轮到他的申请数。</summary>
    public int LaterCount { get; set; }

    /// <summary>"现在轮到他审批"的申请里，最早提交的那张的提交时间。</summary>
    public DateTime? OldestSubmittedAt { get; set; }
}

/// <summary>审批进度里的一张未审批完的申请。</summary>
public class PendingApprovalItemDto
{
    public int      RequestId           { get; set; }
    public string   RequestNo           { get; set; } = "";
    public string   TypeText            { get; set; } = "";
    public string   StatusText          { get; set; } = "";
    public string   ApplicantName       { get; set; } = "";
    public string   ApplicantEmployeeNo { get; set; } = "";
    public string?  DeptName            { get; set; }
    public string   ContentText         { get; set; } = "";
    public string   Reason              { get; set; } = "";
    public DateTime SubmittedAt         { get; set; }

    /// <summary>现在该谁审（当前待处理的最靠前一级）；理论上一定有，没有时留空。</summary>
    public int?   CurrentApproverId   { get; set; }
    public string CurrentApproverName { get; set; } = "";
    public string CurrentApproverNo   { get; set; } = "";

    /// <summary>排在后面的审批人（二级审批时才有），姓名用顿号隔开。</summary>
    public string LaterApprovers { get; set; } = "";
}
