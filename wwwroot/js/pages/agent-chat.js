// 批量确认/拒绝：勾选框数量联动批量按钮的可用状态与提示文字
(function () {
    var checks = document.querySelectorAll('.ac-batch-check');
    var selectAll = document.getElementById('batchSelectAll');
    var form = document.getElementById('acBatchForm');
    if (!form || checks.length === 0) return;
    var hint = document.getElementById('batchCountHint');
    var btns = form.querySelectorAll('button[type=submit]');
    function refresh() {
        var n = Array.prototype.filter.call(checks, function (c) { return c.checked; }).length;
        hint.textContent = n > 0 ? '已勾选 ' + n + ' 条' : '未勾选';
        btns.forEach(function (b) { b.disabled = n === 0; });
        if (selectAll) selectAll.checked = n === checks.length;
    }
    checks.forEach(function (c) { c.addEventListener('change', refresh); });
    if (selectAll) {
        selectAll.addEventListener('change', function () {
            checks.forEach(function (c) { c.checked = selectAll.checked; });
            refresh();
        });
    }
    refresh();
})();

// 轻量 Markdown：先转义 HTML 再转 **粗体**（模型回答常用），避免把星号原样显示
function renderMd(t) {
    var s = String(t).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    s = s.replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<em>$2</em>');
    return s;
}
// 输入框随内容自动增高（最多到 CSS 里设的 max-height，之后出现滚动条）
function autoGrow(ta) {
    ta.style.height = 'auto';
    ta.style.height = ta.scrollHeight + 'px';
}
// 打开页面：把历史里的助手回答按 Markdown 渲染，滚动到底 + 聚焦输入框
(function () {
    var area = document.getElementById('msgArea');
    if (area) {
        area.querySelectorAll('.ac-md').forEach(function (b) {
            b.innerHTML = renderMd(b.textContent);
        });
        area.scrollTop = area.scrollHeight;
    }
    var ta = document.getElementById('sendText');
    if (ta) { ta.focus(); ta.addEventListener('input', function () { autoGrow(ta); }); }
})();

// 回车发送（Shift+Enter 换行）
document.addEventListener('keydown', function (e) {
    if (e.key === 'Enter' && !e.shiftKey && e.target && e.target.id === 'sendText') {
        e.preventDefault();
        agentSend();
    }
});

var streaming = false;

function agentSend() {
    if (streaming || conversationId <= 0) return;
    var ta = document.getElementById('sendText');
    var text = ta.value.trim();
    if (!text) return;

    var tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
    var token = tokenInput ? tokenInput.value : '';

    var btn = document.getElementById('sendBtn');
    var area = document.getElementById('msgArea');
    var hint = document.getElementById('sendHint');

    // 显示用户气泡
    var ub = document.createElement('div');
    ub.className = 'ac-msg-row user';
    ub.innerHTML = '<span class="ac-avatar usr"><i class="bi bi-person-fill"></i></span>' +
        '<div class="ac-bubble-col"><div class="ac-bubble"></div></div>';
    ub.querySelector('.ac-bubble').textContent = text;
    area.appendChild(ub);
    area.scrollTop = area.scrollHeight;

    // 流式气泡（先显示打字中的跳动小圆点）
    var sb = document.createElement('div');
    sb.className = 'ac-msg-row assistant';
    sb.innerHTML = '<span class="ac-avatar bot"><i class="bi bi-robot"></i></span>' +
        '<div class="ac-bubble-col"><div class="ac-bubble ac-md"><span class="ac-typing"><span></span><span></span><span></span></span></div></div>';
    var bubble = sb.querySelector('.ac-bubble');
    area.appendChild(sb);
    area.scrollTop = area.scrollHeight;

    ta.value = '';
    ta.style.height = 'auto';
    btn.disabled = true;
    btn.textContent = '生成中…';
    streaming = true;
    var acc = '';

    fetch('?handler=Stream', {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: 'SendConversationId=' + encodeURIComponent(conversationId) +
              '&SendText=' + encodeURIComponent(text) +
              '&__RequestVerificationToken=' + encodeURIComponent(token)
    }).then(function (res) {
        if (!res.ok || !res.body) throw new Error('请求失败 HTTP ' + res.status);
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
                    var dIdx = block.indexOf('data:');
                    if (dIdx < 0) continue;
                    var data = block.slice(dIdx + 5).trim();
                    if (!data) continue;
                    var ev;
                    try { ev = JSON.parse(data); } catch (e) { continue; }
                    if (ev.t === 'd') {
                        acc += ev.x;
                        bubble.innerHTML = renderMd(acc);
                        area.scrollTop = area.scrollHeight;
                    } else if (ev.t === 'err') {
                        bubble.textContent = '⚠️ ' + ev.x;
                    } else if (ev.t === 'done') {
                        if (ev.x && !acc) { bubble.innerHTML = renderMd(ev.x); }
                    }
                }
                return pump();
            });
        }
        return pump();
    }).catch(function (e) {
        bubble.textContent = '⚠️ ' + e.message;
    }).finally(function () {
        btn.disabled = false;
        btn.textContent = '发送';
        streaming = false;
        // 流结束：刷新页面落库（对话历史/动作卡片/会话列表）
        setTimeout(function () { location.reload(); }, 300);
    });
}
