using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>部门（对应数据库表 Department，支持无限级父子部门）。</summary>
[Table("Department")]
public class Department
{
    [Key] public int Id { get; set; }                       // 主键：部门唯一编号

    /// <summary>部门名称</summary>
    [Required, MaxLength(100)]
    public string DeptName { get; set; } = string.Empty;

    /// <summary>部门编码</summary>
    [MaxLength(50)]
    public string? DeptCode { get; set; }

    /// <summary>上级部门的编号（支持无限级，顶级部门此项为空）</summary>
    public int? ParentId { get; set; }

    /// <summary>所属公司 / 分子公司</summary>
    [MaxLength(100)]
    public string? CompanyName { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }                // 备注说明

    /// <summary>
    /// 长期跟随的考勤组编号（可空）：在"考勤组管理"里把本部门勾进某个考勤组后填写。
    /// 填了之后，本部门以后新入职/调入的员工都会自动归入这个考勤组。
    /// </summary>
    public int? AttendanceGroupId { get; set; }

    public bool IsActive { get; set; } = true;              // 是否启用

    /// <summary>排序号（越小越靠前，用于部门树里同级部门的排序）。</summary>
    public int SortIndex { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.Now; // 创建时间
    public DateTime UpdatedAt { get; set; } = DateTime.Now; // 最后修改时间

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("ParentId")]
    public Department? ParentDepartment { get; set; }            // 上级部门（也是一个部门）

    [ForeignKey("AttendanceGroupId")]
    public AttendanceGroup? AttendanceGroup { get; set; }         // 长期跟随的考勤组

    public ICollection<Department> ChildDepartments { get; set; } = [];  // 下属的子部门列表
    public ICollection<User>       Users            { get; set; } = [];  // 本部门下的员工列表
}
