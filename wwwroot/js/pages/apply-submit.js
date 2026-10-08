// 每种申请类型对应哪几个"必填"的日期/时间字段（id）
const REQUIRED_FIELDS_BY_TYPE = {
    Leave:              ['fLeaveStart', 'fLeaveEnd'],
    PunchReplenishment: ['fPunchDate'],
    Overtime:           ['fOvertimeStart', 'fOvertimeEnd'],
    BusinessTrip:       ['fTripStart', 'fTripEnd']
};

// 切换"申请类型"时：① 只显示当前类型对应的那组字段，其余隐藏；
// ② 同时把"必填"也跟着切换——只有当前选中类型的字段才是必填，其它类型的字段虽然被隐藏了，
//    但它们在网页源码里本来就写了 required，如果不跟着去掉，切到别的类型后点提交，
//    浏览器会去检查那些"看不见"的字段有没有填，结果明明该填的都填了，却提示"还有必填项没填"。
function syncApplyTypeFields(type) {
    document.getElementById('leaveFields').classList.toggle('d-none', type !== 'Leave');
    document.getElementById('punchFields').classList.toggle('d-none', type !== 'PunchReplenishment');
    document.getElementById('overtimeFields').classList.toggle('d-none', type !== 'Overtime');
    document.getElementById('businessTripFields').classList.toggle('d-none', type !== 'BusinessTrip');

    for (const [t, ids] of Object.entries(REQUIRED_FIELDS_BY_TYPE)) {
        ids.forEach(id => {
            const el = document.getElementById(id);
            if (el) el.required = (t === type);
        });
    }
}

const applyTypeSelect = document.getElementById('applyTypeSelect');
syncApplyTypeFields(applyTypeSelect.value);   // 页面刚打开时，也要按当前选中的类型先同步一次
applyTypeSelect.addEventListener('change', function() { syncApplyTypeFields(this.value); });

// ── 日期/时间选择限制：避免选出不合逻辑的时间（结束早于开始、请假选到过去、补卡选到未来）──
// 说明：这里做的是“前端友好提示 + 限制选择器可选范围”，真正兜底的校验在后端（服务器不会信任前端）。
function pad(n) { return String(n).padStart(2, '0'); }
function toLocalInputValue(d) {   // Date -> "yyyy-MM-ddTHH:mm"，datetime-local 输入框要的格式
    return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
function toDateInputValue(d) {    // Date -> "yyyy-MM-dd"，date 输入框要的格式
    return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`;
}

const now       = new Date();
const nowStr    = toLocalInputValue(now);
const todayStr  = toDateInputValue(now);
const leaveMinDate = new Date(now.getTime() - 24 * 60 * 60 * 1000);
const leaveMinStr  = toLocalInputValue(leaveMinDate);   // 现在往前推24小时
const todayStartStr = todayStr + 'T00:00';   // 今天0点
const todayEndStr   = todayStr + 'T23:59';   // 今天23:59（当天24点前）
const tomorrow      = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);
const tomorrowEndStr = toDateInputValue(tomorrow) + 'T23:59';   // 明天23:59：加班结束时间最晚只能到这（允许跨半夜，但不能随便选到更远的日期）
const maxAdvanceDate = new Date(now.getFullYear(), now.getMonth() + 3, now.getDate());
const maxAdvanceStr  = toLocalInputValue(maxAdvanceDate);   // 请假/出差开始时间最多能提前3个月，避免选到离谱的未来日期（2026-09-30）

const fLeaveStart = document.getElementById('fLeaveStart');
const fLeaveEnd    = document.getElementById('fLeaveEnd');
const fPunchDate   = document.getElementById('fPunchDate');
const fOvertimeStart = document.getElementById('fOvertimeStart');
const fOvertimeEnd   = document.getElementById('fOvertimeEnd');
const fTripStart = document.getElementById('fTripStart');
const fTripEnd   = document.getElementById('fTripEnd');

// 请假：开始时间最早只能选到“现在往前推24小时”，最晚只能提前3个月；结束时间不能早于开始时间
fLeaveStart.min = leaveMinStr;
fLeaveStart.max = maxAdvanceStr;
fLeaveStart.addEventListener('change', function () {
    fLeaveEnd.min = this.value;
    if (fLeaveEnd.value && fLeaveEnd.value <= this.value) fLeaveEnd.value = '';
});

// 补卡：补的是过去漏打的卡，日期不能选未来
fPunchDate.max = todayStr;

// 加班：必须是当天的加班，当天24点前提交当日申请；结束时间不能早于开始时间，
// 最晚只能选到明天23:59（允许夜班/晚上加班跨过午夜，但不能一次选出好几天，一次申请只对应一天的加班）
fOvertimeStart.min = todayStartStr;
fOvertimeStart.max = todayEndStr;
fOvertimeEnd.max    = tomorrowEndStr;
fOvertimeStart.addEventListener('change', function () {
    fOvertimeEnd.min = this.value;
    if (fOvertimeEnd.value && fOvertimeEnd.value <= this.value) fOvertimeEnd.value = '';
});

// 出差：开始时间最早只能选到今天0点（方便补提今天已经开始的出差），最晚只能提前3个月；结束时间不能早于开始时间
fTripStart.min = todayStartStr;
fTripStart.max = maxAdvanceStr;
fTripStart.addEventListener('change', function () {
    fTripEnd.min = this.value;
    if (fTripEnd.value && fTripEnd.value <= this.value) fTripEnd.value = '';
});

// 提交前再兜底提示一次（和后端校验规则保持一致，避免用户手动输入绕过选择器限制）
document.querySelector('form[enctype]').addEventListener('submit', function (e) {
    const type = document.getElementById('applyTypeSelect').value;
    let msg = null;
    if (type === 'Leave') {
        if (fLeaveStart.value && fLeaveStart.value < leaveMinStr) msg = '请假开始时间最早只能选到现在往前推24小时以内';
        else if (fLeaveStart.value && fLeaveStart.value > maxAdvanceStr) msg = '请假开始时间最多只能提前3个月申请';
        else if (fLeaveStart.value && fLeaveEnd.value && fLeaveEnd.value <= fLeaveStart.value) msg = '请假结束时间必须晚于开始时间';
    } else if (type === 'PunchReplenishment') {
        if (fPunchDate.value && fPunchDate.value > todayStr) msg = '补卡日期不能晚于今天';
    } else if (type === 'Overtime') {
        if (fOvertimeStart.value && fOvertimeStart.value.slice(0, 10) !== todayStr) msg = '加班申请必须是当天的加班，请在当天24点前提交当日申请';
        else if (fOvertimeStart.value && fOvertimeEnd.value && fOvertimeEnd.value <= fOvertimeStart.value) msg = '加班结束时间必须晚于开始时间';
        else if (fOvertimeEnd.value && fOvertimeEnd.value > tomorrowEndStr) msg = '加班结束时间最晚只能选到明天24点前，一次申请只对应一天的加班（跨夜的加班如果确实超过这个范围，请分开提交）';
    } else if (type === 'BusinessTrip') {
        if (fTripStart.value && fTripStart.value < todayStartStr) msg = '出差开始时间不能早于今天0点';
        else if (fTripStart.value && fTripStart.value > maxAdvanceStr) msg = '出差开始时间最多只能提前3个月申请';
        else if (fTripStart.value && fTripEnd.value && fTripEnd.value <= fTripStart.value) msg = '出差结束时间必须晚于开始时间';
    }
    if (msg) { e.preventDefault(); alert(msg); return; }

    // 提交按钮点一下就立刻锁住、变成"提交中..."：防止手机上网络慢的时候用户以为没反应又连点几下，
    // 同一份申请被发了两三份一模一样的（发现于 2026-09-18 发工资前的数据核查：不少加班申请几百毫秒
    // 内被连续提交了 2-4 次，后端的重复检查在这么短的间隔里也可能来不及生效，源头上锁住按钮更可靠）。
    const submitBtn = this.querySelector('button[type="submit"]');
    if (submitBtn) {
        submitBtn.disabled = true;
        submitBtn.innerHTML = '<i class="bi bi-hourglass-split me-2"></i>提交中...';
    }
});

// 附件支持逐个添加、单独删除：用 DataTransfer 维护已选文件列表
const attachInput = document.getElementById('attachInput');
const staged = new DataTransfer();
const MAX_FILES = 5;
const MAX_SIZE  = 10 * 1024 * 1024;

attachInput.addEventListener('change', function () {
    for (const f of this.files) {
        if (staged.items.length >= MAX_FILES) { alert('最多只能上传 ' + MAX_FILES + ' 个附件'); break; }
        if (f.size > MAX_SIZE) { alert(`文件「${f.name}」超过 10MB，已跳过`); continue; }
        if ([...staged.files].some(x => x.name === f.name && x.size === f.size)) continue; // 去重
        staged.items.add(f);
    }
    attachInput.files = staged.files;   // 同步回输入框，提交时一并上传
    renderFileList();
});

function removeFile(idx) {
    const dt = new DataTransfer();
    [...staged.files].forEach((f, i) => { if (i !== idx) dt.items.add(f); });
    staged.items.clear();
    [...dt.files].forEach(f => staged.items.add(f));
    attachInput.files = staged.files;
    renderFileList();
}

function renderFileList() {
    const list = document.getElementById('fileList');
    list.innerHTML = [...staged.files].map((f, i) =>
        `<span class="badge bg-light text-dark border d-inline-flex align-items-center">
            <i class="bi bi-paperclip me-1"></i>${escapeHtml(f.name)} (${(f.size/1024).toFixed(0)}KB)
            <button type="button" class="btn-close btn-close-sm ms-2" style="font-size:.6rem"
                    aria-label="删除" onclick="removeFile(${i})"></button>
         </span>`
    ).join('');
}

// escapeHtml 已在 _Layout.cshtml 里全局定义一份，这里不再重复声明（2026-09-21 去重）。
