// ── 展开 / 折叠 ────────────────────────────────────────────────────────────
function toggleNode(id, btn) {
    const collapsed = btn.classList.toggle('collapsed');
    btn.querySelector('i').className = collapsed ? 'bi bi-chevron-right' : 'bi bi-chevron-down';
    applyChildVisibility(id, !collapsed);
}
function applyChildVisibility(parentId, visible) {
    document.querySelectorAll(`tr[data-parent="${parentId}"]`).forEach(tr => {
        tr.style.display = visible ? '' : 'none';
        const cid  = tr.getAttribute('data-id');
        const cbtn = tr.querySelector('.tree-toggle');
        if (!visible) applyChildVisibility(cid, false);
        else          applyChildVisibility(cid, cbtn ? !cbtn.classList.contains('collapsed') : true);
    });
}

// ── 勾选 ──────────────────────────────────────────────────────────────────
function toggleAll(cb) { document.querySelectorAll('.row-check').forEach(c => c.checked = cb.checked); }

// ── 新增 / 编辑 ────────────────────────────────────────────────────────────
function openAdd(parentId) {
    document.getElementById('deptModalTitle').textContent = '添加部门';
    document.getElementById('deptForm').action = '?handler=Create';
    document.getElementById('fEditId').value    = 0;
    document.getElementById('fDeptName').value  = '';
    document.getElementById('fSortIndex').value = 0;
    document.getElementById('fIsActive').checked = true;
    // 默认上级：显式传入的 > 列表里唯一勾选的那个 > 无
    let pid = parentId;
    if (pid == null) {
        const checked = document.querySelectorAll('.row-check:checked');
        if (checked.length === 1) pid = checked[0].value;
    }
    document.getElementById('fParentId').value = pid ?? '';
    new bootstrap.Modal(document.getElementById('deptModal')).show();
}
function openEditFromRow(tr) {
    const d = tr.dataset;
    document.getElementById('deptModalTitle').textContent = '编辑部门';
    document.getElementById('deptForm').action = '?handler=Update';
    document.getElementById('fEditId').value    = d.id;
    document.getElementById('fDeptName').value  = d.name;
    document.getElementById('fParentId').value  = d.parentid || '';
    document.getElementById('fSortIndex').value = d.sort;
    document.getElementById('fIsActive').checked = d.active === 'true';
    new bootstrap.Modal(document.getElementById('deptModal')).show();
}
function editSelected() {
    const checked = document.querySelectorAll('.row-check:checked');
    if (checked.length !== 1) { alert('请只勾选一个部门，再点「编辑选中」'); return; }
    openEditFromRow(checked[0].closest('tr'));
}

// ── 删除（批量 / 单个）────────────────────────────────────────────────────
function submitDelete(ids) {
    if (ids.length === 0) { alert('请先勾选要删除的部门'); return; }
    // 挨个部门列出具体受影响的人数/设备数，而不是笼统一句"含有子部门或员工"——
    // 数字越具体，管理员越容易判断这次删除是不是自己真的想要的操作
    const lines = [];
    let hasImpact = false;
    ids.forEach(id => {
        const tr = document.querySelector(`tr[data-id="${id}"]`);
        if (!tr) return;
        const name    = tr.dataset.name;
        const members = parseInt(tr.dataset.members) || 0;
        const devices = parseInt(tr.dataset.devices) || 0;
        const hasKids = tr.dataset.haschildren === 'true';
        if (members > 0 || devices > 0 || hasKids) {
            hasImpact = true;
            const parts = [];
            if (members > 0) parts.push(`${members} 名员工`);
            if (devices > 0) parts.push(`${devices} 台考勤机`);
            if (hasKids)     parts.push('子部门');
            lines.push(`「${name}」：${parts.join('、')}`);
        }
    });
    const msg = hasImpact
        ? `所选部门（含下级）合计影响：\n${lines.join('\n')}\n\n删除后：员工将变为"未分配"，考勤机将失去归属，子部门将提升为顶级部门。\n\n确认删除？`
        : '确认删除所选部门？';
    if (!confirm(msg)) return;
    document.getElementById('fDeleteIds').value = ids.join(',');
    document.getElementById('deleteForm').submit();
}
function deleteSelected() { submitDelete([...document.querySelectorAll('.row-check:checked')].map(c => c.value)); }
function deleteOne(id)    { submitDelete([String(id)]); }
