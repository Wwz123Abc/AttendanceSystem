const dayModal = new bootstrap.Modal(document.getElementById('dayModal'));

function showDay(dateStr) {
    const d = CALENDAR_DETAILS[dateStr];
    if (!d) return;

    document.getElementById('dayModalTitle').textContent = dateStr + ' 考勤明细';

    let rows = `
        <dt class="col-4">考勤状态</dt><dd class="col-8">${d.statusText}</dd>
        <dt class="col-4">上班打卡</dt><dd class="col-8">${d.clockIn}</dd>
        <dt class="col-4">下班打卡</dt><dd class="col-8">${d.clockOut}</dd>
        <dt class="col-4">实际工时</dt><dd class="col-8">${d.hours === '--' ? '--' : d.hours + ' 小时'}</dd>`;

    if (d.late)     rows += `<dt class="col-4">迟到</dt><dd class="col-8 text-danger">${d.late} 分钟</dd>`;
    if (d.early)     rows += `<dt class="col-4">早退</dt><dd class="col-8 text-danger">${d.early} 分钟</dd>`;
    if (d.shiftName) rows += `
        <dt class="col-4">当天排班</dt><dd class="col-8">${escapeHtml(d.shiftName)}（${escapeHtml(d.shiftTime)}）</dd>`;
    if (d.note) rows += `<dt class="col-4">备注</dt><dd class="col-8 text-muted">${escapeHtml(d.note)}</dd>`;

    document.getElementById('dayModalBody').innerHTML = `<dl class="row mb-0">${rows}</dl>`;
    dayModal.show();
}

// escapeHtml 已在 _Layout.cshtml 里全局定义一份，这里不再重复声明（2026-09-21 去重）。
