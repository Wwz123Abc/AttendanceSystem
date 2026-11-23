// 自定义日期/时间选择器（2026-11-23）
// 目的：不依赖浏览器原生 date/time 控件（部分手机浏览器会"显示有值但提交为空"），
// 同时保留"点日历选择"的方便，不用手输数字。
// 每个 [data-dt] 输入框后面加一个 📅 / 🕒 按钮，点开是自绘日历 + 时/分下拉。
(function () {
    'use strict';

    var WEEK = ['日', '一', '二', '三', '四', '五', '六'];
    var activeInput = null;
    var state = { kind: 'date', date: new Date(), hour: 0, minute: 0 };
    var panel = null, backdrop = null, wrapCount = 0;

    function pad(n) { return n < 10 ? '0' + n : '' + n; }
    function ymd(d) { return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()); }
    function hm(h, m) { return pad(h) + ':' + pad(m); }

    function parseValue(kind, value) {
        var now = new Date();
        var out = { date: new Date(now.getFullYear(), now.getMonth(), now.getDate()), hour: now.getHours(), minute: now.getMinutes() };
        if (!value) return out;
        var v = String(value).replace('T', ' ').trim();
        var m = v.match(/^(\d{4})-(\d{1,2})-(\d{1,2})(?:[ ](\d{1,2}):(\d{2}))?$/);
        if (m) {
            out.date = new Date(+m[1], +m[2] - 1, +m[3]);
            if (m[4]) { out.hour = +m[4]; out.minute = +m[5]; }
            return out;
        }
        // 没写年份：11-23 / 11-23 18:30
        m = v.match(/^(\d{1,2})-(\d{1,2})(?:[ ](\d{1,2}):(\d{2}))?$/);
        if (m) {
            out.date = new Date(now.getFullYear(), +m[1] - 1, +m[2]);
            if (m[3]) { out.hour = +m[3]; out.minute = +m[4]; }
            return out;
        }
        // 只写时间：18:30
        m = v.match(/^(\d{1,2}):(\d{2})$/);
        if (m) { out.hour = +m[1]; out.minute = +m[2]; }
        return out;
    }

    function ensurePanel() {
        if (panel) return;
        var style = document.createElement('style');
        style.textContent = [
            '.dt-pick-wrap{display:flex;gap:.35rem;align-items:center;width:100%}',
            '.dt-pick-wrap>input{flex:1 1 auto;min-width:0}',
            '.dt-pick-btn{flex:0 0 auto;border:1px solid #d5dbe5;background:#fff;border-radius:8px;padding:.36rem .5rem;line-height:1;font-size:1rem;cursor:pointer}',
            '.dt-pick-btn:hover{background:#f2f6ff;border-color:#1456cc}',
            '.dt-backdrop{position:fixed;top:0;left:0;right:0;bottom:0;background:rgba(16,24,40,.18);z-index:1998;display:none}',
            '.dt-panel{position:fixed;z-index:1999;background:#fff;border:1px solid #d9dee7;border-radius:12px;box-shadow:0 14px 36px rgba(16,24,40,.22);padding:12px;width:300px;max-height:82vh;overflow:auto;display:none;font-size:14px;color:#27303f}',
            '.dt-head{display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:8px}',
            '.dt-head button{border:0;background:#f2f4f8;border-radius:8px;width:30px;height:30px;font-size:16px;line-height:1;cursor:pointer}',
            '.dt-title{font-weight:600}',
            '.dt-grid{display:grid;grid-template-columns:repeat(7,1fr);gap:2px}',
            '.dt-week{color:#8a93a3;font-size:12px;text-align:center;padding:3px 0}',
            '.dt-cell{border:0;background:transparent;border-radius:7px;text-align:center;padding:6px 0;font-size:13px;cursor:pointer;color:#27303f}',
            '.dt-cell:hover{background:#eef3ff}',
            '.dt-cell.muted{color:#c2c8d3;cursor:default}',
            '.dt-cell.today{box-shadow:inset 0 0 0 1px #1456cc}',
            '.dt-cell.sel{background:#1456cc;color:#fff}',
            '.dt-time{display:flex;align-items:center;gap:8px;margin-top:10px}',
            '.dt-time select{flex:1;border:1px solid #d5dbe5;border-radius:8px;padding:6px;background:#fff}',
            '.dt-foot{display:flex;gap:6px;margin-top:10px}',
            '.dt-foot button{flex:1;border:1px solid #d5dbe5;background:#fff;border-radius:8px;padding:7px 0;font-size:13px;cursor:pointer}',
            '.dt-foot button[data-act="ok"]{background:#1456cc;border-color:#1456cc;color:#fff}',
            '@media(max-width:600px){.dt-panel{left:8px!important;right:8px!important;top:auto!important;bottom:10px;width:auto}}'
        ].join('\n');
        document.head.appendChild(style);

        backdrop = document.createElement('div');
        backdrop.className = 'dt-backdrop';
        backdrop.addEventListener('click', close);
        document.body.appendChild(backdrop);

        panel = document.createElement('div');
        panel.className = 'dt-panel';
        panel.addEventListener('click', onPanelClick);
        panel.addEventListener('change', onPanelChange);
        document.body.appendChild(panel);
    }

    function open(input) {
        ensurePanel();
        activeInput = input;
        state.kind = input.dataset.dt;
        var parsed = parseValue(state.kind, input.value);
        state.date = parsed.date;
        state.hour = parsed.hour;
        state.minute = parsed.minute;
        render();
        position(input);
        panel.style.display = 'block';
        backdrop.style.display = 'block';
    }

    function close() {
        if (panel) panel.style.display = 'none';
        if (backdrop) backdrop.style.display = 'none';
        activeInput = null;
    }

    function position(input) {
        var r = input.getBoundingClientRect();
        var w = panel.offsetWidth || 300;
        var h = panel.offsetHeight || 340;
        var left = Math.min(Math.max(8, r.left), window.innerWidth - w - 8);
        var top = r.bottom + 6;
        if (top + h > window.innerHeight - 8) top = Math.max(8, r.top - h - 6);
        panel.style.left = left + 'px';
        panel.style.top = top + 'px';
    }

    function render() {
        var kind = state.kind;
        var y = state.date.getFullYear(), m = state.date.getMonth(), d = state.date.getDate();
        var html = '';

        if (kind !== 'time') {
            html += '<div class="dt-head"><button type="button" data-nav="-1">‹</button><div class="dt-title">' + y + '年' + (m + 1) + '月</div><button type="button" data-nav="1">›</button></div>';
            html += '<div class="dt-grid">' + WEEK.map(function (w) { return '<div class="dt-week">' + w + '</div>'; }).join('') + '</div>';
            var first = new Date(y, m, 1).getDay();
            var days = new Date(y, m + 1, 0).getDate();
            var cells = '';
            for (var i = 0; i < first; i++) cells += '<div class="dt-cell muted"></div>';
            for (var day = 1; day <= days; day++) {
                var cls = 'dt-cell';
                if (day === d) cls += ' sel';
                var today = new Date();
                if (day === today.getDate() && m === today.getMonth() && y === today.getFullYear()) cls += ' today';
                cells += '<button type="button" class="' + cls + '" data-day="' + day + '">' + day + '</button>';
            }
            html += '<div class="dt-grid">' + cells + '</div>';
        }

        if (kind !== 'date') {
            var hours = '', mins = '';
            for (var h = 0; h < 24; h++) hours += '<option value="' + h + '"' + (h === state.hour ? ' selected' : '') + '>' + pad(h) + '</option>';
            for (var mi = 0; mi < 60; mi++) mins += '<option value="' + mi + '"' + (mi === state.minute ? ' selected' : '') + '>' + pad(mi) + '</option>';
            html += '<div class="dt-time"><select data-h>' + hours + '</select><span>:</span><select data-m>' + mins + '</select></div>';
        }

        html += '<div class="dt-foot">';
        if (kind !== 'time') html += '<button type="button" data-act="today">今天</button>';
        if (kind !== 'date') html += '<button type="button" data-act="now">现在</button>';
        html += '<button type="button" data-act="clear">清空</button><button type="button" data-act="ok">确定</button></div>';

        panel.innerHTML = html;
    }

    function onPanelClick(e) {
        var t = e.target;
        if (!t || !t.getAttribute) return;
        var nav = t.getAttribute('data-nav');
        if (nav) {
            state.date = new Date(state.date.getFullYear(), state.date.getMonth() + (+nav), 1);
            render();
            return;
        }
        var day = t.getAttribute('data-day');
        if (day) {
            state.date = new Date(state.date.getFullYear(), state.date.getMonth(), +day);
            if (state.kind === 'date') { applyAndClose(); return; }
            render();
            return;
        }
        var act = t.getAttribute('data-act');
        if (!act) return;
        if (act === 'today') {
            var n = new Date();
            state.date = new Date(n.getFullYear(), n.getMonth(), n.getDate());
            render();
            return;
        }
        if (act === 'now') {
            var now = new Date();
            state.date = new Date(now.getFullYear(), now.getMonth(), now.getDate());
            state.hour = now.getHours();
            state.minute = now.getMinutes();
            render();
            return;
        }
        if (act === 'clear') {
            activeInput.value = '';
            done();
            return;
        }
        if (act === 'ok') applyAndClose();
    }

    function onPanelChange(e) {
        var t = e.target;
        if (!t) return;
        if (t.hasAttribute('data-h')) state.hour = +t.value;
        if (t.hasAttribute('data-m')) state.minute = +t.value;
    }

    function applyAndClose() {
        var kind = state.kind;
        var v = kind === 'time'
            ? hm(state.hour, state.minute)
            : ymd(state.date) + (kind === 'datetime' ? ' ' + hm(state.hour, state.minute) : '');
        activeInput.value = v;
        done();
    }

    function done() {
        var input = activeInput;
        close();
        try {
            input.dispatchEvent(new Event('input', { bubbles: true }));
            input.dispatchEvent(new Event('change', { bubbles: true }));
        } catch (e) {
            var ev = document.createEvent('HTMLEvents');
            ev.initEvent('change', true, false);
            input.dispatchEvent(ev);
        }
    }

    function init() {
        var inputs = document.querySelectorAll('input[data-dt]');
        for (var i = 0; i < inputs.length; i++) {
            var input = inputs[i];
            if (input.disabled || input.dataset.dtPickerReady === '1') continue;
            input.dataset.dtPickerReady = '1';

            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'dt-pick-btn';
            btn.textContent = input.dataset.dt === 'time' ? '🕒' : '📅';
            btn.title = input.dataset.dt === 'time' ? '选择时间' : '选择日期';
            btn.addEventListener('click', (function (el) {
                return function (e) { e.preventDefault(); open(el); };
            })(input));

            var wrap = document.createElement('div');
            wrap.className = 'dt-pick-wrap';
            input.parentNode.insertBefore(wrap, input);
            wrap.appendChild(input);
            wrap.appendChild(btn);
            if (!input.placeholder) {
                input.placeholder = input.dataset.dt === 'time' ? '20:30' :
                    (input.dataset.dt === 'date' ? '2026-11-23' : '2026-11-23 18:30');
            }
            wrapCount++;
        }
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') close();
        });

        // 页面里动态加出来的行（饭点设置"添加一行"、班次午间卡"添加一段"）也要有选择按钮
        if (window.MutationObserver) {
            var observer = new MutationObserver(function () { init(); });
            observer.observe(document.body, { childList: true, subtree: true });
        }
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
