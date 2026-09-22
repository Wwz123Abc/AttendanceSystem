using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>系统公告服务契约：发布、撤下、查我发布的/我能看到的。</summary>
public interface IAnnouncementService
{
    /// <summary>
    /// 发布一条公告：算出受众名单，写公告本身 + 一人一条已读记录（初始未读）+ 一人一条站内通知。
    /// publisherRole 决定范围校验规则：非管理员/文员（即班组长/主管）强制按"我的直属下属"发，
    /// 不采信页面传来的 ScopeType/ScopeId，防止越权发给不该管的人。
    /// </summary>
    Task<Announcement> PublishAsync(int publisherUserId, UserRole publisherRole, PublishAnnouncementDto dto);

    /// <summary>撤下一条公告（软删除）。只有发布人自己，或管理范围覆盖这条公告发布范围的管理员/文员，才能撤——
    /// 光是管理员/文员身份不够，范围外的（比如别的分公司、或总部"全公司"范围）不行。
    /// <paramref name="managerVisibleDeptIds"/> 为 null 表示总部超级管理员，不受限。</summary>
    Task<bool> WithdrawAsync(int operatorUserId, bool isManager, int announcementId, HashSet<int>? managerVisibleDeptIds = null);

    /// <summary>查"我能看到"的公告栏列表（我在受众名单里、且这条公告仍有效），按发布时间倒序。</summary>
    Task<List<AnnouncementBoardItemDto>> GetBoardForUserAsync(int userId);

    /// <summary>把某条公告标记为"我已读"（已经读过的再点一次不会重复处理）。</summary>
    Task MarkReadAsync(int userId, int announcementId);

    /// <summary>查"我发布过"的公告列表（含已读/未读人数统计），按发布时间倒序。</summary>
    Task<List<AnnouncementPublishedItemDto>> GetMyPublishedAsync(int publisherUserId);

    /// <summary>
    /// 查某条公告的已读明细（谁读了谁没读）。只有发布人自己，或管理范围覆盖这条公告的管理员/文员，才能查；
    /// 查不到公告、或没权限查，返回 null。<paramref name="managerVisibleDeptIds"/> 语义同 <see cref="WithdrawAsync"/>。
    /// </summary>
    Task<List<AnnouncementReadDetailDto>?> GetReadDetailAsync(int operatorUserId, bool isManager, int announcementId, HashSet<int>? managerVisibleDeptIds = null);

    /// <summary>数一下"我的直属下属"有多少人，班组长/主管打开发布页时用来提示"将发给 N 人"。</summary>
    Task<int> CountDirectReportsAsync(int userId);

    /// <summary>给"发布公告"表单准备"选部门"下拉数据（按层级缩进展示）。<paramref name="visibleDeptIds"/>
    /// 为 null 表示不受限（返回全公司部门）；非 null 时只返回落在这个范围内的部门——范围裁剪下沉到
    /// 服务层，不能只靠调用方（目前唯一调用点 Publish.cshtml.cs）自己记得过滤，以防将来新增调用方漏裁剪
    /// （2026-09-21，见 docs/项目审查与问题总表.md §8-15）。</summary>
    Task<List<AnnouncementScopeOptionDto>> GetDepartmentOptionsAsync(HashSet<int>? visibleDeptIds = null);

    /// <summary>给"发布公告"表单准备"选考勤组"下拉数据。<paramref name="visibleDeptIds"/> 为 null 表示
    /// 不受限；非 null 时只保留"关联部门都落在这个范围内（All，不是 Any）或完全没关联部门（全公司通用组）"
    /// 的考勤组——跟 IsAnnouncementInScopeAsync/发布时的服务端校验同一套口径，不然会出现"下拉框能选中、
    /// 提交却被拒"的体验问题（2026-09-21，见 docs/项目审查与问题总表.md §8-15）。</summary>
    Task<List<AnnouncementScopeOptionDto>> GetAttendanceGroupOptionsAsync(HashSet<int>? visibleDeptIds = null);
}
