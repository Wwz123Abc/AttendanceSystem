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
        // 夜班（跨天班次）：补下班卡时默认勾上"第二天"，管理员可以自己改
        window.lastShiftCrossDay = !!data.crossDay;
        window.lastShiftStart = data.shiftStart || null;
        refreshPunchTypeUi();
        const row = (label, html) => `<div class="row g-1 mb-1"><div class="col-3 col-md-2 text-muted">${label}</div><div class="col-9 col-md-10">${html}</div></div>`;
        const t = s => s ? s.replace('T', ' ').slice(5) : '--';
        const parts = [];
        if (data.exists) {
            parts.push(row('当前记录', `上班 <b>${t(data.clockIn)}</b> · 下班 <b>${t(data.clockOut)}</b> · 状态 <b>${escapeHtml(data.status)}</b> · 工时 <b>${data.workHours}</b> 小时`));
        } else {
            parts.push(row('当前记录', '<span class="text-muted">这天暂无考勤记录</span>'));
        }
        parts.push(row('班次', escapeHtml(data.shift || '—')));
        const pun = data.punches || [];
        const fmt = p => `<span class="text-nowrap me-3">${escapeHtml(p.time)} <span class="text-muted">${escapeHtml(p.type)}${p.source ? '·' + escapeHtml(p.source) : ''}</span></span>`;
        const today = pun.filter(p => !p.nextDay), next = pun.filter(p => p.nextDay);
        let punHtml = pun.length === 0 ? '<span class="text-muted">无</span>' : (today.length ? today.map(fmt).join('') : '<span class="text-muted">当天无</span>');
        if (next.length) punHtml += `<div class="mt-1"><span class="text-muted me-2">次日</span>${next.map(fmt).join('')}</div>`;
        parts.push(row('原始打卡', punHtml));
        if (data.midResult) parts.push(row('午间命中', escapeHtml(data.midResult.replaceAll(';', '；'))));
        if (data.notes && data.notes.length)
            parts.push(row('备注', '<ul class="mb-0 ps-3">' + data.notes.map(n => `<li>${escapeHtml(n.text)}${n.count > 1 ? ` <span class="badge bg-secondary">×${n.count}</span>` : ''}</li>`).join('') + '</ul>'));
        box.innerHTML = parts.join('');
        box.style.display = '';
    } catch (e) { alert('查询失败，请重试'); }
}

// escapeHtml 已在 _Layout.cshtml 里全局定义一份，这里不再重复声明（2026-09-21 去重）。

// 补卡类型不同，说明和"强制覆盖"是否可用也不同：午间卡只新增一条，不存在覆盖
const PUNCH_TYPE_HELP = {
    '1': '补上班卡：默认只有比现有上班时间更早才生效。',
    '2': '补下班卡：默认只有比现有下班时间更晚才生效。',
    '3': '补午间那次打卡：时间必须落在班次的午间窗口内、且这一段还没打上卡，只新增一条，不动上下班时间。'
};
function refreshPunchTypeUi() {
    const v = document.getElementById('fPunchType').value;
    document.getElementById('fPunchTypeHelp').textContent = PUNCH_TYPE_HELP[v] || '';
    const mid = v === '3';
    document.getElementById('fOverwriteBox').style.display = mid ? 'none' : '';
    if (mid) document.getElementById('fOverwrite').checked = false;
    autoNextDay();
}
document.getElementById('fPunchType').addEventListener('change', refreshPunchTypeUi);
refreshPunchTypeUi();

// 夜班（跨天班次）：补下班卡/午间卡时，填的钟点早于班次上班时间（比如 08:00、00:30）就是已经过了零点，自动勾上"第二天"；
// 上班卡不会是第二天。管理员之后可以自己改勾选框——只有换类型、改钟点、重新查询时才会重新判断。
function autoNextDay() {
    if (!window.lastShiftCrossDay || !window.lastShiftStart) return;
    const type = document.getElementById('fPunchType').value;
    const time = document.getElementById('fPunchTime').value;
    const box = document.getElementById('fNextDay');
    if (type === '1') { box.checked = false; return; }
    if (time) box.checked = time < window.lastShiftStart;
}
document.getElementById('fPunchTime').addEventListener('input', autoNextDay);
