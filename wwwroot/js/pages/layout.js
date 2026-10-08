// ── 窄屏侧边栏开关：手机宽度下侧边栏默认收起来，点汉堡按钮滑出，点遮罩层/选菜单再收回去 ──
function toggleSidebar() {
    document.querySelector('.sidebar').classList.toggle('open');
    document.getElementById('sidebarBackdrop').classList.toggle('show');
}
function closeSidebar() {
    document.querySelector('.sidebar').classList.remove('open');
    document.getElementById('sidebarBackdrop').classList.remove('show');
}

// ── 右上角"小铃铛"通知功能 ───────────────────────────────────────────────
// notifOpen 记录通知下拉框现在是"打开"还是"关闭"，点一下铃铛就反过来切换一次
let notifOpen = false;
// 记这次打开页面以来"已经提示过"的通知 id，避免同一条每隔 60 秒轮询一次就重复弹窗、重复响铃。
// notifFirstLoad 只管第一次拉取：刚打开页面时如果本来就攒着一堆未读通知，只更新铃铛上的数字，
// 不用把它们全部弹一遍、响一遍铃——只有"打开页面之后新收到的"才弹窗+响铃，体验上更像"刚才有新消息"。
const seenNotifIds = new Set();
let notifFirstLoad = true;
let notifPrevCount = 0;   // 上一次拉取时的未读总数，用来发现"新增的未读比这次弹出的卡片还多"（见下方 missedCount）

// 点铃铛：切换下拉框的开关状态；打开的时候顺便去后台拉一次最新通知
function toggleNotifDropdown() {
    notifOpen = !notifOpen;
    document.getElementById('notifDropdown').classList.toggle('show', notifOpen);
    if (notifOpen) loadNotifications();
}

// 监听整个页面的点击：只要点的地方不在铃铛/下拉框范围内，就把下拉框关掉。
// 这样实现"点击外面自动收起"的效果，不用给每个别的元素单独绑定事件。
document.addEventListener('click', e => {
    if (!document.getElementById('notifBell').contains(e.target)) {
        notifOpen = false;
        document.getElementById('notifDropdown').classList.remove('show');
    }
});

// 向后台要"我的未读通知"列表，拿到后拼成 HTML 显示在下拉框里，
// 并且把铃铛右上角的数字气泡（badge）更新成未读条数
async function loadNotifications() {
    try {
        const r = await fetch('/api/notifications');
        const data = await r.json();
        const badge = document.getElementById('notifBadge');
        const list  = document.getElementById('notifList');

        badge.textContent = data.count > 99 ? '99+' : data.count;       // 超过99条就显示"99+"，不然数字太长挤不下
        badge.style.display = data.count > 0 ? 'flex' : 'none';         // 没有未读就把这个红色气泡隐藏掉

        // 弹窗/响铃的"新通知"判断必须放在"空列表就提前返回"之前：打开页面时没有未读通知是最常见的情况，
        // 如果空列表分支直接 return，notifFirstLoad/notifPrevCount 就一直停在初始值，接下来第一条新通知会被
        // 当成"打开页面时就有的旧通知"，不弹卡片、不响铃，只有铃铛数字 +1（2026-10-06 复核发现）
        const freshOnes = notifFirstLoad ? [] : data.items.filter(n => !seenNotifIds.has(n.id));
        data.items.forEach(n => seenNotifIds.add(n.id));
        const missedCount = notifFirstLoad ? 0 : Math.max(0, (data.count - notifPrevCount) - freshOnes.length);
        notifPrevCount  = data.count;
        notifFirstLoad  = false;

        if (data.items.length === 0) {
            list.innerHTML = '<div class="text-center text-muted py-3 small">暂无未读通知</div>';
            return;
        }

        // 把每条通知拼成一小块 HTML（标题+时间+内容），点击后调用 readNotif 标记已读。
        // 标题/内容可能来自公告正文或员工自助登记时自己填的姓名，都是用户能控制的自由文本，
        // 不能直接拼进 HTML（会被当成"存储型 XSS"利用），下面用 escapeHtml 转义成安全文本
        list.innerHTML = data.items.map(n => `
            <div class="notif-item unread" onclick="readNotif(${n.id}, '${n.notificationType}', ${n.relatedId})">
                <div class="d-flex justify-content-between">
                    <div class="notif-title">${escapeHtml(n.title)}</div>
                    <div class="notif-time">${n.createdAt}</div>
                </div>
                <div class="notif-content">${escapeHtml(n.content)}</div>
            </div>`).join('');

        // 上面已经算出"这次轮询里新出现的"通知（第一次拉取时不算新——那是打开页面前就攒下的旧未读）。
        // 每条新通知弹一张小卡片；弹窗数量不设上限（真出现好几条一起弹，按顺序往下排队也不会乱），
        // 但铃声只响一次，避免同一刻来了好几条通知就连响好几下
        // /api/notifications 只返回最新 15 条未读（GetUnread 的 Take(15)），未读总数 count 不受这个限制。
        // 如果两次轮询之间一次性新增的未读比这 15 条还多（比如"待审批提醒"一次给同一个人发了十几条），
        // 排在第 15 条以外的那几条不会出现在 data.items 里，也就没法像别的新通知一样单独弹卡片——
        // 用总数的增量减去这次实际弹出的卡片数，算出"这次没弹出来的"有几条，另外弹一条汇总提示兜底，
        // 不然这几条会安安静静地待在铃铛数字里，用户完全不会注意到还有新通知进来（2026-09-30 复核发现）。
        if (freshOnes.length > 0 || missedCount > 0) {
            playNotifSound();
            freshOnes.forEach(showNotifToast);
            if (missedCount > 0) showNotifOverflowToast(missedCount);
        }
    } catch (e) { console.error(e); }
}

// 拿 Web Audio API 现场合成一声短促的"叮"，不用额外准备音频文件、也不用等文件加载。
// 浏览器的自动播放限制只挡"没有用户交互就自己放视频/长音频"，这种毫秒级的短提示音不受影响。
function playNotifSound() {
    try {
        const ctx = new (window.AudioContext || window.webkitAudioContext)();
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.connect(gain); gain.connect(ctx.destination);
        osc.type = 'sine';
        osc.frequency.value = 880;                                   // 高音 A，清脆不刺耳
        gain.gain.setValueAtTime(0.15, ctx.currentTime);
        gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.35);   // 快速淡出，听起来像"叮"而不是长鸣
        osc.start();
        osc.stop(ctx.currentTime + 0.35);
    } catch (e) { /* 极少数环境不支持 Web Audio，静默跳过，不影响其它功能 */ }
}

// 右上角弹出一张新通知小卡片：点击后跟下拉框里点一条通知的效果一样（标记已读+跳转），
// 6 秒后没人点就自动淡出消失，避免一直堆在屏幕上挡内容
function showNotifToast(n) {
    const wrap = document.getElementById('notifToastWrap');
    const el = document.createElement('div');
    el.className = 'notif-toast';
    el.innerHTML = `
        <div class="notif-title">${escapeHtml(n.title)}</div>
        <div class="notif-content">${escapeHtml(n.content)}</div>`;
    el.onclick = () => { readNotif(n.id, n.notificationType, n.relatedId); el.remove(); };
    wrap.appendChild(el);
    setTimeout(() => el.remove(), 6000);
}

// 未读通知一次性来了太多、被 Take(15) 挤出列表的那几条没法弹出正常的通知卡片（没有具体的
// id/标题/内容可用），改弹一条通用汇总提示；点击效果是打开铃铛下拉框——下拉框同样只显示最新
// 15 条，但把这些标记已读后，后面攒着的会在下一次轮询里递补上来，一批一批就能看完
function showNotifOverflowToast(missedCount) {
    const wrap = document.getElementById('notifToastWrap');
    const el = document.createElement('div');
    el.className = 'notif-toast';
    el.innerHTML = `
        <div class="notif-title">还有新通知</div>
        <div class="notif-content">还有 ${missedCount} 条新通知未显示，点击查看</div>`;
    // 这张卡片不在 #notifBell 范围内：点击事件如果冒泡到 document，会被下面"点击铃铛/下拉框以外
    // 就关闭下拉框"的全局监听器当成"点了外面"，把刚打开的下拉框又立刻关掉——必须 stopPropagation
    // 挡住冒泡（点通知列表里普通一条、点"全部已读"按钮，都是同样的道理，见 markAllRead）
    el.onclick = e => { e.stopPropagation(); el.remove(); if (!notifOpen) toggleNotifDropdown(); };
    wrap.appendChild(el);
    setTimeout(() => el.remove(), 6000);
}

// 把用户能控制的自由文本（公告标题/正文、自助登记姓名等）转义成安全文本再拼进 HTML，
// 防止里面刚好含有 <script> 之类的内容被浏览器当成真正的代码执行（"存储型 XSS"）
function escapeHtml(s) {
    const d = document.createElement('div');
    d.textContent = s ?? '';
    // innerHTML 只转义 < > &，不转义引号——拼进 href="..." 这类属性值时，带引号的内容能跳出属性、塞进 onmouseover 之类的事件（2026-10-07 第 18 轮审查），所以引号也要转义
    return d.innerHTML.replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

// 点某一条通知：先告诉后台"这条我看过了"（标记已读），
// 审批相关的通知直接带去待审批页面方便马上处理；公告通知带去公告栏；不然就只是刷新一下通知列表
async function readNotif(id, type, relatedId) {
    await fetch(`/api/notifications/${id}/read`, { method: 'POST' });
    if (type === 'ApprovalPending' || type === 'ApprovalResult')
        location.href = '/Approval/PendingApproval';
    else if (type === 'Announcement')
        location.href = '/Notice/Board';
    else
        loadNotifications();
}

// "全部已读"按钮：把所有未读通知一次性标记成已读
// e.stopPropagation() 是为了防止这次点击又被上面"点击空白处关闭下拉框"的监听逻辑抢先处理掉
async function markAllRead(e) {
    e.stopPropagation();
    await fetch('/api/notifications/read-all', { method: 'POST' });
    loadNotifications();
}

// 页面一打开就先拉一次通知；之后每隔 60 秒自动再拉一次，
// 这样即使用户一直不点铃铛，未读数字气泡也能及时更新，不用手动刷新页面
loadNotifications();
setInterval(loadNotifications, 60000);
