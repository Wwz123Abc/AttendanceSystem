// ── 按类型分类(Tab) + 批量勾选 ────────────────────────────────────────────
// 每一行 <tr> 上都标了 data-type="补卡/请假/加班/出差的枚举名"，切标签就是按这个属性显示/隐藏对应的行，
// 不用重新向后台请求数据（列表本来就已经全部加载好了）。
const typeTabs = document.getElementById('typeTabs');
const allRows  = [...document.querySelectorAll('#approvalRows tr[data-type]')];
const selectAllCheckbox  = document.getElementById('selectAllCheckbox');
const selectedCountText  = document.getElementById('selectedCountText');
const batchApproveBtn    = document.getElementById('batchApproveBtn');
const batchRejectBtn     = document.getElementById('batchRejectBtn');

function visibleRows() { return allRows.filter(r => r.style.display !== 'none'); }
function selectedIds()  { return allRows.filter(r => r.querySelector('.row-check').checked).map(r => r.querySelector('.row-check').value); }

// 刷新右上角"已选 N 条"文字，以及"批量通过/驳回"按钮的可点状态（一条都没选就不让点）
function updateSelectedCount() {
    const n = selectedIds().length;
    if (selectedCountText) selectedCountText.textContent = `已选 ${n} 条`;
    if (batchApproveBtn) batchApproveBtn.disabled = n === 0;
    if (batchRejectBtn)  batchRejectBtn.disabled  = n === 0;
}

if (typeTabs) {
    typeTabs.addEventListener('click', e => {
        const btn = e.target.closest('button[data-type]');
        if (!btn) return;
        typeTabs.querySelectorAll('button').forEach(b => b.classList.replace('btn-primary', 'btn-outline-secondary'));
        btn.classList.replace('btn-outline-secondary', 'btn-primary');
        const type = btn.dataset.type;
        allRows.forEach(r => r.style.display = (type === 'all' || r.dataset.type === type) ? '' : 'none');
        // 切换分类后清空之前的勾选：避免出现"选中的行被筛掉看不见了，但批量按钮还显示选了几条"这种confusing状态
        allRows.forEach(r => { r.querySelector('.row-check').checked = false; });
        if (selectAllCheckbox) selectAllCheckbox.checked = false;
        updateSelectedCount();
    });
}

allRows.forEach(r => r.querySelector('.row-check').addEventListener('change', updateSelectedCount));

if (selectAllCheckbox) {
    // 全选只勾选当前"看得见"的行（被分类标签筛掉的行不受影响），符合"眼见为实"的直觉
    selectAllCheckbox.addEventListener('change', () => {
        visibleRows().forEach(r => { r.querySelector('.row-check').checked = selectAllCheckbox.checked; });
        updateSelectedCount();
    });
}

// ── 审批确认弹窗：单条通过/驳回、批量通过/驳回，四种情况共用同一个弹窗 ──────────
// 点哪个按钮，就把按钮上标的数据（data-id=单条时处理哪张申请单、data-action=四种情况之一）读出来，
// 现改这个弹窗的标题、按钮文字/颜色、提交去哪个后台接口，批量时还要把勾选的申请单 id 逐个塞成隐藏字段。
const approveModal = document.getElementById('approveModal');
approveModal.addEventListener('show.bs.modal', function(e) {
    const btn = e.relatedTarget;   // relatedTarget = 触发弹窗打开的那个按钮
    const action  = btn.dataset.action;             // approve / reject / batch-approve / batch-reject
    const isBatch = action.startsWith('batch-');
    const approved = action === 'approve' || action === 'batch-approve';
    const ids = isBatch ? selectedIds() : [];

    document.getElementById('modalRequestId').value = btn.dataset.id || '';

    document.getElementById('approveModalTitle').textContent = isBatch
        ? `确认批量${approved ? '通过' : '驳回'}（共 ${ids.length} 条）`
        : (approved ? '确认通过申请' : '确认驳回申请');

    const submitBtn = document.getElementById('approveBtn');
    submitBtn.textContent = approved ? '确认通过' : '确认驳回';
    submitBtn.className = approved ? 'btn btn-success' : 'btn btn-danger';
    submitBtn.formAction = isBatch
        ? (approved ? '?handler=BatchApprove' : '?handler=BatchReject')
        : (approved ? '?handler=Approve' : '?handler=Reject');

    // 批量模式：把当前勾选的申请单 id 逐个塞成隐藏字段，随表单一起提交给后台（对应 BindProperty List<int> BatchIds）
    const container = document.getElementById('batchIdsContainer');
    container.innerHTML = '';
    ids.forEach(v => {
        const inp = document.createElement('input');
        inp.type = 'hidden'; inp.name = 'BatchIds'; inp.value = v;
        container.appendChild(inp);
    });

    // 驳回必须说明原因，方便申请人知道被拒的理由；通过则和以前一样，意见选填
    const commentInput = document.getElementById('commentInput');
    commentInput.required = !approved;
    document.getElementById('commentLabel').textContent = approved ? '审批意见（可选）' : '驳回原因（必填）';
});


// "查看详情"弹窗：点眼睛图标时，直接从上面已经准备好的 APPROVAL_DETAILS 字典里
// 按申请单 Id 取出对应那条数据来显示，不用再单独向后台发一次请求要数据（页面加载时已经带过来了）。
const detailModal = document.getElementById('detailModal');
detailModal.addEventListener('show.bs.modal', function(e) {
    const d = APPROVAL_DETAILS[e.relatedTarget.dataset.id];
    if (!d) { document.getElementById('detailBody').innerHTML = '加载失败'; return; }

    let rows = `
        <dt class="col-4">单号</dt><dd class="col-8">${d.no}</dd>
        <dt class="col-4">申请类型</dt><dd class="col-8">${d.type}</dd>
        <dt class="col-4">申请人</dt><dd class="col-8">${escapeHtml(d.name)}（${escapeHtml(d.empNo)}）</dd>
        <dt class="col-4">部门</dt><dd class="col-8">${d.dept ? escapeHtml(d.dept) : '—'}</dd>`;

    // 按类型展示具体日期/时间
    if (d.leaveStart) {
        rows += `
        <dt class="col-4">假期类型</dt><dd class="col-8">${d.leaveType}</dd>
        <dt class="col-4">请假起止</dt><dd class="col-8">${d.leaveStart} ~ ${d.leaveEnd}</dd>
        <dt class="col-4">请假时长</dt><dd class="col-8">${d.leaveHours} 小时</dd>`;
    }
    if (d.punchDate) {
        rows += `
        <dt class="col-4">补卡日期</dt><dd class="col-8">${d.punchDate}</dd>
        <dt class="col-4">补卡类型</dt><dd class="col-8">${d.punchType}</dd>
        <dt class="col-4">补卡时间</dt><dd class="col-8">${d.punchTime}</dd>`;
    }
    if (d.otStart) {
        rows += `
        <dt class="col-4">加班起止</dt><dd class="col-8">${d.otStart} ~ ${d.otEnd}</dd>
        <dt class="col-4">加班时长</dt><dd class="col-8">${d.otHours} 小时</dd>`;
    }
    if (d.tripStart) {
        rows += `
        <dt class="col-4">出差目的地</dt><dd class="col-8">${d.tripDest ? escapeHtml(d.tripDest) : '未填写'}</dd>
        <dt class="col-4">出差起止</dt><dd class="col-8">${d.tripStart} ~ ${d.tripEnd}</dd>
        <dt class="col-4">出差天数</dt><dd class="col-8">${d.tripDays} 天</dd>`;
    }

    rows += `
        <dt class="col-4">申请原因</dt><dd class="col-8">${d.reason ? escapeHtml(d.reason) : '无'}</dd>
        <dt class="col-4">提交时间</dt><dd class="col-8">${d.time}</dd>`;

    // 附件材料（审批人可点击查看）
    let att = '';
    if (d.attachments && d.attachments.length) {
        att = '<dt class="col-4">附件材料</dt><dd class="col-8">' +
            d.attachments.map((u, idx) =>
                `<a href="${u.startsWith('/') ? escapeHtml(u) : '#'}" target="_blank" rel="noopener noreferrer" class="d-block"><i class="bi bi-paperclip me-1"></i>附件${idx + 1}（${escapeHtml(u.split('/').pop())}）</a>`
            ).join('') + '</dd>';
    } else {
        att = '<dt class="col-4">附件材料</dt><dd class="col-8 text-muted">无</dd>';
    }

    document.getElementById('detailBody').innerHTML = `<dl class="row mb-0">${rows}${att}</dl>`;
});

// escapeHtml 已在 _Layout.cshtml 里全局定义一份，这里不再重复声明（2026-09-21 去重）。
