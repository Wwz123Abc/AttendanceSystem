(function () {
    if (window.__agentWidgetLoaded) return;
    window.__agentWidgetLoaded = true;

    var convId = 0;
    var busy = false;
    var briefShown = false;
    var fab = document.getElementById('awFab');
    var panel = document.getElementById('awPanel');
    var body = document.getElementById('awBody');
    var actionsBox = document.getElementById('awActions');
    var chips = document.getElementById('awChips');
    var text = document.getElementById('awText');
    var hint = document.getElementById('awHint');

    // 轻量 Markdown：转义后把 **粗体** 渲染出来，避免星号原样显示
    function renderMd(t) {
        var s = String(t).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
        s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
        s = s.replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<em>$2</em>');
        return s;
    }

    function token() {
        // 从服务端取当前会话有效的防伪令牌（用请求头方式携带，更可靠）
        return fetch('/Agent/Chat?handler=Token', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.json(); })
            .then(function (d) { return d.token || ''; })
            .catch(function () { return ''; });
    }
    function scrollBottom() { body.scrollTop = body.scrollHeight; }
    function bubble(role, content) {
        var row = document.createElement('div');
        row.className = 'aw-msg ' + role;
        var b = document.createElement('div');
        b.className = 'b';
        b.textContent = content;
        row.appendChild(b);
        body.appendChild(row);
        scrollBottom();
        return b;
    }

    // 渲染待确认/可撤回的动作卡片列表（直接在悬浮窗内点确认/拒绝/撤回，不用跳完整页）
    function renderActions(list) {
        actionsBox.innerHTML = '';
        (list || []).forEach(function (a) {
            if (!a.isPending && !a.canUndo) return;   // 已拒绝/已过期/不可撤回的执行记录不占地方
            var card = document.createElement('div');
            card.className = 'aw-action-card' + (a.highRisk ? ' risk' : '');
            var top = document.createElement('div');
            top.className = 'aw-action-top';
            top.innerHTML = '<span class="aw-action-tool"></span><span class="aw-action-status"></span>';
            top.querySelector('.aw-action-tool').textContent = a.toolNameText || '操作';
            top.querySelector('.aw-action-status').textContent = a.statusText || '';
            card.appendChild(top);
            var sum = document.createElement('div');
            sum.className = 'aw-action-summary';
            sum.textContent = a.summaryText || '';
            card.appendChild(sum);
            if (a.canUndo && a.undoHint) {
                var note = document.createElement('div');
                note.className = 'aw-action-note';
                note.textContent = a.undoHint;
                card.appendChild(note);
            }
            var btns = document.createElement('div');
            btns.className = 'aw-action-btns';
            if (a.isPending) {
                btns.innerHTML = '<button type="button" class="approve">确认执行</button><button type="button" class="reject">拒绝</button>';
                btns.querySelector('.approve').addEventListener('click', function () { reviewAction(a.id, 'approve', this); });
                btns.querySelector('.reject').addEventListener('click', function () { reviewAction(a.id, 'reject', this); });
            } else if (a.canUndo) {
                btns.innerHTML = '<button type="button" class="undo">撤回</button>';
                btns.querySelector('.undo').addEventListener('click', function () { reviewAction(a.id, 'undo', this); });
            }
            card.appendChild(btns);
            actionsBox.appendChild(card);
        });
    }

    function refreshActions() {
        if (!convId) return;
        fetch('/Agent/Chat?handler=WidgetActions&conversationId=' + convId, { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.json(); })
            .then(function (d) { renderActions(d.actions); })
            .catch(function () { /* 静默：不影响主对话 */ });
    }

    function reviewAction(actionId, act, btnEl) {
        var card = btnEl.closest('.aw-action-card');
        card.querySelectorAll('button').forEach(function (b) { b.disabled = true; });
        token().then(function (tk) {
            return fetch('/Agent/Chat?handler=WidgetReview', {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'RequestVerificationToken': tk },
                body: 'actionId=' + encodeURIComponent(actionId) + '&conversationId=' + encodeURIComponent(convId) + '&act=' + encodeURIComponent(act)
            });
        }).then(function (r) { return r.json(); }).then(function (d) {
            hint.textContent = d.message || (d.ok ? '操作成功' : '操作失败');
            renderActions(d.actions);
        }).catch(function (e) {
            hint.textContent = '⚠️ ' + e.message;
            card.querySelectorAll('button').forEach(function (b) { b.disabled = false; });
        });
    }

    function openPanel() {
        panel.classList.add('open');
        if (convId === 0) loadData();
        text.focus();
    }
    function closePanel() { panel.classList.remove('open'); }
    function setBusy(on) {
        busy = on;
        fab.classList.toggle('busy', on);
        document.getElementById('awSend').disabled = on;
        text.disabled = on;
    }

    // 主动播报：今日异常/待审批/待确认登记速览，每次页面打开只播一次，不打扰
    function showBriefOnce() {
        if (briefShown) return;
        briefShown = true;
        fetch('/Agent/Chat?handler=WidgetBrief', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.json(); })
            .then(function (d) { if (d.text) bubble('assistant', d.text); })
            .catch(function () { /* 静默：播报失败不影响正常对话 */ });
    }

    function loadData() {
        fetch('/Agent/Chat?handler=WidgetData', { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.json(); })
            .then(function (d) {
                body.innerHTML = '';
                if (!d.enabled) {
                    bubble('assistant', '智能助手未启用（Agent:Enabled=true + AGENT_API_KEY 环境变量）。');
                    return;
                }
                if (!d.conversationId) {
                    // 无会话：自动建一个
                    return token().then(function (tk) {
                        return fetch('/Agent/Chat?handler=WidgetEnsure', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'RequestVerificationToken': tk }
                        }).then(function (r2) { return r2.json(); }).then(function (e) {
                            convId = e.conversationId;
                            showBriefOnce();
                            bubble('assistant', '你好！我是考勤系统智能助手。有什么可以帮你？（要数据查询、生成操作提案都可以，生成的动作可以直接在下面确认）');
                        });
                    });
                }
                convId = d.conversationId;
                var msgs = d.messages || [];
                showBriefOnce();
                if (msgs.length === 0) {
                    bubble('assistant', '你好！我是考勤系统智能助手。有什么可以帮你？（要数据查询、生成操作提案都可以，生成的动作可以直接在下面确认）');
                } else {
                    msgs.forEach(function (m) {
                        if (m.role === 'user') bubble('user', m.content);
                        else if (m.role === 'assistant') {
                            var b = bubble('assistant', m.content);
                            b.innerHTML = renderMd(m.content);
                        }
                    });
                }
                renderActions(d.actions);
            }).catch(function (e) {
                bubble('assistant', '加载失败：' + e.message);
            });
    }

    function send() {
        if (busy || !convId) return;
        var t = text.value.trim();
        if (!t) return;
        text.value = '';
        bubble('user', t);
        var sb = bubble('assistant', '');
        sb.innerHTML = '<span class="aw-typing"><span></span><span></span><span></span></span>';
        var acc = '';
        setBusy(true);
        hint.textContent = '正在生成…（写操作生成动作后请到「完整页」确认执行）';

        token().then(function (tk) {
            return fetch('/Agent/Chat?handler=Stream', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded',
                    'RequestVerificationToken': tk
                },
                body: 'SendConversationId=' + encodeURIComponent(convId) +
                      '&SendText=' + encodeURIComponent(t)
            }).then(function (res) {
                if (!res.ok || !res.body) throw new Error('HTTP ' + res.status);
                var reader = res.body.getReader();
                var decoder = new TextDecoder('utf-8');
                var buf = '';
                function pump() {
                    return reader.read().then(function (r) {
                        if (r.done) return;
                        buf += decoder.decode(r.value, { stream: true });
                        var idx;
                        while ((idx = buf.indexOf('\n\n')) >= 0) {
                            var block = buf.slice(0, idx);
                            buf = buf.slice(idx + 2);
                            var di = block.indexOf('data:');
                            if (di < 0) continue;
                            var data = block.slice(di + 5).trim();
                            if (!data) continue;
                            var ev;
                            try { ev = JSON.parse(data); } catch (e) { continue; }
                            if (ev.t === 'd') { acc += ev.x; sb.innerHTML = renderMd(acc); scrollBottom(); }
                            else if (ev.t === 'err') { sb.textContent = '⚠️ ' + ev.x; }
                            else if (ev.t === 'done' && ev.x && !acc) { sb.innerHTML = renderMd(ev.x); }
                        }
                        return pump();
                    });
                }
                return pump();
            });
        }).catch(function (e) {
            sb.textContent = '⚠️ ' + e.message;
        }).finally(function () {
            setBusy(false);
            hint.textContent = '生成的操作提案可以直接在下面点确认执行；手机号等敏感信息请勿粘贴。';
            refreshActions();
        });
    }

    fab.addEventListener('click', function () {
        if (panel.classList.contains('open')) closePanel();
        else openPanel();
    });
    document.getElementById('awClose').addEventListener('click', closePanel);
    document.getElementById('awSend').addEventListener('click', send);
    text.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); send(); } });
    chips.querySelectorAll('.aw-chip').forEach(function (chip) {
        chip.addEventListener('click', function () {
            if (busy || !convId) return;
            text.value = chip.getAttribute('data-q');
            send();
        });
    });
})();
