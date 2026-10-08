function setDeptId(id) {
    const el = document.getElementById('fDepartmentId');
    if (el) el.value = id === null || id === undefined ? '' : id;
}
function openAdd() {
    document.getElementById('deviceModalTitle').textContent = '添加设备';
    document.getElementById('fId').value = 0;
    document.getElementById('fSN').value = '';
    document.getElementById('fName').value = '';
    document.getElementById('fIsActive').checked = true;
    setDeptId(null);
    new bootstrap.Modal(document.getElementById('deviceModal')).show();
}
function openEdit(id, sn, name, isActive, deptId) {
    document.getElementById('deviceModalTitle').textContent = '编辑设备 - ' + sn;
    document.getElementById('fId').value = id;
    document.getElementById('fSN').value = sn;
    document.getElementById('fName').value = name;
    document.getElementById('fIsActive').checked = isActive;
    setDeptId(deptId);
    new bootstrap.Modal(document.getElementById('deviceModal')).show();
}
