// 提交申请页专用“选日期+时间”小工具：只给本页的时间框加按钮，不监听整页 DOM。
// 2026-11-24：员工反馈手输数字太麻烦；点开选年/月/日/时/分，点确定即可。
// 日期/“现在”都以服务器时间为准，避免手机时钟被设错（曾出现年份跳到 2626）。
(function () {
    'use strict';

    var HOURS = [], MINUTES = [];
    for (var h = 0; h < 24; h++) HOURS.push((h < 10 ? '0' : '') + h);
    for (var m = 0; m < 60; m += 5) MINUTES.push((m < 10 ? '0' : '') + m);

    var currentInput = null;
    var panel = null, backdrop = null;
    var yearSel = null, monthSel = null, daySel = null, hourSel = null, minuteSel = null, dateRow = null;

    function pad(n) { return n < 10 ? '0' + n : String(n); }

    function serverToday() {
        // 员工手机时钟可能被设成 2626 年，日期补全不能读手机本地时间
        if (window.APP_TODAY) return window.APP_TODAY;
        var d = new Date();
        return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
    }

    function serverNowTime() {
        var m = String(window.APP_NOW_TIME || '').match(/^(\d{1,2}):(\d{2})/);
        if (m) return { h: pad(parseInt(m[1], 10) % 24), m: m[2] };
        var d = new Date();
        return { h: pad(d.getHours()), m: pad(d.getMinutes()) };
    }

    function dateIn(value) {
        var m = String(value || '').match(/(\d{4})-(\d{2})-(\d{2})/);
        return m ? m[0] : '';
    }

    // 结束时间没填日期时，优先沿用同组开始时间的日期，避免选完变成“今天”而错一天
    function fallbackDate(input) {
        if (input && /End$/.test(input.id || '')) {
            var start = document.getElementById(input.id.replace(/End$/, 'Start'));
            var d = start ? dateIn(start.value) : '';
            if (d) return d;
        }
        return '';
    }

    // 补卡的“打卡时间”旁边有一个独立的“补卡日期”输入框，选时间时一起选日期
    function pairedDateInput(input) {
        if (input.getAttribute('data-dt') !== 'time') return null;
        return document.getElementById((input.id || '').replace(/Time$/, 'Date'));
    }

    function showDateFor(input) {
        return input.getAttribute('data-dt') === 'datetime' || pairedDateInput(input) !== null;
    }

    function currentTime(value) {
        var m = String(value || '').match(/(\d{1,2}):(\d{2})/);
        if (m) return { h: pad(parseInt(m[1], 10) % 24), m: m[2] };
        return serverNowTime();
    }

    function ensureMinuteOption(mm) {
        if (MINUTES.indexOf(mm) >= 0) return;
        var opt = document.createElement('option');
        opt.value = mm; opt.textContent = mm;
        minuteSel.appendChild(opt);
        MINUTES.push(mm);
    }

    function serverYear() {
        return parseInt((window.APP_TODAY || '2026-01-01').slice(0, 4), 10) || new Date().getFullYear();
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
            '.tp-panel{position:fixed;z-index:1999;left:50%;top:50%;transform:translate(-50%,-50%);width:min(360px,calc(100vw - 28px));background:#fff;border-radius:14px;box-shadow:0 18px 50px rgba(16,24,40,.28);display:none;overflow:hidden}',
            '.tp-head{display:flex;align-items:center;justify-content:space-between;padding:.8rem 1rem;border-bottom:1px solid #eef1f5;font-weight:600}',
            '.tp-close{border:0;background:transparent;font-size:1.3rem;line-height:1;color:#8b93a3;cursor:pointer}',
            '.tp-body{padding:.9rem 1rem 1rem}',
            '.tp-date-row{display:flex;align-items:center;gap:.3rem;margin-bottom:.7rem}',
            '.tp-date-row select{flex:1 1 0;min-width:0;border:1px solid #d5dbe5;border-radius:8px;padding:.5rem .35rem;background:#fff;font-size:.95rem}',
            '.tp-date-row span{font-size:.78rem;color:#667085;flex:0 0 auto}',
            '.tp-row{display:flex;align-items:center;gap:.6rem}',
            '.tp-row select{flex:1 1 0;min-width:0;border:1px solid #d5dbe5;border-radius:8px;padding:.55rem .5rem;background:#fff;font-size:1rem}',
            '.tp-split{color:#98a2b3}',
            '.tp-actions{display:flex;gap:.4rem;margin-top:.9rem}',
            '.tp-actions button{flex:1;border-radius:8px;padding:.52rem .2rem;border:1px solid #d5dbe5;background:#fff;font-size:.86rem;white-space:nowrap}',
            '.tp-actions .tp-ok{background:#1456cc;border-color:#1456cc;color:#fff}',
            '.tp-actions .tp-now,.tp-actions .tp-today{color:#1456cc}',
            '@media(max-width:600px){.tp-panel{left:8px;right:8px;top:auto;bottom:10px;transform:none;width:auto}}'
        ].join('');
        document.head.appendChild(style);

        backdrop = document.createElement('div');
        backdrop.className = 'tp-backdrop';
        backdrop.addEventListener('click', close);

        panel = document.createElement('div');
        panel.className = 'tp-panel';
        panel.innerHTML =
            '<div class="tp-head"><span>选择日期时间</span><button type="button" class="tp-close" aria-label="关闭">×</button></div>' +
            '<div class="tp-body">' +
            '  <div class="tp-date-row">' +
            '    <select class="tp-year" aria-label="年"></select><span>年</span>' +
            '    <select class="tp-month" aria-label="月"></select><span>月</span>' +
            '    <select class="tp-day" aria-label="日"></select><span>日</span>' +
            '  </div>' +
            '  <div class="tp-row">' +
            '    <select class="tp-hour" aria-label="小时"></select><span class="tp-split">:</span><select class="tp-minute" aria-label="分钟"></select>' +
            '  </div>' +
            '  <div class="tp-actions">' +
            '    <button type="button" class="tp-today">今天</button>' +
            '    <button type="button" class="tp-now">现在</button>' +
            '    <button type="button" class="tp-cancel">取消</button>' +
            '    <button type="button" class="tp-ok">确定</button>' +
            '  </div>' +
            '</div>';
        document.body.appendChild(backdrop);
        document.body.appendChild(panel);

        yearSel = panel.querySelector('.tp-year');
        monthSel = panel.querySelector('.tp-month');
        daySel = panel.querySelector('.tp-day');
        hourSel = panel.querySelector('.tp-hour');
        minuteSel = panel.querySelector('.tp-minute');
        dateRow = panel.querySelector('.tp-date-row');

        var y = serverYear();
        for (var yy = y - 1; yy <= y + 2; yy++) {
            var oy = document.createElement('option');
            oy.value = String(yy); oy.textContent = String(yy);
            yearSel.appendChild(oy);
        }
        for (var mm = 1; mm <= 12; mm++) {
            var om = document.createElement('option');
            om.value = pad(mm); om.textContent = pad(mm);
            monthSel.appendChild(om);
        }
        HOURS.forEach(function (x) { var o = document.createElement('option'); o.value = x; o.textContent = x; hourSel.appendChild(o); });
        MINUTES.forEach(function (x) { var o = document.createElement('option'); o.value = x; o.textContent = x; minuteSel.appendChild(o); });

        yearSel.addEventListener('change', function () { rebuildDays(); });
        monthSel.addEventListener('change', function () { rebuildDays(); });
        panel.querySelector('.tp-close').addEventListener('click', close);
        panel.querySelector('.tp-cancel').addEventListener('click', close);
        panel.querySelector('.tp-ok').addEventListener('click', apply);
        panel.querySelector('.tp-today').addEventListener('click', function () {
            setDateSelectors(serverToday());
        });
        panel.querySelector('.tp-now').addEventListener('click', function () {
            var t = serverNowTime();
            setDateSelectors(serverToday());
            hourSel.value = t.h;
            ensureMinuteOption(t.m);
            minuteSel.value = t.m;
        });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && panel.style.display === 'block') close();
        });
    }

    function daysInMonth(y, m) {
        return new Date(parseInt(y, 10), parseInt(m, 10), 0).getDate();
    }

    function rebuildDays() {
        var keep = daySel.value || '01';
        var count = daysInMonth(yearSel.value, monthSel.value);
        daySel.innerHTML = '';
        for (var d = 1; d <= count; d++) {
            var od = document.createElement('option');
            od.value = pad(d); od.textContent = pad(d);
            daySel.appendChild(od);
        }
        daySel.value = parseInt(keep, 10) <= count ? keep : pad(count);
    }

    function setDateSelectors(dateText) {
        var m = String(dateText || '').match(/^(\d{4})-(\d{2})-(\d{2})$/);
        if (!m) return false;
        var y = m[1];
        if (!Array.prototype.some.call(yearSel.options, function (o) { return o.value === y; })) return false;
        yearSel.value = y;
        monthSel.value = m[2];
        rebuildDays();
        daySel.value = m[3];
        return true;
    }

    function open(input) {
        buildPanel();
        currentInput = input;
        var t = currentTime(input.value);
        var showDate = showDateFor(input);
        dateRow.style.display = showDate ? 'flex' : 'none';
        if (showDate) {
            var paired = pairedDateInput(input);
            var d = dateIn(input.value) || dateIn(paired ? paired.value : '') || fallbackDate(input) || serverToday();
            if (!setDateSelectors(d)) setDateSelectors(serverToday());
        }
        var hasHour = HOURS.indexOf(t.h) >= 0;
        hourSel.value = hasHour ? t.h : serverNowTime().h;
        ensureMinuteOption(t.m);
        minuteSel.value = t.m;

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
        var paired = pairedDateInput(currentInput);
        var showDate = showDateFor(currentInput);

        if (kind === 'time' && !paired) {
            currentInput.value = time;
        } else {
            var d = showDate
                ? yearSel.value + '-' + monthSel.value + '-' + daySel.value
                : (dateIn(currentInput.value) || fallbackDate(currentInput) || serverToday());
            if (kind === 'time' && paired) {
                paired.value = d;
                paired.dispatchEvent(new Event('input', { bubbles: true }));
                paired.dispatchEvent(new Event('change', { bubbles: true }));
                currentInput.value = time;
            } else {
                currentInput.value = d + ' ' + time;
            }
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
