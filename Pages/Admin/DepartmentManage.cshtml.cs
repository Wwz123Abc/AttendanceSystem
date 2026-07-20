using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 部门管理页：单页树形表格，支持增删改、批量删除、添加子部门。
/// 新建/改名调整层级/删除部门时，会顺带把这个部门同步到钉钉通讯录（本系统 → 钉钉），
/// 让钉钉那边的部门结构跟本系统保持一致；钉钉那边同步失败不影响本地操作，只带一句提示回去。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class DepartmentManageModel(
    AttendanceDbContext db, IDingTalkContactClient dingTalkContactClient, ILogger<DepartmentManageModel> logger) : PageModel
{
    /// <summary>钉钉的根部门编号，新建顶级部门时要把它当"上级部门"传给钉钉。</summary>
    private const long DingTalkRootDeptId = 1;

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
    public record DeptRow(Department Dept, int Depth, int MemberCount, bool HasChildren);

    public async Task OnGetAsync() => await LoadAsync();

    // ── 读取并组装树 ──────────────────────────────────────────────────────────
    private async Task LoadAsync()
    {
        AllDepts = await db.Departments
            .OrderBy(d => d.SortIndex).ThenBy(d => d.DeptName).ToListAsync();

        // 每个部门的“直属员工数”（DepartmentId 正好等于该部门的人数）
        var direct = (await db.Users.Where(u => u.DepartmentId != null)
                .GroupBy(u => u.DepartmentId!.Value)
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
        if (byParent.TryGetValue(0, out var roots))
            foreach (var r in roots) Rollup(r.Id);

        Rows = [];
        void Walk(int parentKey, int depth)
        {
            if (!byParent.TryGetValue(parentKey, out var kids)) return;
            foreach (var d in kids)
            {
                Rows.Add(new DeptRow(d, depth, total.GetValueOrDefault(d.Id), byParent.ContainsKey(d.Id)));
                Walk(d.Id, depth + 1);   // 递归处理它的子部门
            }
        }
        Walk(0, 0);
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

            var warning = await TrySyncCreateToDingTalkAsync(dept);
            SuccessMessage = $"部门「{DeptName.Trim()}」创建成功" + (warning is null ? "" : $"（{warning}）");
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }

        await LoadAsync();
        return Page();
    }

    /// <summary>
    /// 尝试把刚建好的本地部门同步创建到钉钉（本系统 → 钉钉）。
    /// 顶级部门（没有上级）挂到钉钉根部门下；有上级的部门，要求上级部门已经有钉钉部门编号，
    /// 换不出来（上级还没同步过）就跳过，等上级同步好了、这个部门下次编辑时会自动补上。
    /// 返回值：null=已同步成功或本来就跳过；非空=已经具备同步条件、但调用钉钉接口失败了，给管理员看的提示。
    /// </summary>
    private async Task<string?> TrySyncCreateToDingTalkAsync(Department dept)
    {
        long parentDingId;
        if (!dept.ParentId.HasValue)
        {
            parentDingId = DingTalkRootDeptId;
        }
        else
        {
            var parent = await db.Departments.FindAsync(dept.ParentId.Value);
            if (parent?.DingTalkDeptId is not { } pid)
                return null;   // 上级部门还没有钉钉编号，跳过；不算错误，日常操作大多如此（逐级建部门时钉钉同步会跟着逐级补上）
            parentDingId = pid;
        }

        try
        {
            dept.DingTalkDeptId = await dingTalkContactClient.CreateDepartmentAsync(dept.DeptName, parentDingId);
            await db.SaveChangesAsync();
            return null;
        }
        catch (Exception ex)
        {
            return $"钉钉同步创建部门失败：{ex.Message}（本地部门已正常创建，如需要请到钉钉通讯录手动添加）";
        }
    }

    // ── 编辑 ──────────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostUpdateAsync()
    {
        try
        {
            var dept = await db.Departments.FindAsync(EditId)
                       ?? throw new InvalidOperationException("部门不存在");
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

            var warning = await TrySyncUpdateToDingTalkAsync(dept);
            SuccessMessage = "部门信息已更新" + (warning is null ? "" : $"（{warning}）");
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }

        await LoadAsync();
        return Page();
    }

    /// <summary>
    /// 尝试把改过的部门名称/上级部门同步到钉钉（本系统 → 钉钉）。
    /// 这个部门如果还没有钉钉部门编号（比如是这个功能上线之前建的老部门），就当成"补建"，
    /// 走一遍和新建一样的流程，顺便把编号补上，以后就能正常联动了。
    /// </summary>
    private async Task<string?> TrySyncUpdateToDingTalkAsync(Department dept)
    {
        if (dept.DingTalkDeptId is not { } dingId)
            return await TrySyncCreateToDingTalkAsync(dept);   // 老部门第一次同步，走"新建"逻辑补上编号

        long? parentDingId;
        if (!dept.ParentId.HasValue)
        {
            parentDingId = DingTalkRootDeptId;
        }
        else
        {
            var parent = await db.Departments.FindAsync(dept.ParentId.Value);
            parentDingId = parent?.DingTalkDeptId;   // 新上级还没同步过就传 null，这次先只改名字，上级留给下次编辑再补
        }

        try
        {
            await dingTalkContactClient.UpdateDepartmentAsync(dingId, dept.DeptName, parentDingId);
            return null;
        }
        catch (Exception ex)
        {
            return $"钉钉同步更新部门失败：{ex.Message}（本地信息已正常更新，如需要请到钉钉通讯录手动确认）";
        }
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

            var depts = await db.Departments.Where(d => ids.Contains(d.Id)).ToListAsync();
            var dingIdsToDelete = depts.Where(d => d.DingTalkDeptId.HasValue).Select(d => d.DingTalkDeptId!.Value).ToList();

            db.Departments.RemoveRange(depts);
            // 数据库外键约束为 SET NULL：删除后员工的部门自动置空、子部门自动提升为顶级
            await db.SaveChangesAsync();

            // 本地删除已经成功了，再尽力把钉钉那边对应的部门也删掉（本系统 → 钉钉）；
            // 钉钉那边如果部门下还有成员会拒绝删除，这种情况只记一句提示，不回滚本地删除
            var failedCount = 0;
            foreach (var dingId in dingIdsToDelete)
            {
                try { await dingTalkContactClient.DeleteDepartmentAsync(dingId); }
                catch (Exception ex)
                {
                    failedCount++;
                    logger.LogWarning(ex, "删除部门时同步删除钉钉部门失败，钉钉部门编号={DingTalkDeptId}", dingId);
                }
            }

            SuccessMessage = $"已删除 {depts.Count} 个部门（其员工已转为“未分配”，子部门已提升为顶级）"
                + (failedCount > 0 ? $"（其中 {failedCount} 个钉钉同步删除失败，请到钉钉通讯录手动确认，常见原因是部门下还有钉钉成员）" : "");
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
