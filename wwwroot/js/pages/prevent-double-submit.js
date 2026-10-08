// 防重复提交：同一个表单（POST）点一次提交后 15 秒内不再接受第二次提交，并把提交按钮置灰。
// 背景：连点按钮/回车+点击会让浏览器把同一个操作发两次，服务器处理完第一次后，第二次撞上"该登记不存在，
// 或已经被处理过了"之类的提示，把第一次的成功提示盖掉（新增员工/确认录入/新增考勤机都遇到过）。
// 15 秒后自动解锁：万一是"导出"这类不会跳转页面的 POST，也不至于按钮一直点不了。
(function () {
    const LOCK_MS = 15000;
    const locked = new Set();
    const isLocked = f => locked.has(f);
    const submitButtons = f => f.querySelectorAll('button[type=submit], input[type=submit], button:not([type])');

    function lock(f) {
        locked.add(f);
        // 延迟到提交动作真正发出去之后再置灰，否则按钮自带的 name/value 会随表单丢失
        setTimeout(() => submitButtons(f).forEach(b => { b.dataset.lockedBySubmit = '1'; b.disabled = true; }), 0);
        setTimeout(() => unlock(f), LOCK_MS);
    }
    function unlock(f) {
        locked.delete(f);
        submitButtons(f).forEach(b => { if (b.dataset.lockedBySubmit) { b.disabled = false; delete b.dataset.lockedBySubmit; } });
    }
    const needGuard = f => (f.method || 'get').toLowerCase() === 'post' && f.target !== '_blank';

    // 用户点提交按钮/按回车。放在冒泡阶段：表单自己的 onsubmit（比如"确认删除吗？"点了取消）先执行，
    // 已经被取消的提交不该上锁
    document.addEventListener('submit', function (e) {
        const f = e.target;
        if (e.defaultPrevented || !needGuard(f)) return;
        if (isLocked(f)) { e.preventDefault(); return; }
        lock(f);
    });

    // 脚本里直接调 form.submit()（比如员工管理页的"停用/启用"按钮）不会触发 submit 事件，这里一并拦
    const rawSubmit = HTMLFormElement.prototype.submit;
    HTMLFormElement.prototype.submit = function () {
        if (needGuard(this)) {
            if (isLocked(this)) return;
            lock(this);
        }
        return rawSubmit.call(this);
    };

    // 点浏览器"后退"回到这个页面时（页面从缓存恢复），把锁清掉，不然按钮会一直是灰的
    window.addEventListener('pageshow', function (e) {
        if (e.persisted) Array.from(locked).forEach(unlock);
    });
})();
