// 总审批记录页：审批进度表里点"查看申请"，弹出这个审批人手上"现在轮到他审批"的申请列表。
async function openPendingOf(btn) {
    const modal = bootstrap.Modal.getOrCreateInstance(document.getElementById('pendingOfModal'));
    document.getElementById('pendingOfTitle').textContent = btn.dataset.approverName + ' 手上待审批的申请';
    document.getElementById('pendingOfLoading').classList.remove('d-none');
    document.getElementById('pendingOfWrap').classList.add('d-none');
    document.getElementById('pendingOfEmpty').classList.add('d-none');
    document.getElementById('pendingOfEmpty').innerHTML = '<i class="bi bi-inbox"></i>没有数据';
    document.getElementById('pendingOfBody').innerHTML = '';
    modal.show();

    const dept = document.getElementById('progressCard').dataset.dept || '';
    try {
        const resp = await fetch(`?handler=PendingOf&approverId=${btn.dataset.approverId}&deptId=${dept}`);
        const json = await resp.json();
        document.getElementById('pendingOfLoading').classList.add('d-none');
        if (!json.success || !json.data || json.data.length === 0) {
            document.getElementById('pendingOfEmpty').classList.remove('d-none');
            return;
        }
        document.getElementById('pendingOfBody').innerHTML = json.data.map(r => `
            <tr>
                <td class="small text-nowrap">${escapeHtml(r.requestNo)}</td>
                <td class="small text-nowrap">${escapeHtml(r.typeText)}</td>
                <td class="small text-nowrap">${escapeHtml(r.applicantName)}<br><span class="text-muted">${escapeHtml(r.applicantEmployeeNo)}</span></td>
                <td class="small">${escapeHtml(r.deptName)}</td>
                <td class="small">${escapeHtml(r.contentText)}</td>
                <td class="small text-nowrap">${escapeHtml(r.submittedAt)}</td>
                <td class="small text-nowrap ${r.days >= 3 ? 'text-danger fw-semibold' : ''}">${r.days} 天</td>
                <td class="small">${escapeHtml(r.laterApprovers || '—')}</td>
            </tr>`).join('');
        document.getElementById('pendingOfWrap').classList.remove('d-none');
    } catch (e) {
        document.getElementById('pendingOfLoading').classList.add('d-none');
        document.getElementById('pendingOfEmpty').classList.remove('d-none');
        document.getElementById('pendingOfEmpty').textContent = '加载失败，请重试';
    }
}
