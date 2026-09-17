using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>部门管理页：单页树形表格，支持增删改、批量删除、添加子部门。分公司管理员只能在自己的
/// 管理范围内新建/编辑子部门，看不到、动不了其他分公司或总部顶层的部门。</summary>
[Authorize(Policy = "ManagePolicy")]
public class DepartmentManageModel(AttendanceDbContext db, IDeptScopeService deptScopeService) : PageModel
{
    /// <summary>树形展开后的扁平行（已按父子顺序排好，带层级深度）。</summary>
    public List<DeptRow> Rows { get; set; } = [];
    /// <summary>所有部门（供“上级部门”下拉）。</summary>
    public List<Department> AllDepts { get; set; } = [];

    public string? SuccessMessage { get; set; }
    public string? ErrorMessage   { get; set; }

    // 表单绑定
    [BindProperty] public int     EditId    { get; set; }
    [BindProperty] public string  DeptName  { get; set; } = string.Empty;
    [BindProperty] public int?    ParentId  { get; set; }
    [BindProperty] public int     SortIndex { get; set; }
    [BindProperty] public bool    IsActive  { get; set; } = true;
    [BindProperty] public string? DeleteIds { get; set; }   // 逗号分隔的待删除部门 id

    /// <summary>一行部门数据：部门本身 + 层级深度 + 成员数 + 是否有子部门。</summary>
    public record DeptRow(Department Dept, int Depth, int MemberCount, int DeviceCount, bool HasChildren);

    public async Task OnGetAsync() => await LoadAsync();

    // ── 读取并组装树 ──────────────────────────────────────────────────────────
    private async Task LoadAsync()
    {
        var cu = HttpContext.GetCurrentUser()!;
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);

        AllDepts = await db.Departments
            .OrderBy(d => d.SortIndex).ThenBy(d => d.DeptName).ToListAsync();
        if (visibleIds is not null)
            AllDepts = AllDepts.Where(d => visibleIds.Contains(d.Id)).ToList();   // 受限管理员看不到范围外的部门

        // 每个部门的“直属员工数”（DepartmentId 正好等于该部门的人数）
        var direct = (await db.Users.Where(u => u.DepartmentId != null)
                .GroupBy(u => u.DepartmentId!.Value)
                .Select(g => new { DeptId = g.Key, Count = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.DeptId, x => x.Count);

        // 每个部门直属的考勤机数——删除部门前要提醒管理员这些设备会失去归属，跟员工是同一个道理
        var directDevices = (await db.ZKDevices.Where(dv => dv.DepartmentId != null)
                .GroupBy(dv => dv.DepartmentId!.Value)
                .Select(g => new { DeptId = g.Key, Count = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.DeptId, x => x.Count);

        // 按“上级部门”分组，方便递归展开（顶级部门用 0 当 key，因为没有 Id=0 的部门）
        var byParent = AllDepts
            .GroupBy(d => d.ParentId ?? 0)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 成员数 = 本部门直属 + 所有下级部门累加（父部门显示整条线的总人数，而不只是直属）
        var total = new Dictionary<int, int>();
        int Rollup(int deptId)
        {
            var sum = direct.GetValueOrDefault(deptId);
            if (byParent.TryGetValue(deptId, out var kids))
                foreach (var k in kids) sum += Rollup(k.Id);
            total[deptId] = sum;
            return sum;
        }
        // 考勤机数同样按"本部门 + 所有下级部门"累加，口径跟上面的成员数一致
        var totalDevices = new Dictionary<int, int>();
        int RollupDevices(int deptId)
        {
            var sum = directDevices.GetValueOrDefault(deptId);
            if (byParent.TryGetValue(deptId, out var kids))
                foreach (var k in kids) sum += RollupDevices(k.Id);
            totalDevices[deptId] = sum;
            return sum;
        }

        Rows = [];
        void Walk(int parentKey, int depth)
        {
            if (!byParent.TryGetValue(parentKey, out var kids)) return;
            foreach (var d in kids)
            {
                Rows.Add(new DeptRow(d, depth, total.GetValueOrDefault(d.Id), totalDevices.GetValueOrDefault(d.Id), byParent.ContainsKey(d.Id)));
                Walk(d.Id, depth + 1);   // 递归处理它的子部门
            }
        }
        if (cu.IsScoped)
        {
            // 受限管理员：树顶就是自己的范围根部门本身，不是"没有父部门"的那批顶级部门
            var rootId = cu.ScopedDepartmentId!.Value;
            if (byParent.TryGetValue(rootId, out var rootKids))
                foreach (var r in rootKids) { Rollup(r.Id); RollupDevices(r.Id); }
            Rollup(rootId); RollupDevices(rootId);
            var root = AllDepts.FirstOrDefault(d => d.Id == rootId);
            if (root is not null)
            {
                Rows.Add(new DeptRow(root, 0, total.GetValueOrDefault(root.Id), totalDevices.GetValueOrDefault(root.Id), byParent.ContainsKey(root.Id)));
                Walk(root.Id, 1);
            }
        }
        else
        {
            if (byParent.TryGetValue(0, out var roots))
                foreach (var r in roots) { Rollup(r.Id); RollupDevices(r.Id); }
            Walk(0, 0);
        }
    }

    // ── 新增 ──────────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostCreateAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(DeptName))
                throw new InvalidOperationException("请填写部门名称");
            if (DeptName.Trim().Length > 100)
                throw new InvalidOperationException("部门名称不能超过 100 个字");
            if (SortIndex is < 0 or > 9999)
                throw new InvalidOperationException("排序号请填 0-9999 之间");

            var cu = HttpContext.GetCurrentUser()!;
            if (cu.IsScoped && !ParentId.HasValue)
                throw new InvalidOperationException("只能在自己的管理范围内新建子部门，不能新建顶级部门");
            if (!await deptScopeService.CanAccessDeptAsync(cu, ParentId))
                throw new InvalidOperationException("无权在该上级部门下新建子部门");

            var dept = new Department
            {
                DeptName  = DeptName.Trim(),
                ParentId  = ParentId,
                SortIndex = SortIndex,
                IsActive  = IsActive,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            db.Departments.Add(dept);
            await db.SaveChangesAsync();

            SuccessMessage = $"部门「{DeptName.Trim()}」创建成功";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }

        await LoadAsync();
        return Page();
    }

    // ── 编辑 ──────────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostUpdateAsync()
    {
        try
        {
            var dept = await db.Departments.FindAsync(EditId)
                       ?? throw new InvalidOperationException("部门不存在");

            var cu = HttpContext.GetCurrentUser()!;
            // 目标部门本身、改完之后的新上级，都必须在自己的管理范围内——防止受限管理员绕过界面
            // 直接拿别的分公司的部门 id 编辑，或者把自己范围内的部门"挪"到范围外（改父部门实现越权）
            if (!await deptScopeService.CanAccessDeptAsync(cu, dept.Id))
                throw new InvalidOperationException("无权编辑该部门");
            if (cu.IsScoped && dept.Id == cu.ScopedDepartmentId!.Value)
            {
                // 自己的管理范围根部门本身：允许改名字/排序/启用状态，但不能改父部门——
                // 改了父部门等于把自己整个管理范围挪到别的位置，这种事只有总部管理员能做
                if (ParentId != dept.ParentId)
                    throw new InvalidOperationException("不能修改自己管理范围根部门的上级部门，如需调整请联系总部管理员");
            }
            else
            {
                if (cu.IsScoped && !ParentId.HasValue)
                    throw new InvalidOperationException("不能把部门挪到自己管理范围之外（顶级）");
                if (!await deptScopeService.CanAccessDeptAsync(cu, ParentId))
                    throw new InvalidOperationException("无权把部门挪到该上级部门下");
            }

            if (string.IsNullOrWhiteSpace(DeptName))
                throw new InvalidOperationException("请填写部门名称");
            if (DeptName.Trim().Length > 100)
                throw new InvalidOperationException("部门名称不能超过 100 个字");
            if (SortIndex is < 0 or > 9999)
                throw new InvalidOperationException("排序号请填 0-9999 之间");
            if (ParentId == EditId)
                throw new InvalidOperationException("上级部门不能是自己");
            if (ParentId.HasValue && await IsDescendantAsync(ParentId.Value, EditId))
                throw new InvalidOperationException("上级部门不能选择自己的下级部门（会形成循环）");

            dept.DeptName  = DeptName.Trim();
            dept.ParentId  = ParentId;
            dept.SortIndex = SortIndex;
            dept.IsActive  = IsActive;
            dept.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();

            SuccessMessage = "部门信息已更新";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }

        await LoadAsync();
        return Page();
    }

    // ── 删除（支持批量）──────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostDeleteAsync()
    {
        try
        {
            var ids = (DeleteIds ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var i) ? i : 0)
                .Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0)
                throw new InvalidOperationException("请先勾选要删除的部门");

            var cu = HttpContext.GetCurrentUser()!;
            var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);
            // 受限管理员：范围外的 id 静默剔除（不能删别的分公司的部门）；自己的范围根部门本身也不能删——
            // 删了自己的范围根，整个管理范围就没了着落（数据库外键其实也会拦住这个操作，这里提前给个
            // 看得懂的提示，不让它变成一句原始的外键约束错误）
            if (visibleIds is not null)
                ids = ids.Where(id => visibleIds.Contains(id) && id != cu.ScopedDepartmentId!.Value).ToList();
            if (ids.Count == 0)
                throw new InvalidOperationException("没有可以删除的部门（不能删除自己管理范围之外的部门，也不能删除自己的管理范围根部门）");

            var depts = await db.Departments.Where(d => ids.Contains(d.Id)).ToListAsync();

            db.Departments.RemoveRange(depts);
            // 数据库外键约束为 SET NULL：删除后员工的部门自动置空、子部门自动提升为顶级
            await db.SaveChangesAsync();

            SuccessMessage = $"已删除 {depts.Count} 个部门（其员工已转为“未分配”，子部门已提升为顶级）";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }

        await LoadAsync();
        return Page();
    }

    /// <summary>判断 candidateId 是不是 nodeId 的后代（防止把上级设成自己的子孙，形成环）。</summary>
    private async Task<bool> IsDescendantAsync(int candidateId, int nodeId)
    {
        var map = (await db.Departments.Select(d => new { d.Id, d.ParentId }).ToListAsync())
            .ToDictionary(x => x.Id, x => x.ParentId);
        int? cur = candidateId;
        while (cur.HasValue)
        {
            if (cur.Value == nodeId) return true;
            cur = map.GetValueOrDefault(cur.Value);
        }
        return false;
    }
}
