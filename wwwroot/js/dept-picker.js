/*
 * 部门选择器（全系统共用）。两种用法，都是"渐进增强"：页面原有的 <select> 和复选框树照常工作、照常提交表单，
 * 这个脚本只是把它们换成更好用的界面，不需要改后端。
 *
 * 1) 单选下拉：给 <select> 加 data-dept-picker
 *    把一长串靠全角空格缩进的下拉，换成"可搜索的树形选择器"：
 *      · 按钮上显示选中部门的完整路径（总公司 / 科瑞科技 / 生产部），不再只看到末级名字；
 *      · 展开后可以输入关键字搜索（匹配部门名或完整路径），搜索结果带路径、关键字高亮；
 *      · 不搜索时是可折叠的树，默认展开前两层和已选部门所在的路径；
 *      · 键盘：↑↓ 移动、Enter 选中、Esc 关闭；
 *      · 面板挂在 <body> 上，在弹窗里也不会被裁掉；窄屏时占满宽度。
 *    原来的 <select> 仍然存在（只是隐藏），选中后会同步它的值并触发 change 事件，
 *    所以页面里原有的 onchange、表单提交、脚本里 select.value = x 的写法都不用改。
 *    层级靠选项文字前面的全角空格（　）个数判断——页面里本来就是这么缩进的。
 *
 * 2) 多选树：给容器加 data-dept-tree（容器里是一行一个 .form-check 的复选框，带 data-id / data-parent）
 *    加上：搜索框、展开全部/收起全部、全选/清空、"已选 N 个"、每一行可折叠、
 *    父部门"部分勾选"的半选状态。原有的"勾父部门连带勾子部门"的逻辑不动。
 */
(function () {
    'use strict';

    var FW = '　';                       // 全角空格：页面里用它做缩进
    var openPicker = null;                   // 同一时间只开一个面板

    function esc(s) {
        return String(s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function highlight(text, q) {
        if (!q) return esc(text);
        var i = text.toLowerCase().indexOf(q.toLowerCase());
        if (i < 0) return esc(text);
        return esc(text.slice(0, i)) + '<mark>' + esc(text.slice(i, i + q.length)) + '</mark>' + esc(text.slice(i + q.length));
    }

    /* ───────────────────────── 单选下拉 ───────────────────────── */

    function enhanceSelect(select) {
        if (select.dataset.deptPickerReady) return;
        select.dataset.deptPickerReady = '1';

        var items = [];          // {i, value, name, depth, parent, path, hasChildren, isEmpty}
        var stack = [];
        var emptyItem = null;

        function readOptions() {
            items = []; stack = []; emptyItem = null;
            Array.prototype.forEach.call(select.options, function (o, i) {
                var raw = o.text, d = 0;
                while (raw.charAt(d) === FW) d++;
                var name = raw.slice(d).trim();
                if (o.value === '') {
                    if (!emptyItem) emptyItem = { i: i, value: '', name: name, depth: 0, isEmpty: true };
                    return;
                }
                while (stack.length && stack[stack.length - 1].depth >= d) stack.pop();
                var parent = stack.length ? stack[stack.length - 1] : null;
                var it = { i: i, value: o.value, name: name, depth: d, parent: parent, hasChildren: false, isEmpty: false,
                           disabled: o.disabled, path: (parent ? parent.path + ' / ' : '') + name };
                if (parent) parent.hasChildren = true;
                items.push(it);
                stack.push(it);
            });
        }
        readOptions();

        // 外壳：[隐藏的 select] + [按钮]
        var wrap = document.createElement('div');
        wrap.className = 'dept-picker';
        var style = select.getAttribute('style');
        if (style) wrap.setAttribute('style', style);
        select.parentNode.insertBefore(wrap, select);
        wrap.appendChild(select);
        select.classList.add('dept-native');
        select.tabIndex = -1;
        select.removeAttribute('style');

        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'form-select dept-picker-btn' + (select.classList.contains('form-select-sm') ? ' form-select-sm' : '');
        btn.setAttribute('role', 'combobox');
        btn.setAttribute('aria-haspopup', 'listbox');
        btn.setAttribute('aria-expanded', 'false');
        if (select.disabled) btn.disabled = true;
        wrap.appendChild(btn);

        function selectedItem() {
            var v = select.value;
            if (v === '') return emptyItem;
            for (var k = 0; k < items.length; k++) if (items[k].value === v) return items[k];
            return null;
        }

        function refreshButton() {
            var it = selectedItem();
            var placeholder = select.dataset.deptPickerPlaceholder || (emptyItem ? emptyItem.name : '请选择部门');
            if (it && !it.isEmpty) {
                btn.innerHTML = '<span class="dept-picker-label">' + esc(it.path) + '</span>';
                btn.title = it.path;
                btn.classList.remove('is-empty');
            } else {
                btn.innerHTML = '<span class="dept-picker-label">' + esc(it ? it.name : placeholder) + '</span>';
                btn.title = '';
                btn.classList.add('is-empty');
            }
            btn.disabled = select.disabled;
        }
        refreshButton();

        // 脚本里直接写 select.value = x（不会触发事件）时，按钮也要跟着变
        var desc = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value');
        Object.defineProperty(select, 'value', {
            configurable: true,
            get: function () { return desc.get.call(this); },
            set: function (v) { desc.set.call(this, v); refreshButton(); }
        });
        select.addEventListener('change', refreshButton);
        if (select.form) select.form.addEventListener('reset', function () { setTimeout(refreshButton, 0); });
        // 选项被脚本重新生成时（比如按分公司过滤），重新读取
        new MutationObserver(function () { readOptions(); refreshButton(); }).observe(select, { childList: true });

        /* ── 面板 ── */
        var panel = null, searchInput = null, listEl = null, countEl = null;
        var expanded = new Set();
        var visibleRows = [];       // 当前可选中的行（DOM）
        var activeIdx = -1;

        function buildPanel() {
            panel = document.createElement('div');
            panel.className = 'dept-picker-panel';
            panel.innerHTML =
                '<div class="dept-picker-search"><i class="bi bi-search"></i>' +
                '<input type="text" class="form-control form-control-sm" placeholder="搜索部门名称…" autocomplete="off" aria-label="搜索部门">' +
                '<button type="button" class="dept-picker-clear" title="清除搜索" hidden>&times;</button></div>' +
                '<div class="dept-picker-list" role="listbox"></div>' +
                '<div class="dept-picker-foot"><span class="dept-picker-count"></span>' +
                '<span class="dept-picker-keys">↑↓ 选择　Enter 确定　Esc 关闭</span></div>';
            document.body.appendChild(panel);
            searchInput = panel.querySelector('input');
            listEl = panel.querySelector('.dept-picker-list');
            countEl = panel.querySelector('.dept-picker-count');
            var clearBtn = panel.querySelector('.dept-picker-clear');

            searchInput.addEventListener('input', function () {
                clearBtn.hidden = !searchInput.value;
                render();
            });
            clearBtn.addEventListener('click', function () { searchInput.value = ''; clearBtn.hidden = true; render(); searchInput.focus(); });
            searchInput.addEventListener('keydown', onKey);
            listEl.addEventListener('click', onListClick);
            listEl.addEventListener('mousemove', function (e) {
                var row = e.target.closest('.dept-row');
                if (row && !row.classList.contains('active')) setActive(visibleRows.indexOf(row), false);
            });
        }

        function initExpanded() {
            expanded = new Set();
            items.forEach(function (it) { if (it.depth < 1) expanded.add(it.value); });   // 默认展开顶层
            var sel = selectedItem();
            for (var p = sel && !sel.isEmpty ? sel.parent : null; p; p = p.parent) expanded.add(p.value);
        }

        function rowHtml(it, q, flat) {
            var isSel = select.value === it.value;
            var pad = flat ? 0 : it.depth * 18;
            var caret = '';
            if (!flat) {
                caret = it.hasChildren
                    ? '<span class="dept-caret" data-toggle="' + esc(it.value) + '" title="展开/收起"><i class="bi bi-chevron-' + (expanded.has(it.value) ? 'down' : 'right') + '"></i></span>'
                    : '<span class="dept-caret-gap"></span>';
            }
            var label = flat
                ? '<span class="dept-name">' + highlight(it.name, q) + '</span>' + (it.parent ? '<span class="dept-path">' + highlight(it.path, q) + '</span>' : '')
                : '<span class="dept-name">' + esc(it.name) + '</span>';
            return '<div class="dept-row' + (isSel ? ' selected' : '') + (it.disabled ? ' disabled' : '') + '" role="option" aria-selected="' + isSel +
                '" data-value="' + esc(it.value) + '" style="padding-left:' + (8 + pad) + 'px">' + caret + label +
                (isSel ? '<i class="bi bi-check2 dept-tick"></i>' : '') + '</div>';
        }

        function render() {
            var q = (searchInput.value || '').trim();
            var html = '';
            if (!q) {
                if (emptyItem) {
                    var sel0 = select.value === '';
                    html += '<div class="dept-row dept-row-empty' + (sel0 ? ' selected' : '') + '" role="option" data-value="" style="padding-left:8px">' +
                        '<span class="dept-caret-gap"></span><span class="dept-name">' + esc(emptyItem.name) + '</span>' + (sel0 ? '<i class="bi bi-check2 dept-tick"></i>' : '') + '</div>';
                }
                // 树：祖先都展开的才显示
                items.forEach(function (it) {
                    for (var p = it.parent; p; p = p.parent) if (!expanded.has(p.value)) return;
                    html += rowHtml(it, '', false);
                });
                countEl.textContent = '共 ' + items.length + ' 个部门';
            } else {
                var n = 0, ql = q.toLowerCase();
                items.forEach(function (it) {
                    if (it.path.toLowerCase().indexOf(ql) >= 0) { html += rowHtml(it, q, true); n++; }
                });
                if (!n) html = '<div class="dept-none">没有找到包含"' + esc(q) + '"的部门</div>';
                countEl.textContent = '找到 ' + n + ' 个';
            }
            listEl.innerHTML = html;
            visibleRows = Array.prototype.slice.call(listEl.querySelectorAll('.dept-row:not(.disabled)'));
            var cur = visibleRows.findIndex(function (r) { return r.classList.contains('selected'); });
            setActive(q ? 0 : (cur >= 0 ? cur : 0), true);
        }

        function setActive(idx, scroll) {
            visibleRows.forEach(function (r) { r.classList.remove('active'); });
            activeIdx = idx;
            var r = visibleRows[idx];
            if (r) { r.classList.add('active'); if (scroll) r.scrollIntoView({ block: 'nearest' }); }
        }

        function pick(value) {
            if (select.value !== value) {
                desc.set.call(select, value);
                select.dispatchEvent(new Event('input', { bubbles: true }));
                select.dispatchEvent(new Event('change', { bubbles: true }));
            }
            refreshButton();
            close(true);
        }

        function onListClick(e) {
            var tog = e.target.closest('[data-toggle]');
            if (tog) {
                var v = tog.getAttribute('data-toggle');
                if (expanded.has(v)) expanded.delete(v); else expanded.add(v);
                var top = listEl.scrollTop;
                render(); listEl.scrollTop = top;
                return;
            }
            var row = e.target.closest('.dept-row');
            if (row && !row.classList.contains('disabled')) pick(row.getAttribute('data-value'));
        }

        function onKey(e) {
            if (e.key === 'ArrowDown') { e.preventDefault(); setActive(Math.min(activeIdx + 1, visibleRows.length - 1), true); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(Math.max(activeIdx - 1, 0), true); }
            else if (e.key === 'Home') { e.preventDefault(); setActive(0, true); }
            else if (e.key === 'End') { e.preventDefault(); setActive(visibleRows.length - 1, true); }
            else if (e.key === 'Enter') { e.preventDefault(); if (visibleRows[activeIdx]) pick(visibleRows[activeIdx].getAttribute('data-value')); }
            else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(true); }
            else if (e.key === 'Tab') { close(false); }
            else if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
                // 在树里用左右键展开/收起当前行
                var row = visibleRows[activeIdx];
                var t = row && row.querySelector('[data-toggle]');
                if (t && !searchInput.value) {
                    var v = t.getAttribute('data-toggle');
                    var want = e.key === 'ArrowRight';
                    if (expanded.has(v) !== want) { e.preventDefault(); if (want) expanded.add(v); else expanded.delete(v); var keep = row.getAttribute('data-value'); render(); var ni = visibleRows.findIndex(function (r) { return r.getAttribute('data-value') === keep; }); setActive(ni, true); }
                }
            }
        }

        function position() {
            if (!panel) return;
            var r = btn.getBoundingClientRect();
            var vw = document.documentElement.clientWidth, vh = document.documentElement.clientHeight;
            var width = Math.max(r.width, 300);
            if (vw < 480) width = vw - 16;
            var left = Math.min(Math.max(8, r.left), vw - width - 8);
            var spaceBelow = vh - r.bottom - 8, spaceAbove = r.top - 8;
            var maxH = 380;
            var below = spaceBelow >= 220 || spaceBelow >= spaceAbove;
            panel.style.width = width + 'px';
            panel.style.left = left + 'px';
            var h = Math.min(maxH, below ? spaceBelow : spaceAbove);
            panel.style.maxHeight = h + 'px';
            if (below) { panel.style.top = (r.bottom + 4) + 'px'; panel.style.bottom = ''; }
            else { panel.style.bottom = (vh - r.top + 4) + 'px'; panel.style.top = ''; }
        }

        function open() {
            if (select.disabled || openPicker === api) return;
            if (openPicker) openPicker.close(false);
            if (!panel) buildPanel();
            readOptions();
            initExpanded();
            searchInput.value = '';
            panel.querySelector('.dept-picker-clear').hidden = true;
            panel.classList.add('open');
            btn.setAttribute('aria-expanded', 'true');
            render();
            position();
            openPicker = api;
            searchInput.focus();
            var sel = panel.querySelector('.dept-row.selected');
            if (sel) sel.scrollIntoView({ block: 'center' });
        }

        function close(focusBack) {
            if (!panel || !panel.classList.contains('open')) return;
            panel.classList.remove('open');
            btn.setAttribute('aria-expanded', 'false');
            if (openPicker === api) openPicker = null;
            if (focusBack) btn.focus();
        }

        var api = { close: close, contains: function (n) { return (panel && panel.contains(n)) || wrap.contains(n); }, position: position };
        btn.addEventListener('click', function () { if (panel && panel.classList.contains('open')) close(true); else open(); });
        btn.addEventListener('keydown', function (e) {
            if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); open(); }
        });
    }

    document.addEventListener('mousedown', function (e) {
        if (openPicker && !openPicker.contains(e.target)) openPicker.close(false);
    });
    window.addEventListener('resize', function () { if (openPicker) openPicker.position(); });
    window.addEventListener('scroll', function (e) {
        if (openPicker && !(e.target && e.target.closest && e.target.closest('.dept-picker-panel'))) openPicker.position();
    }, true);

    /* ───────────────────────── 多选树 ───────────────────────── */

    function enhanceTree(box) {
        if (box.dataset.deptTreeReady) return;
        box.dataset.deptTreeReady = '1';
        var rows = Array.prototype.slice.call(box.querySelectorAll('.form-check'));
        if (!rows.length) return;

        // 每行的信息
        var info = new Map();
        rows.forEach(function (row) {
            var cb = row.querySelector('input.dept-checkbox');
            if (!cb) return;
            var pl = parseFloat(row.style.paddingLeft) || 1.5;
            var depth = Math.max(0, Math.round((pl - 1.5) / 1.2));
            info.set(cb.dataset.id, { row: row, cb: cb, depth: depth, parentId: cb.dataset.parent || '', children: [], text: row.textContent.replace(/\s+/g, ' ').trim() });
        });
        info.forEach(function (it) { var p = info.get(it.parentId); if (p) p.children.push(it); });

        // 给每行留出折叠按钮的位置
        info.forEach(function (it, id) {
            it.row.style.paddingLeft = (2.5 + it.depth * 1.2) + 'rem';
            it.row.classList.add('dept-tree-row');
            var caret = document.createElement('span');
            caret.className = 'dept-tree-caret' + (it.children.length ? '' : ' leaf');
            caret.style.left = (it.depth * 1.2) + 'rem';
            if (it.children.length) {
                caret.innerHTML = '<i class="bi bi-chevron-down"></i>';
                caret.title = '展开/收起';
                caret.addEventListener('click', function () { toggle(id); });
            }
            it.row.insertBefore(caret, it.row.firstChild);
            it.caret = caret;
            it.collapsed = false;
        });

        // 工具条
        var bar = document.createElement('div');
        bar.className = 'dept-tree-bar';
        bar.innerHTML =
            '<div class="input-group input-group-sm dept-tree-search"><span class="input-group-text"><i class="bi bi-search"></i></span>' +
            '<input type="text" class="form-control" placeholder="搜索部门…" autocomplete="off" aria-label="搜索部门"></div>' +
            '<div class="btn-group btn-group-sm" role="group">' +
            '<button type="button" class="btn btn-outline-secondary" data-act="expand">展开全部</button>' +
            '<button type="button" class="btn btn-outline-secondary" data-act="collapse">收起全部</button></div>' +
            '<div class="btn-group btn-group-sm" role="group">' +
            '<button type="button" class="btn btn-outline-secondary" data-act="all">全选</button>' +
            '<button type="button" class="btn btn-outline-secondary" data-act="none">清空</button></div>' +
            '<span class="dept-tree-count text-muted small"></span>';
        box.parentNode.insertBefore(bar, box);
        var input = bar.querySelector('input'), countEl = bar.querySelector('.dept-tree-count');

        function setCollapsed(id, v) {
            var it = info.get(id); if (!it || !it.children.length) return;
            it.collapsed = v;
            it.caret.firstChild.className = 'bi bi-chevron-' + (v ? 'right' : 'down');
        }
        function toggle(id) { setCollapsed(id, !info.get(id).collapsed); applyVisibility(); }

        function applyVisibility() {
            var q = input.value.trim().toLowerCase();
            var match = new Set();
            if (q) {
                info.forEach(function (it, id) {
                    if (it.text.toLowerCase().indexOf(q) >= 0) {
                        match.add(id);
                        for (var p = info.get(it.parentId); p; p = info.get(p.parentId)) match.add(p.cb.dataset.id);   // 命中项的所有上级也要显示
                    }
                });
            }
            info.forEach(function (it, id) {
                var show = true;
                if (q) show = match.has(id);
                else for (var p = info.get(it.parentId); p; p = info.get(p.parentId)) if (p.collapsed) { show = false; break; }
                it.row.style.display = show ? '' : 'none';
            });
        }

        function updateState() {
            var checked = 0;
            info.forEach(function (it) { if (it.cb.checked) checked++; });
            // 半选：自己没勾，但下面有人被勾
            info.forEach(function (it) {
                var any = false;
                (function walk(n) { n.children.forEach(function (c) { if (c.cb.checked) any = true; walk(c); }); })(it);
                it.cb.indeterminate = !it.cb.checked && any;
            });
            countEl.textContent = checked ? '已选 ' + checked + ' 个' : '未选择（= 不限制）';
        }

        input.addEventListener('input', applyVisibility);
        box.addEventListener('change', updateState);
        bar.addEventListener('click', function (e) {
            var b = e.target.closest('[data-act]'); if (!b) return;
            var act = b.getAttribute('data-act');
            if (act === 'expand' || act === 'collapse') {
                info.forEach(function (it, id) { setCollapsed(id, act === 'collapse'); });
                if (act === 'collapse') info.forEach(function (it, id) { if (it.depth === 0) setCollapsed(id, false); });   // 收起时保留顶层，不然只剩一行
                input.value = ''; applyVisibility();
            } else {
                info.forEach(function (it) {
                    if (it.row.style.display === 'none') return;     // 搜索时只处理看得见的
                    var want = act === 'all';
                    if (it.cb.checked !== want) { it.cb.checked = want; it.cb.dispatchEvent(new Event('change', { bubbles: true })); }
                });
                updateState();
            }
        });

        // 默认：除了顶层，其余折叠到只显示前两层；已勾选的部门所在路径保持展开
        info.forEach(function (it, id) { if (it.children.length && it.depth >= 1) setCollapsed(id, true); });
        info.forEach(function (it) {
            if (it.cb.checked) for (var p = info.get(it.parentId); p; p = info.get(p.parentId)) setCollapsed(p.cb.dataset.id, false);
        });
        applyVisibility();
        updateState();
        // 弹窗里"回显已保存的勾选"是脚本直接改 checked 的，不触发 change——每次点开弹窗时重新计算一下
        box.closest('.modal') && box.closest('.modal').addEventListener('shown.bs.modal', function () {
            info.forEach(function (it) {
                if (it.cb.checked) for (var p = info.get(it.parentId); p; p = info.get(p.parentId)) setCollapsed(p.cb.dataset.id, false);
            });
            applyVisibility(); updateState();
        });
        box.dataset.deptTreeRefresh = '1';
        box.deptTreeRefresh = function () { applyVisibility(); updateState(); };
    }

    function init() {
        document.querySelectorAll('select[data-dept-picker]').forEach(enhanceSelect);
        document.querySelectorAll('[data-dept-tree]').forEach(enhanceTree);
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();
    window.DeptPicker = { init: init };
})();
