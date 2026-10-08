const noticeDetailModal = document.getElementById('noticeDetailModal');
noticeDetailModal.addEventListener('show.bs.modal', function (e) {
    const id = e.relatedTarget.dataset.id;
    const d = NOTICE_DETAILS[id];
    if (!d) return;
    document.getElementById('noticeDetailTitle').textContent = d.title;
    document.getElementById('noticeDetailMeta').textContent = d.meta;
    document.getElementById('noticeDetailContent').textContent = d.content;

    // 打开详情顺手标记已读：去掉这张卡片的"未读"样式和角标，不用等刷新页面才消失
    const card = e.relatedTarget;
    card.classList.remove('border-primary');
    card.querySelector('.badge.bg-danger')?.remove();
    // 标记已读会改变服务端数据，改用 POST + 防伪令牌（跟页面上其它几处 GET 的 ?handler= 只读查询不一样，
    // 这个会写库，不能只靠 SameSite=Lax 兜底）。项目没有单独配置防伪令牌走请求头（HeaderName），
    // 用默认的表单字段方式提交，跟 Razor Pages 表单自动生成的 <form> 提交方式一致。
    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
    fetch(`?handler=MarkRead&id=${id}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: `__RequestVerificationToken=${encodeURIComponent(token || '')}`
    }).catch(() => {});
});
