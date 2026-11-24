// 提交申请页专用的“选时间”小工具：只给本页的日期/时间框加按钮，不再全站挂选择器。
// 2026-11-24：员工反馈直接输数字太麻烦；这里用“小时/分钟下拉 + 确定”，点两下即可，避免手输。
// 不监听整页 DOM 变化、不用 MutationObserver，页面加载时初始化一次。
(function () {
    'use strict';

    var HOURS = [], MINUTES = [];
    for (var h = 0; h < 24; h++) HOURS.push((h < 10 ? '0' : '') + h);
    for (var m = 0; m < 60; m += 5) MINUTES.push((m < 10 ? '0' : '') + m);

    var currentInput = null;
    var panel = null, backdrop = null, hourSel = null, minuteSel = null, dateText = null;

    function pad(n) { return n < 10 ? '0' + n : String(n); }

    function todayText() {
        var d = new Date();
        return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
    }

    function dateIn(value) {
        var m = String(value || '').match(/\d{4}-\d{2}-\d{2}/);
        return m ? m[0] : '';
    }

    // 结束时间没填日期时，优先沿用同组开始时间的日期，避免选完时间变成“今天”而错一天
    function fallbackDate(input) {
        if (input && /End$/.test(input.id || '')) {
            var start = document.getElementById(input.id.replace(/End$/, 'Start'));
            var d = start ? dateIn(start.value) : '';
            if (d) return d;
        }
        return '';
    }

    function currentTime(value) {
        var m = String(value || '').match(/(\d{1,2}):(\d{2})/);
        if (m) return { h: pad(parseInt(m[1], 10) % 24), m: m[2] };
        var d = new Date();
        return { h: pad(d.getHours()), m: pad(d.getMinutes()) };
    }

    function ensureMinuteOption(mm) {
        if (MINUTES.indexOf(mm) >= 0) return;
        var opt = document.createElement('option');
        opt.value = mm; opt.textContent = mm;
        minuteSel.appendChild(opt);
        MINUTES.push(mm);
    }

    function buildPanel() {
        if (panel) return;

        var style = document.createElement('style');
        style.textContent = [
            '.tp-wrap{display:flex;gap:.4rem;align-items:center;width:100%}',
            '.tp-wrap>input{flex:1 1 auto;min-width:0}',
            '.tp-btn{flex:0 0 auto;border:1px solid #d5dbe5;background:#fff;border-radius:8px;padding:.36rem .55rem;line-height:1.2;font-size:.85rem;color:#1456cc;white-space:nowrap}',
            '.tp-btn:hover{background:#f2f6ff;border-color:#1456cc}',
            '.tp-backdrop{position:fixed;inset:0;background:rgba(15,23,42,.38);z-index:1998;display:none}',
            '.tp-panel{position:fixed;z-index:1999;left:50%;top:50%;transform:translate(-50%,-50%);width:min(340px,calc(100vw - 32px));background:#fff;border-radius:14px;box-shadow:0 18px 50px rgba(16,24,40,.28);display:none;overflow:hidden}',
            '.tp-head{display:flex;align-items:center;justify-content:space-between;padding:.85rem 1rem;border-bottom:1px solid #eef1f5;font-weight:600}',
            '.tp-close{border:0;background:transparent;font-size:1.3rem;line-height:1;color:#8b93a3;cursor:pointer}',
            '.tp-body{padding:1rem}',
            '.tp-date{font-size:.82rem;color:#667085;margin-bottom:.7rem;background:#f7f9fc;border-radius:8px;padding:.45rem .6rem}',
            '.tp-row{display:flex;align-items:center;gap:.6rem}',
            '.tp-row select{flex:1 1 0;min-width:0;border:1px solid #d5dbe5;border-radius:8px;padding:.55rem .5rem;background:#fff;font-size:1rem}',
            '.tp-split{color:#98a2b3}',
            '.tp-actions{display:flex;gap:.5rem;margin-top:1rem}',
            '.tp-actions button{flex:1;border-radius:8px;padding:.55rem;border:1px solid #d5dbe5;background:#fff;font-size:.92rem}',
            '.tp-actions .tp-ok{background:#1456cc;border-color:#1456cc;color:#fff}',
            '.tp-actions .tp-now{color:#1456cc}',
            '@media(max-width:600px){.tp-panel{left:8px;right:8px;top:auto;bottom:10px;transform:none;width:auto}}'
        ].join('');
        document.head.appendChild(style);

        backdrop = document.createElement('div');
        backdrop.className = 'tp-backdrop';
        backdrop.addEventListener('click', close);

        panel = document.createElement('div');
        panel.className = 'tp-panel';
        panel.innerHTML =
            '<div class="tp-head"><span>选择时间</span><button type="button" class="tp-close" aria-label="关闭">×</button></div>' +
            '<div class="tp-body">' +
            '  <div class="tp-date"></div>' +
            '  <div class="tp-row">' +
            '    <select class="tp-hour" aria-label="小时"></select><span class="tp-split">:</span><select class="tp-minute" aria-label="分钟"></select>' +
            '  </div>' +
            '  <div class="tp-actions">' +
            '    <button type="button" class="tp-now">现在</button>' +
            '    <button type="button" class="tp-cancel">取消</button>' +
            '    <button type="button" class="tp-ok">确定</button>' +
            '  </div>' +
            '</div>';
        document.body.appendChild(backdrop);
        document.body.appendChild(panel);

        hourSel = panel.querySelector('.tp-hour');
        minuteSel = panel.querySelector('.tp-minute');
        dateText = panel.querySelector('.tp-date');
        HOURS.forEach(function (x) { var o = document.createElement('option'); o.value = x; o.textContent = x; hourSel.appendChild(o); });
        MINUTES.forEach(function (x) { var o = document.createElement('option'); o.value = x; o.textContent = x; minuteSel.appendChild(o); });

        panel.querySelector('.tp-close').addEventListener('click', close);
        panel.querySelector('.tp-cancel').addEventListener('click', close);
        panel.querySelector('.tp-ok').addEventListener('click', apply);
        panel.querySelector('.tp-now').addEventListener('click', function () {
            var d = new Date();
            hourSel.value = pad(d.getHours());
            ensureMinuteOption(pad(d.getMinutes()));
            minuteSel.value = pad(d.getMinutes());
        });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && panel.style.display === 'block') close();
        });
    }

    function open(input) {
        buildPanel();
        currentInput = input;
        var t = currentTime(input.value);
        var hasHour = HOURS.indexOf(t.h) >= 0;
        hourSel.value = hasHour ? t.h : HOURS[new Date().getHours()];
        ensureMinuteOption(t.m);
        minuteSel.value = t.m;

        var isTimeOnly = input.getAttribute('data-dt') === 'time';
        var d = dateIn(input.value) || fallbackDate(input) || todayText();
        dateText.style.display = isTimeOnly ? 'none' : '';
        dateText.textContent = isTimeOnly
            ? ''
            : '日期：' + d + '（日期可在输入框修改，这里只选时间）';

        backdrop.style.display = 'block';
        panel.style.display = 'block';
    }

    function close() {
        if (panel) panel.style.display = 'none';
        if (backdrop) backdrop.style.display = 'none';
        currentInput = null;
    }

    function apply() {
        if (!currentInput) return close();
        var time = hourSel.value + ':' + minuteSel.value;
        var kind = currentInput.getAttribute('data-dt');
        if (kind === 'time') {
            currentInput.value = time;
        } else {
            var d = dateIn(currentInput.value) || fallbackDate(currentInput) || todayText();
            currentInput.value = d + ' ' + time;
        }
        currentInput.dispatchEvent(new Event('input', { bubbles: true }));
        currentInput.dispatchEvent(new Event('change', { bubbles: true }));
        close();
        currentInput.focus();
    }

    function init() {
        var inputs = document.querySelectorAll('input[data-dt="time"], input[data-dt="datetime"]');
        for (let i = 0; i < inputs.length; i++) {
            const input = inputs[i];
            if (input.getAttribute('data-tp-ready') === '1') continue;
            input.setAttribute('data-tp-ready', '1');
            var wrap = document.createElement('div');
            wrap.className = 'tp-wrap';
            input.parentNode.insertBefore(wrap, input);
            wrap.appendChild(input);
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'tp-btn';
            btn.textContent = '选时间';
            btn.addEventListener('click', function (ev) {
                ev.preventDefault();
                open(input);
            });
            wrap.appendChild(btn);
        }
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
