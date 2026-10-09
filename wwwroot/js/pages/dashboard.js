let drilldownModal;
document.addEventListener('DOMContentLoaded', () => {
    drilldownModal = new bootstrap.Modal(document.getElementById('drilldownModal'));
});

async function openDrilldown(category, title) {
    document.getElementById('drilldownTitle').textContent = title + ' - 人员名单';
    document.getElementById('drilldownLoading').classList.remove('d-none');
    document.getElementById('drilldownTableWrap').classList.add('d-none');
    document.getElementById('drilldownEmpty').classList.add('d-none');
    // 每次打开都先把"暂无人员"这个空状态的内容复位，防止上一次请求失败时把它永久改写成
    // "加载失败，请重试"之后，这次哪怕是正常的"查到 0 人"也会显示成上次的失败文案
    document.getElementById('drilldownEmpty').innerHTML = '<i class="bi bi-inbox"></i>暂无人员';
    document.getElementById('drilldownBody').innerHTML = '';
    drilldownModal.show();

    try {
        const resp = await fetch(`?handler=Drilldown&category=${category}`);
        const json = await resp.json();
        document.getElementById('drilldownLoading').classList.add('d-none');

        if (!json.success || !json.data || json.data.length === 0) {
            document.getElementById('drilldownEmpty').classList.remove('d-none');
            return;
        }

        document.getElementById('drilldownBody').innerHTML = json.data.map(p => `
            <tr>
                <td class="small text-muted">${escapeHtml(p.employeeNo)}</td>
                <td class="fw-semibold">${escapeHtml(p.realName)}</td>
                <td class="small">${escapeHtml(p.deptName)}</td>
                <td class="small">${escapeHtml(p.clockIn)}</td>
                <td class="small">${escapeHtml(p.clockOut)}</td>
                <td><span class="badge ${p.badge}">${escapeHtml(p.statusText)}</span></td>
            </tr>`).join('');
        document.getElementById('drilldownTableWrap').classList.remove('d-none');
    } catch (e) {
        document.getElementById('drilldownLoading').classList.add('d-none');
        document.getElementById('drilldownEmpty').classList.remove('d-none');
        document.getElementById('drilldownEmpty').textContent = '加载失败，请重试';
    }
}

// escapeHtml 已在 _Layout.cshtml 里全局定义一份，这里不再重复声明（2026-09-21 去重）。


// ── 今日数字每 30 秒悄悄更新一次（不整页刷新，不会闪屏、也不会打断正在看的名单弹窗）──
const REFRESH_SECONDS = 30;
async function refreshStats() {
    if (document.hidden) return;   // 页面在后台标签里就先不取
    try {
        const resp = await fetch('?handler=Stats', { headers: { 'Accept': 'application/json' } });
        if (!resp.ok) return;
        const s = await resp.json();
        if (!s.success) return;
        const set = (id, v) => { const el = document.getElementById(id); if (el) el.textContent = v; };
        set('st-total', s.total); set('st-present', s.present); set('st-absent', s.absent);
        set('st-late', s.late); set('st-onleave', s.onLeave); set('st-notpunched', s.notPunched);
        set('st-rate', s.rate.toFixed(1) + '%');
        set('st-ratetext', `${s.present} / ${s.total} 人出勤`);
        const bar = document.getElementById('st-ratebar');
        if (bar) bar.style.width = s.rate + '%';
        set('st-updated', '更新于 ' + s.updatedAt);
    } catch (e) { /* 网络抖动就等下一轮 */ }
}
setInterval(refreshStats, REFRESH_SECONDS * 1000);
document.addEventListener('visibilitychange', () => { if (!document.hidden) refreshStats(); });
