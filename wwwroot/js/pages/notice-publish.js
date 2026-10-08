// 发布范围选择：切到"指定部门/指定考勤组"才显示对应的下拉框
const scopeTypeSelect = document.getElementById('scopeTypeSelect');
if (scopeTypeSelect) {
    function updateScopeUI() {
        const deptWrap  = document.getElementById('scopeDeptWrap');
        const groupWrap = document.getElementById('scopeGroupWrap');
        const roleWrap  = document.getElementById('scopeRoleWrap');
        const deptSelect  = document.getElementById('scopeDeptSelect');
        const groupSelect = document.getElementById('scopeGroupSelect');
        const val = scopeTypeSelect.value;
        deptWrap.style.display  = val === 'Department'      ? '' : 'none';
        groupWrap.style.display = val === 'AttendanceGroup' ? '' : 'none';
        roleWrap.style.display  = val === 'Role'            ? '' : 'none';
        // ScopeId 这个隐藏绑定字段跟着当前显示的那个下拉框走
        deptSelect.name  = val === 'Department'      ? 'ScopeId' : '';
        groupSelect.name = val === 'AttendanceGroup'  ? 'ScopeId' : '';
    }
    scopeTypeSelect.addEventListener('change', updateScopeUI);
    updateScopeUI();
}

// 已读明细弹窗：点按钮时 AJAX 拉这条公告的已读名单
const readDetailModal = new bootstrap.Modal(document.getElementById('readDetailModal'));
document.querySelectorAll('.read-detail-btn').forEach(btn => {
    btn.addEventListener('click', async () => {
        const id = btn.dataset.id;
        const tbody = document.getElementById('readDetailBody');
        tbody.innerHTML = '<tr><td colspan="3" class="text-muted small">加载中...</td></tr>';
        readDetailModal.show();
        try {
            const resp = await fetch(`?handler=ReadDetail&id=${id}`);
            const list = await resp.json();
            tbody.innerHTML = list.length === 0
                ? '<tr><td colspan="3" class="text-muted small">没有受众</td></tr>'
                : list.map(x => `
                    <tr>
                        <td>${escapeHtml(x.realName)}</td>
                        <td class="small text-muted">${escapeHtml(x.employeeNo)}</td>
                        <td class="small ${x.readAt ? 'text-success' : 'text-danger'}">${x.readAtText}</td>
                    </tr>`).join('');
        } catch (e) {
            tbody.innerHTML = '<tr><td colspan="3" class="text-danger small">加载失败</td></tr>';
        }
    });
});

// escapeHtml 已在 _Layout.cshtml 里全局定义一份，这里不再重复声明（2026-09-21 去重）。
