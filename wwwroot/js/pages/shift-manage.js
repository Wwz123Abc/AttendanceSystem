const shiftModal = new bootstrap.Modal(document.getElementById('shiftModal'));
// 页面左侧默认选中的第一个考勤组，新增班次时用它做"所属考勤组"下拉的默认值，省得每次都要手选

// 点"新增班次"：把弹窗里所有输入框清空/恢复成默认值（上次编辑班次留下的旧内容不能带过来）
function openAddShift() {
    document.getElementById('shiftModalTitle').textContent = '新增班次';
    document.getElementById('shiftId').value = 0;   // 0 表示"新增"，OnPostSaveShiftAsync 靠这个区分新增还是修改
    document.getElementById('shiftGroupId').value = FIRST_GROUP || '';
    document.getElementById('fSName').value = '';
    document.getElementById('fWS').value = '09:00';
    document.getElementById('fWE').value = '18:00';
    document.getElementById('fLT').value = 5;
    document.getElementById('fET').value = 5;
    document.getElementById('fEI').value = 60;
    document.getElementById('fOT').value = 30;
    document.getElementById('fSH').value = 8;
    document.getElementById('fCross').checked = false;
    document.getElementById('fSColor').value = '#1890ff';
    setMidCheckWindows([]);
    setRestDays([0, 6]);   // 新建默认周六周日休息，和后台的默认值保持一致
    shiftModal.show();
}

// 点某个班次的"编辑"：把这个班次现有的各项值填进弹窗表单，方便直接在原有基础上改
function openEditShift(id, gid, name, ws, we, lt, et, ei, ot, cross, sh, color, restDaysJson, midWindowsJson) {
    document.getElementById('shiftModalTitle').textContent = '编辑班次';
    document.getElementById('shiftId').value = id;   // 非 0，OnPostSaveShiftAsync 据此判断是"修改"
    document.getElementById('shiftGroupId').value = gid;
    document.getElementById('fSName').value = name;
    document.getElementById('fWS').value = ws;
    document.getElementById('fWE').value = we;
    document.getElementById('fLT').value = lt;
    document.getElementById('fET').value = et;
    document.getElementById('fEI').value = ei;
    document.getElementById('fOT').value = ot;
    document.getElementById('fSH').value = sh;
    document.getElementById('fCross').checked = cross;
    document.getElementById('fSColor').value = color;
    setMidCheckWindows(JSON.parse(midWindowsJson || '[]'));
    setRestDays(JSON.parse(restDaysJson || '[]'));
    shiftModal.show();
}

// ── 午间必打卡窗口：动态增删的表单行（做法跟考勤组的"打卡地点"多行表单一样）──────────────
// Razor Pages 绑定 List<T> 要求索引从 0 开始连续编号，所以每次增删后都要重新给所有行编号。
function addMidCheckRow(data) {
    data = data || { start: '', end: '' };
    const div = document.createElement('div');
    div.className = 'mid-check-row d-flex align-items-end gap-2 mb-2';
    div.innerHTML = `
        <div class="flex-fill">
            <label class="form-label small mb-1 text-muted">开始</label>
            <input type="time" class="form-control form-control-sm" data-field="Start" />
        </div>
        <div class="flex-fill">
            <label class="form-label small mb-1 text-muted">结束</label>
            <input type="time" class="form-control form-control-sm" data-field="End" />
        </div>
        <button type="button" class="btn btn-outline-danger btn-sm" title="删除这一段" onclick="removeMidCheckRow(this)"><i class="bi bi-trash"></i></button>
    `;
    div.querySelector('[data-field="Start"]').value = data.start ?? '';
    div.querySelector('[data-field="End"]').value = data.end ?? '';
    document.getElementById('midCheckRows').appendChild(div);
    renumberMidCheckRows();
}

function removeMidCheckRow(btn) {
    btn.closest('.mid-check-row').remove();
    renumberMidCheckRows();
}

function renumberMidCheckRows() {
    document.querySelectorAll('#midCheckRows .mid-check-row').forEach((row, idx) => {
        row.querySelectorAll('[data-field]').forEach(input => {
            input.name = `MidCheckWindowInputs[${idx}].${input.dataset.field}`;
        });
    });
}

// 清空后按传入的窗口列表重新渲染（编辑时回显 / 新建时清空）
function setMidCheckWindows(list) {
    document.getElementById('midCheckRows').innerHTML = '';
    list.forEach(w => addMidCheckRow({ start: w.start, end: w.end }));
}

// 按传入的星期几编号列表，勾选/取消勾选"每周休息日"
function setRestDays(days) {
    document.querySelectorAll('.rest-day-chk').forEach(cb => {
        cb.checked = days.includes(parseInt(cb.value, 10));
    });
}

// 排班表单里"是否所选组全部成员"这个开关：勾上就不用再一个个选人了，
// 所以把下面"逐人挑选"的名单区域和"全选/清空"按钮都藏起来，减少不必要的操作
function toggleEmployeeSelect(allSelected) {
    document.getElementById('employeeSelect').style.display = allSelected ? 'none' : 'block';
    document.getElementById('pickTools').style.display = allSelected ? 'none' : 'block';
}
// "全选"/"清空"按钮：只对当前"按部门筛选"后还看得见的人生效，筛掉不显示的人不会被误勾/误清空
function checkAll(v) {
    document.querySelectorAll('.member-chk').forEach(c => {
        if (c.closest('.form-check').style.display !== 'none') c.checked = v;
    });
}

// 筛选批量排班的候选人名单：部门下拉 + 姓名/工号搜索框，两个条件同时生效（都满足才显示）；
// 不影响已勾选状态，清空筛选条件还能看到。一个考勤组下的人如果因为筛选全部被隐藏，
// 连组名标题一起隐藏，不留一个空标题。
function filterMembers() {
    const dept = document.getElementById('deptFilter').value;
    const kw   = document.getElementById('memberSearch').value.trim().toLowerCase();
    document.querySelectorAll('#employeeSelect .form-check').forEach(row => {
        const deptOk = dept === '' || row.dataset.dept === dept;
        const kwOk   = kw === '' || (row.dataset.search || '').includes(kw);
        row.style.display = (deptOk && kwOk) ? '' : 'none';
    });
    document.querySelectorAll('#employeeSelect .grp-header').forEach(header => {
        let sib = header.nextElementSibling, anyVisible = false;
        while (sib && !sib.classList.contains('grp-header')) {
            if (sib.style.display !== 'none') { anyVisible = true; break; }
            sib = sib.nextElementSibling;
        }
        header.style.display = anyVisible ? '' : 'none';
    });
}
// 搜索过滤考勤组（勾选状态不受影响，隐藏的已勾选项仍会一起提交）
function filterGroups(q) {
    q = (q || '').trim().toLowerCase();
    document.querySelectorAll('.grp-item').forEach(el => {
        const name = el.getAttribute('data-name') || '';
        el.style.display = (q === '' || name.includes(q)) ? '' : 'none';
    });
}
