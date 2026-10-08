// 记住当前选中的标签页（员工列表 / 待确认登记），配合上面 30 秒自动刷新一起用——
// 不然每次自动刷新都会跳回"员工列表"，正在"待确认登记"里核对的管理员会很烦
document.querySelectorAll('.nav-tabs button[data-bs-toggle="tab"]').forEach(btn => {
    btn.addEventListener('shown.bs.tab', () => sessionStorage.setItem('userManageTab', btn.dataset.bsTarget));
});
(function restoreTab() {
    const saved = sessionStorage.getItem('userManageTab');
    if (!saved) return;
    const trigger = document.querySelector(`.nav-tabs button[data-bs-target="${saved}"]`);
    if (trigger) new bootstrap.Tab(trigger).show();
})();

// 当前左侧选中的部门（新增员工时作为默认部门）

// ── 直属上级：搜索选择（候选人不多，直接把全量列表带下来，前端过滤，不用每敲一个字都请求后端）──

function setSuperId(id, fallbackLabel) {
    id = id || '';
    document.getElementById('fSuperId').value = id;
    const found = id ? SUPERVISORS.find(s => String(s.id) === String(id)) : null;
    document.getElementById('fSuperIdSearch').value = !id ? '' : (found ? found.label : (fallbackLabel || `（原上级已不在候选列表中，ID:${id}）`));
}

function renderSuperResults(list) {
    const box = document.getElementById('fSuperIdResults');
    box.innerHTML = '';
    if (list.length === 0) { box.style.display = 'none'; return; }
    // 姓名是员工资料里的自由文本，没有限制字符——用 textContent 而不是拼 HTML 字符串塞进 innerHTML，
    // 就算姓名里被人写了 <script> 之类的内容，也只会被当成纯文本显示出来，不会被当成标签解析执行
    list.forEach(s => {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'list-group-item list-group-item-action py-1 px-2 small';
        btn.dataset.id = s.id;
        btn.textContent = s.label;
        btn.addEventListener('click', () => { setSuperId(s.id); box.style.display = 'none'; });
        box.appendChild(btn);
    });
    box.style.display = 'block';
}

document.getElementById('fSuperIdSearch').addEventListener('input', function () {
    document.getElementById('fSuperId').value = '';   // 手动改动搜索框时先清空已选中的，等真正点选候选项后才重新赋值
    const kw = this.value.trim();
    if (!kw) { renderSuperResults([]); return; }
    renderSuperResults(SUPERVISORS.filter(s => s.label.includes(kw)).slice(0, 20));
});
document.addEventListener('click', function (e) {
    const box = document.getElementById('fSuperIdResults');
    if (!document.getElementById('fSuperIdSearch').contains(e.target) && !box.contains(e.target)) box.style.display = 'none';
});
// 提交前校验：必填时必须是从候选列表里点选出来的（不能只打字不选），否则 SuperId 会是空的
document.getElementById('userForm').addEventListener('submit', function (e) {
    const searchEl = document.getElementById('fSuperIdSearch');
    if (searchEl.required && !document.getElementById('fSuperId').value) {
        e.preventDefault();
        alert('请从下拉列表中点选一位直属上级（不能只输入文字不选择）');
        searchEl.focus();
    }
});

function toggleAll(cb) { document.querySelectorAll('.row-check').forEach(c => c.checked = cb.checked); }

// ── 工号自动生成：新增/确认录入时，选定部门后自动去后台按"公司"前缀+流水号生成工号 ──
// 只在"新增"和"确认录入"这两个场景生效（编辑老员工时工号已经定了，不应该跟着部门乱变）。
let autoFillEmployeeNo = false;

async function fillEmployeeNoForDept(deptId) {
    if (!autoFillEmployeeNo || !deptId) return;
    try {
        const resp = await fetch(`?handler=GenerateEmployeeNo&deptId=${deptId}`);
        const data = await resp.json();
        if (data.employeeNo) document.getElementById('fEmployeeNo').value = data.employeeNo;
    } catch (e) { /* 生成失败就静默忽略，不影响手动填写工号 */ }
}
document.getElementById('fDeptId').addEventListener('change', function () {
    fillEmployeeNoForDept(this.value);
    suggestSupervisorForDept(this.value);
    autoFillScopedDept();
});

// ── 管理范围默认跟着"部门"走：这两个字段本来就经常设成同一个值（详见 CanAccessDeptAsync/
// ValidateScopeForSaveAsync 的口径说明），分开填一步很容易漏掉后者，导致新账号意外变成
// "不受限的总部超级管理员"。只在角色是"管理员/文员"、且用户本次打开弹窗后还没手动改过
// "管理范围"下拉框时才自动带出；一旦手动改过（哪怕改回和部门一样的值），就不再覆盖，
// 尊重管理员的显式选择。
let scopedDeptTouched = false;
document.getElementById('fScopedDeptId')?.addEventListener('change', () => { scopedDeptTouched = true; });
function autoFillScopedDept() {
    const scopedEl = document.getElementById('fScopedDeptId');
    if (!scopedEl || scopedDeptTouched) return;
    const role = document.getElementById('fRole').value;
    if (role !== 'Admin' && role !== 'Clerk') return;
    scopedEl.value = document.getElementById('fDeptId').value;
}
document.getElementById('fRole').addEventListener('change', autoFillScopedDept);

// ── 直属上级自动带出：选定部门后，按"该部门下角色=主管的在职员工"自动预选直属上级 ──
// 唯一匹配才自动选中；0 个或多个匹配都不自动选，改成提示文案，交给管理员手动选择。
async function suggestSupervisorForDept(deptId) {
    const hintEl = document.getElementById('fSuperIdHint');
    if (!deptId) { hintEl.textContent = '选定部门后会自动带出该部门的主管，也可以手动改选。'; return; }
    try {
        const resp = await fetch(`?handler=SuggestSupervisor&deptId=${deptId}`);
        const data = await resp.json();
        if (data.supervisorId) {
            setSuperId(data.supervisorId);
            hintEl.textContent = '已按部门自动带出主管，也可以手动搜索改选。';
        } else if (data.count === 0) {
            setSuperId('');
            hintEl.textContent = '该部门暂无主管，请手动搜索选择直属上级。';
        } else {
            setSuperId('');
            hintEl.textContent = '该部门配置了多位主管，无法自动判断，请手动搜索选择直属上级。';
        }
    } catch (e) { /* 自动带出失败就静默忽略，不影响手动选择 */ }
}

// ── 左侧部门树：默认折叠，点箭头展开/收起（点名字仍是筛选，不受影响）────────
function deptToggle(ev, id, icon) {
    ev.preventDefault(); ev.stopPropagation();
    const expanded = icon.classList.contains('bi-chevron-down');
    icon.classList.toggle('bi-chevron-down', !expanded);
    icon.classList.toggle('bi-chevron-right', expanded);
    deptSetChildren(id, !expanded);
}
function deptSetChildren(parentId, visible) {
    document.querySelectorAll(`.dept-node[data-parent="${parentId}"]`).forEach(el => {
        el.style.display = visible ? '' : 'none';
        const cid = el.getAttribute('data-id');
        const ic  = el.querySelector('.tree-toggle');
        if (!visible) deptSetChildren(cid, false);
        else          deptSetChildren(cid, ic ? ic.classList.contains('bi-chevron-down') : true);
    });
}
// 打开页面时若正筛选某个深层部门，自动展开到它（否则它会被折叠隐藏，看不到选中态）
document.addEventListener('DOMContentLoaded', () => {
    if (SELECTED_DEPT === null) return;
    const cur = document.querySelector(`.dept-node[data-id="${SELECTED_DEPT}"]`);
    let pid = cur ? cur.getAttribute('data-parent') : '';
    while (pid) {
        const p = document.querySelector(`.dept-node[data-id="${pid}"]`);
        if (!p) break;
        const ic = p.querySelector('.tree-toggle');
        if (ic) { ic.classList.remove('bi-chevron-right'); ic.classList.add('bi-chevron-down'); }
        document.querySelectorAll(`.dept-node[data-parent="${pid}"]`).forEach(c => c.style.display = '');
        pid = p.getAttribute('data-parent');
    }
});

// 行内单个操作：通过统一隐藏表单提交（自动带上当前筛选上下文）
function doAction(handler, id, confirmMsg) {
    if (confirmMsg && !confirm(confirmMsg)) return;
    const f = document.getElementById('actionForm');
    f.action = '?handler=' + handler;
    document.getElementById('actId').value = id;
    document.getElementById('actBatchIds').value = '';
    f.submit();
}
// 批量操作：收集勾选的员工 id
function doBatch(handler, confirmMsg) {
    const ids = [...document.querySelectorAll('.row-check:checked')].map(c => c.value);
    if (ids.length === 0) { alert('请先勾选员工'); return; }
    if (confirmMsg && !confirm(confirmMsg)) return;
    const f = document.getElementById('actionForm');
    f.action = '?handler=' + handler;
    document.getElementById('actId').value = 0;
    document.getElementById('actBatchIds').value = ids.join(',');
    f.submit();
}

// 勾选/清空"推送到哪些考勤机"多选框
function setDeviceIds(ids) {
    const idSet = new Set((ids || []).map(String));
    document.querySelectorAll('.device-checkbox').forEach(cb => cb.checked = idSet.has(cb.value));
}
// 设置"管理范围"下拉框（不受限管理员才会渲染这个下拉框，普通管理员看不到，直接跳过）
function setScopedDeptId(id) {
    const el = document.getElementById('fScopedDeptId');
    if (el) el.value = id || '';
}

// 新增
function openAdd() {
    scopedDeptTouched = false;   // 重新打开弹窗，允许管理范围再次跟着部门/角色自动带默认值
    document.getElementById('userModalTitle').textContent = '新增员工';
    document.getElementById('userForm').action = '?handler=Create';
    document.getElementById('userSubmitBtn').textContent = '创建';
    document.getElementById('fEditUserId').value = 0;
    document.getElementById('fRegistrationId').value = '';   // 不是在确认扫码登记，清空关联
    document.getElementById('pwdRow').style.display = '';
    ['fEmployeeNo','fRealName','fPhone','fIdNumber','fContractCompany','fHireDate',
     'fHomeAddress','fEmergencyContactName','fEmergencyContactPhone']
        .forEach(id => document.getElementById(id).value = '');
    setPosition('');
    document.getElementById('fIdCardPhoto').value = '';
    document.getElementById('fIdCardPhotoPreviewWrap').style.display = 'none';   // 新建员工还没有照片，不显示预览
    document.getElementById('fAllowRemotePunch').checked = false;   // 新建员工默认不开远程打卡
    document.getElementById('fAttendanceExempt').checked = false;   // 新建员工默认需要打卡
    document.getElementById('fRole').value = 'Employee';
    document.getElementById('fDeptId').value  = SELECTED_DEPT !== null ? SELECTED_DEPT : '';  // 默认=左侧选中的部门
    document.getElementById('fGroupId').value = '';
    setSuperId('');
    setDeviceIds([]);
    setScopedDeptId(null);
    document.getElementById('fPhone').required = true;             // 新建必须填手机号
    document.getElementById('fPhoneReq').style.display = '';
    document.getElementById('fSuperIdSearch').required = true;     // 新建必须选直属上级
    document.getElementById('fSuperIdReq').style.display = '';
    document.getElementById('fDeptId').required = true;            // 新建必须选部门
    document.getElementById('fDeptIdReq').style.display = '';
    autoFillEmployeeNo = true;
    fillEmployeeNoForDept(document.getElementById('fDeptId').value);   // 默认部门已选中时，直接按它生成一次
    suggestSupervisorForDept(document.getElementById('fDeptId').value);
    new bootstrap.Modal(document.getElementById('userModal')).show();
}

// 复制"扫码登记"链接
function copyRegLink() {
    const input = document.getElementById('regLinkInput');
    input.select();
    navigator.clipboard.writeText(input.value).then(() => alert('链接已复制')).catch(() => document.execCommand('copy'));
}

// 切换"登记二维码"卡片显示的分公司：换一下图片和链接的 deptId 参数即可，不用整页刷新
function switchQrDept(deptId) {
    document.getElementById('regQrImg').src   = '?handler=Qr&deptId=' + deptId;
    document.getElementById('regLinkInput').value = REG_BASE_URL + '?deptId=' + deptId;
}

// 打开"驳回登记"弹窗
function openRejectReg(id, name) {
    document.getElementById('rejRegId').value = id;
    document.getElementById('rejRegName').textContent = name;
    document.getElementById('rejRegReason').value = '';
    new bootstrap.Modal(document.getElementById('rejectRegModal')).show();
}

// 点"确认录入"：复用"新增员工"弹窗，把员工扫码填的姓名/手机号/身份证号预填进去，
// 管理员只需要再补上工号、部门、考勤组等信息即可
function openConfirm(data) {
    scopedDeptTouched = false;   // 重新打开弹窗，允许管理范围再次跟着部门/角色自动带默认值
    document.getElementById('userModalTitle').textContent = '确认录入 - ' + data.realName;
    document.getElementById('userForm').action = '?handler=Create';
    document.getElementById('userSubmitBtn').textContent = '确认并创建账号';
    document.getElementById('fEditUserId').value = 0;
    document.getElementById('fRegistrationId').value = data.id;   // 标记这次创建关联的是哪条登记
    document.getElementById('pwdRow').style.display = '';
    document.getElementById('fEmployeeNo').value = '';
    document.getElementById('fRealName').value   = data.realName;
    setPosition(data.position || '');   // 扫码登记选的岗位（选项跟登记页同一份）
    document.getElementById('fPhone').value      = data.phone;
    document.getElementById('fIdNumber').value   = data.idNumber;
    document.getElementById('fContractCompany').value = data.contractCompany || '';
    document.getElementById('fHireDate').value   = '';
    document.getElementById('fHomeAddress').value = data.homeAddress || '';
    document.getElementById('fEmergencyContactName').value = data.emergencyContactName || '';
    document.getElementById('fEmergencyContactPhone').value = data.emergencyContactPhone || '';
    document.getElementById('fIdCardPhoto').value = '';   // 文件框留空=沿用员工登记时已上传的照片（服务端会自动带过去）
    if (data.idCardPhotoUrl) {
        document.getElementById('fIdCardPhotoPreviewWrap').style.display = '';
        document.getElementById('fIdCardPhotoPreview').src = data.idCardPhotoUrl;
        document.getElementById('fIdCardPhotoPreviewLink').href = data.idCardPhotoUrl;
    } else {
        document.getElementById('fIdCardPhotoPreviewWrap').style.display = 'none';
    }
    document.getElementById('fAllowRemotePunch').checked = false;   // 新建员工默认不开远程打卡
    document.getElementById('fAttendanceExempt').checked = false;   // 新建员工默认需要打卡
    document.getElementById('fRole').value       = 'Employee';
    // 优先用登记本身带的意向部门（扫的哪个分公司的码就是哪个）；没有的话（旧版通用链接提交的）
    // 才退回到当前左侧筛选树选中的部门，跟原来的兜底逻辑一致
    document.getElementById('fDeptId').value     = data.deptId != null ? data.deptId : (SELECTED_DEPT !== null ? SELECTED_DEPT : '');
    document.getElementById('fGroupId').value    = '';
    setSuperId('');
    setDeviceIds([]);
    setScopedDeptId(null);
    document.getElementById('fPhone').required   = true;
    document.getElementById('fPhoneReq').style.display = '';
    document.getElementById('fSuperIdSearch').required = true;
    document.getElementById('fSuperIdReq').style.display = '';
    document.getElementById('fDeptId').required  = true;
    document.getElementById('fDeptIdReq').style.display = '';
    autoFillEmployeeNo = true;
    fillEmployeeNoForDept(document.getElementById('fDeptId').value);   // 默认部门已选中时，直接按它生成一次
    suggestSupervisorForDept(document.getElementById('fDeptId').value);
    new bootstrap.Modal(document.getElementById('userModal')).show();
}

// 重置密码：弹窗填写（留空=随机生成，和以前的行为一致）
function openResetPwd(id, name) {
    document.getElementById('rpId').value = id;
    document.getElementById('rpName').textContent = name;
    document.getElementById('rpValue').value = '';
    new bootstrap.Modal(document.getElementById('resetPwdModal')).show();
}

// 编辑
function openEdit(data) {
    scopedDeptTouched = false;   // 重新打开弹窗，允许管理范围再次跟着部门/角色自动带默认值
    autoFillEmployeeNo = false;   // 编辑老员工，工号已经定了，切换部门不应该跟着自动改工号
    document.getElementById('userModalTitle').textContent = '编辑员工 - ' + data.realName;
    document.getElementById('userForm').action = '?handler=Update';
    document.getElementById('userSubmitBtn').textContent = '保存';
    document.getElementById('fEditUserId').value = data.id;
    document.getElementById('fRegistrationId').value = '';   // 编辑老员工，跟扫码登记无关
    document.getElementById('pwdRow').style.display = 'none';
    document.getElementById('fEmployeeNo').value = data.employeeNo;
    document.getElementById('fRealName').value   = data.realName;
    document.getElementById('fRole').value       = data.role;
    document.getElementById('fDeptId').value     = data.deptId || '';
    document.getElementById('fGroupId').value    = data.groupId || '';
    setSuperId(data.superId || '');
    setDeviceIds(data.deviceIds);
    setScopedDeptId(data.scopedDeptId);
    setPosition(data.position || '');
    document.getElementById('fPhone').value      = data.phone;
    document.getElementById('fPhone').required   = false;          // 编辑老员工不强制补填手机号
    document.getElementById('fPhoneReq').style.display = 'none';
    document.getElementById('fSuperIdSearch').required = false;    // 编辑老员工不强制补填直属上级
    document.getElementById('fSuperIdReq').style.display = 'none';
    document.getElementById('fDeptId').required  = false;          // 编辑老员工不强制补填部门（比如部门被删后"未分配"的员工）
    document.getElementById('fDeptIdReq').style.display = 'none';
    document.getElementById('fSuperIdHint').textContent = '选定部门后会自动带出该部门的主管，也可以手动改选。';
    document.getElementById('fIdNumber').value   = data.idNumber || '';
    document.getElementById('fContractCompany').value = data.contractCompany || '';
    document.getElementById('fHireDate').value   = data.hireDate;
    document.getElementById('fHomeAddress').value = data.homeAddress || '';
    document.getElementById('fEmergencyContactName').value  = data.emergencyContactName || '';
    document.getElementById('fEmergencyContactPhone').value = data.emergencyContactPhone || '';
    document.getElementById('fIdCardPhoto').value = '';   // 文件框永远从空开始，不选=保留原照片
    document.getElementById('fAllowRemotePunch').checked = !!data.allowRemotePunch;
    document.getElementById('fAttendanceExempt').checked = !!data.attendanceExempt;
    if (data.idCardPhotoUrl) {
        document.getElementById('fIdCardPhotoPreviewWrap').style.display = '';
        document.getElementById('fIdCardPhotoPreview').src = data.idCardPhotoUrl;
        document.getElementById('fIdCardPhotoPreviewLink').href = data.idCardPhotoUrl;
    } else {
        document.getElementById('fIdCardPhotoPreviewWrap').style.display = 'none';
    }
    new bootstrap.Modal(document.getElementById('userModal')).show();
}

// 岗位下拉框赋值：值不在选项里（老员工以前手填的岗位）时先补一个选项，不然下拉框会显示成空白、保存时把原岗位丢掉
function setPosition(value) {
    const sel = document.getElementById('fPosition');
    if (value && ![...sel.options].some(o => o.value === value)) {
        const opt = document.createElement('option');
        opt.value = value;
        opt.textContent = value;
        sel.appendChild(opt);
    }
    sel.value = value || '';
}
