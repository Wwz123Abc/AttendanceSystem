async function searchUsers(keyword) {
    if (!keyword) { renderUserResults([]); return; }
    try {
        const resp = await fetch(`?handler=SearchUsers&keyword=${encodeURIComponent(keyword)}`);
        renderUserResults(await resp.json());
    } catch (e) { renderUserResults([]); }
}

let lastUserResults = [];
function renderUserResults(list) {
    lastUserResults = list;
    const box = document.getElementById('fUserResults');
    if (list.length === 0) { box.style.display = 'none'; box.innerHTML = ''; return; }
    box.innerHTML = list.map((u, i) =>
        `<button type="button" class="list-group-item list-group-item-action py-1 px-2 small" data-idx="${i}">${escapeHtml(u.label)}</button>`
    ).join('');
    box.style.display = 'block';
    box.querySelectorAll('[data-idx]').forEach(btn => {
        btn.addEventListener('click', () => {
            const u = lastUserResults[parseInt(btn.dataset.idx, 10)];
            document.getElementById('fUserId').value = u.id;
            document.getElementById('fUserSearch').value = u.label;
            box.style.display = 'none';
            document.getElementById('currentRecordBox').style.display = 'none';
        });
    });
}

let userSearchTimer = null;
document.getElementById('fUserSearch').addEventListener('input', function () {
    document.getElementById('fUserId').value = '';   // 手动改动搜索框时先清空已选中的，等真正点选候选项后才重新赋值
    clearTimeout(userSearchTimer);
    const kw = this.value.trim();
    userSearchTimer = setTimeout(() => searchUsers(kw), 200);
});
document.addEventListener('click', function (e) {
    const box = document.getElementById('fUserResults');
    if (!document.getElementById('fUserSearch').contains(e.target) && !box.contains(e.target)) box.style.display = 'none';
});

async function loadCurrentRecord() {
    const userId = document.getElementById('fUserId').value;
    const workDate = document.getElementById('fWorkDate').value;
    const box = document.getElementById('currentRecordBox');
    if (!userId) { alert('请先从下拉列表中选择一位员工'); return; }
    if (!workDate) { alert('请先选择考勤日期'); return; }
    try {
        const resp = await fetch(`?handler=Record&userId=${userId}&workDate=${workDate}`);
        const data = await resp.json();
        if (!data.exists) {
            box.innerHTML = '<i class="bi bi-info-circle me-1"></i>该员工这一天暂无考勤记录。';
        } else {
            box.innerHTML = `<i class="bi bi-info-circle me-1"></i>当前记录：上班 ${data.clockIn ? data.clockIn.replace('T', ' ') : '--'}，`
                + `下班 ${data.clockOut ? data.clockOut.replace('T', ' ') : '--'}，状态 ${data.status}，工时 ${data.workHours} 小时`
                + (data.note ? `，备注：${escapeHtml(data.note)}` : '');
        }
        box.style.display = '';
    } catch (e) { alert('查询失败，请重试'); }
}

// escapeHtml 已在 _Layout.cshtml 里全局定义一份，这里不再重复声明（2026-09-21 去重）。
