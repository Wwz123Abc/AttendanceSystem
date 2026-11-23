// 统一的日期/时间文本输入格式化（2026-11-20）
// 目的：移动端不再依赖浏览器原生的 date/time/datetime-local 控件——部分浏览器会"显示有值但提交为空"。
// 用法：<input type="text" data-dt="date|time|datetime" ... />
// 输入纯数字也能自动补分隔符：
//   20261116        -> 2026-11-16
//   2030 / 930      -> 20:30 / 09:30
//   202611162030    -> 2026-11-16T20:30
(function () {
    'use strict';

    function digitsOnly(v) {
        return (v || '').replace(/：/g, ':').replace(/[^\d]/g, '');
    }

    function normalize(el) {
        if (!el || !el.dataset || !el.dataset.dt) return;
        var kind = el.dataset.dt;
        var d = digitsOnly(el.value);

        if (kind === 'time') {
            if (d.length === 3) d = '0' + d;
            if (d.length >= 4) el.value = d.slice(0, 2) + ':' + d.slice(2, 4);
            return;
        }

        if (kind === 'date') {
            if (d.length >= 8) el.value = d.slice(0, 4) + '-' + d.slice(4, 6) + '-' + d.slice(6, 8);
            return;
        }

        if (kind === 'datetime') {
            if (d.length >= 12) {
                el.value = d.slice(0, 4) + '-' + d.slice(4, 6) + '-' + d.slice(6, 8) +
                           'T' + d.slice(8, 10) + ':' + d.slice(10, 12);
            } else if (d.length === 8 && /:/.test(el.value)) {
                // 只写了"月-日 时:分"（如 11-21 22:47）：补上今年，避免被当成 8 位日期截断
                el.value = new Date().getFullYear() + '-' + d.slice(0, 2) + '-' + d.slice(2, 4) +
                           'T' + d.slice(4, 6) + ':' + d.slice(6, 8);
            }
            // 其他不完整的写法保持原样，交给服务器容错解析（服务器会补年份/用开始日期补结束时间）
            return;
        }
    }

    function bind() {
        // 把所有还依赖浏览器原生控件的日期/时间输入统一换成文本框（2026-11-20）：
        // 原生控件在部分手机浏览器上会"显示有值但提交为空"；换成文本框后由本脚本统一格式化。
        var native = document.querySelectorAll('input[type="date"], input[type="time"], input[type="datetime-local"]');
        for (var n = 0; n < native.length; n++) {
            var raw = native[n];
            if (!raw.dataset.dt) {
                var kind = raw.type === 'date' ? 'date' : (raw.type === 'time' ? 'time' : 'datetime');
                raw.dataset.dt = kind;
                raw.type = 'text';
                if (!raw.placeholder) {
                    raw.placeholder = kind === 'date' ? '2026-11-16' : (kind === 'time' ? '20:30' : '2026-11-16 20:30');
                }
                if (!raw.getAttribute('inputmode')) raw.setAttribute('inputmode', 'numeric');
                if (!raw.getAttribute('autocomplete')) raw.setAttribute('autocomplete', 'off');
            }
        }

        var inputs = document.querySelectorAll('input[data-dt]');
        for (var i = 0; i < inputs.length; i++) {
            var el = inputs[i];
            el.addEventListener('blur', function () { normalize(this); });
            // 边输边格式化（只在"纯数字、光标在末尾"时做，避免打断修改中间的数字）：
            // 输满 8 位日期 / 4 位时间 / 12 位日期时间，立刻补上分隔符，手机上不用找冒号横杠。
            el.addEventListener('input', function () {
                var kind = this.dataset.dt;
                if (!kind) return;
                var raw = this.value || '';
                if (/[-:\/\s]/.test(raw)) return;
                if (typeof this.selectionStart === 'number' && this.selectionStart !== raw.length) return;
                var d = raw.replace(/\D/g, '');
                if (kind === 'date' && d.length >= 8) {
                    this.value = d.slice(0, 4) + '-' + d.slice(4, 6) + '-' + d.slice(6, 8);
                } else if (kind === 'time' && d.length >= 4) {
                    this.value = d.slice(0, 2) + ':' + d.slice(2, 4);
                } else if (kind === 'datetime' && d.length >= 12) {
                    this.value = d.slice(0, 4) + '-' + d.slice(4, 6) + '-' + d.slice(6, 8) +
                                 'T' + d.slice(8, 10) + ':' + d.slice(10, 12);
                }
            });
        }
        // 在页面自己的 submit 处理之前格式化（capture 阶段），保证后续比较/提交都用标准写法
        var forms = document.querySelectorAll('form');
        for (var j = 0; j < forms.length; j++) {
            forms[j].addEventListener('submit', function () {
                var list = this.querySelectorAll('input[data-dt]');
                for (var k = 0; k < list.length; k++) normalize(list[k]);
            }, true);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', bind);
    } else {
        bind();
    }
})();
