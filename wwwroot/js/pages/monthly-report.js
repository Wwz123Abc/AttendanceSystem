// 勾选/取消"公司/部门"合并树里的某一项时，连带把它所有下级节点（部门/子部门）也一起勾上/取消，
// 勾公司节点就相当于把它下面所有部门都选中，范围可以很大；只勾具体部门，范围就很小。
function onReportDeptCheckboxChange(checkbox) {
    const checked = checkbox.checked;
    const nodeId  = checkbox.dataset.id;
    document.querySelectorAll(`#reportDeptTree .dept-checkbox[data-parent="${nodeId}"]`).forEach(child => {
        child.checked = checked;
        onReportDeptCheckboxChange(child);   // 递归处理下一级
    });
}
