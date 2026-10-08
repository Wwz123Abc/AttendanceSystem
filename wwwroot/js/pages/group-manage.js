const modal = new bootstrap.Modal(document.getElementById('groupModal'));

function openAdd() {
    document.getElementById('modalTitle').textContent = '新建考勤组';
    document.getElementById('editId').value = 0;
    document.getElementById('fGroupName').value = '';
    document.getElementById('fLunch').value = 60;
    document.getElementById('fDinner').value = 30;
    document.getElementById('fEnableLocation').checked = false;
    toggleLocation(false);
    setSelectedApprovers([]);
    setSelectedDepts([]);
    setLocations([]);
    setApprovalLevel('Level1');
    modal.show();
}

function openEdit(id, name, enableLoc, lunch, dinner, approverIdsJson, deptIdsJson, locationsJson, approvalLevel) {
    document.getElementById('modalTitle').textContent = '编辑考勤组';
    document.getElementById('editId').value = id;
    document.getElementById('fGroupName').value = name;
    document.getElementById('fLunch').value = lunch;
    document.getElementById('fDinner').value = dinner;
    document.getElementById('fEnableLocation').checked = enableLoc;
    toggleLocation(enableLoc);
    setSelectedApprovers(JSON.parse(approverIdsJson || '[]'));
    setSelectedDepts(JSON.parse(deptIdsJson || '[]'));
    setLocations(JSON.parse(locationsJson || '[]'));
    setApprovalLevel(approvalLevel || 'Level1');
    modal.show();
}

// 按传入的审批人编号列表，勾选/取消勾选审批人多选框
function setSelectedApprovers(approverIds) {
    document.querySelectorAll('.approver-checkbox').forEach(cb => {
        cb.checked = approverIds.includes(parseInt(cb.value, 10));
    });
}

// 设置"审批层级"单选（Level1/Level2）
function setApprovalLevel(level) {
    document.getElementById('fLevel1').checked = level !== 'Level2';
    document.getElementById('fLevel2').checked = level === 'Level2';
}

// 提交前兜底校验：审批人必须至少选一个（和后端校验规则保持一致，避免用户绕过看不出哪里没填就直接提交）
document.getElementById('groupForm').addEventListener('submit', function (e) {
    const hasApprover = document.querySelectorAll('.approver-checkbox:checked').length > 0;
    if (!hasApprover) { e.preventDefault(); alert('请至少选择一位审批人'); }
});

// 按传入的部门编号列表，勾选/取消勾选部门树（这是回显已保存的数据，不需要级联）
function setSelectedDepts(deptIds) {
    document.querySelectorAll('.dept-checkbox').forEach(cb => {
        cb.checked = deptIds.includes(parseInt(cb.value, 10));
    });
    filterApprovers();   // 回显完部门勾选后，审批人名单也要跟着刷新一遍，不然编辑弹窗刚打开时名单和勾选状态对不上
}

// 手动勾选/取消勾选某个部门时：连带把它所有下级部门也一起勾上/取消（勾选大部门=连它底下的小部门一起跟这个考勤组）
function onDeptCheckboxChange(checkbox) {
    const checked = checkbox.checked;
    const deptId  = checkbox.dataset.id;
    document.querySelectorAll(`.dept-checkbox[data-parent="${deptId}"]`).forEach(child => {
        child.checked = checked;
        onDeptCheckboxChange(child);   // 递归处理下一级
    });
}

// 审批人名单跟着"所属部门"的勾选联动筛选：只显示所属部门在已勾选范围内的人；
// 一个部门都没勾时不做限制，显示全部候选人（避免刚打开新建弹窗、还没勾部门时名单就是空的）。
// 只是隐藏显示，不会连带取消已经勾选的审批人——防止切换部门勾选时，误把管理员已经选好的人清掉。
function filterApprovers() {
    const checkedDeptIds = new Set(
        [...document.querySelectorAll('#deptTreeList .dept-checkbox:checked')].map(cb => cb.value)
    );
    document.querySelectorAll('#approverList .form-check').forEach(row => {
        const show = checkedDeptIds.size === 0 || checkedDeptIds.has(row.dataset.dept);
        row.style.display = show ? '' : 'none';
    });
}

// "是否启用定位打卡"这个开关：勾上才需要配打卡地点，所以没勾的时候把这块藏起来
function toggleLocation(enabled) {
    document.getElementById('locationFields').style.display = enabled ? 'block' : 'none';
}

// ── 打卡地点：动态增删的表单行 ──────────────────────────────────────────────
// Razor Pages 绑定 List<T> 要求索引从 0 开始连续编号，所以每次增删后都要重新给所有行编号，
// 不能只在末尾自增一个从不回退的计数器（否则删中间一行后，索引会断掉，后面的行绑不上）。
function addLocationRow(data) {
    data = data || { name: '', lat: '', lng: '', radius: 500 };
    const div = document.createElement('div');
    div.className = 'location-row position-relative border rounded-3 p-3 pt-4 mb-3 bg-body-tertiary';
    div.innerHTML = `
        <span class="badge bg-secondary-subtle text-secondary-emphasis position-absolute top-0 start-0 mt-2 ms-2 location-index">地点</span>
        <button type="button" class="btn-close position-absolute top-0 end-0 mt-2 me-2" title="删除这个地点" onclick="removeLocationRow(this)"></button>
        <div class="row g-2">
            <div class="col-12 col-md-5">
                <label class="form-label small mb-1 text-muted">地点名称</label>
                <input type="text" maxlength="200" class="form-control form-control-sm" data-field="Name" placeholder="如：总部大楼" />
            </div>
            <div class="col-6 col-md-2">
                <label class="form-label small mb-1 text-muted">纬度</label>
                <input type="number" step="0.000001" min="-90" max="90" class="form-control form-control-sm" data-field="Latitude" />
            </div>
            <div class="col-6 col-md-2">
                <label class="form-label small mb-1 text-muted">经度</label>
                <input type="number" step="0.000001" min="-180" max="180" class="form-control form-control-sm" data-field="Longitude" />
            </div>
            <div class="col-6 col-md-1">
                <label class="form-label small mb-1 text-muted">半径(米)</label>
                <input type="number" min="0" max="5000" class="form-control form-control-sm" data-field="Radius" />
            </div>
            <div class="col-6 col-md-2 d-flex align-items-end gap-1">
                ${AMAP_ENABLED ? '<button type="button" class="btn btn-outline-primary btn-sm flex-fill" title="搜地址/地图选点" onclick="openAMapPicker(this)"><i class="bi bi-map"></i> 选点</button>' : ''}
                <button type="button" class="btn btn-outline-secondary btn-sm flex-fill" title="获取当前位置" onclick="fillMyLocation(this)"><i class="bi bi-geo-alt"></i></button>
            </div>
        </div>
    `;
    div.querySelector('[data-field="Name"]').value = data.name ?? '';
    div.querySelector('[data-field="Latitude"]').value = data.lat ?? '';
    div.querySelector('[data-field="Longitude"]').value = data.lng ?? '';
    div.querySelector('[data-field="Radius"]').value = data.radius ?? 500;
    document.getElementById('locationRows').appendChild(div);
    renumberLocationRows();
    // 新加的地点必须马上看得见，所以清掉搜索条件（否则新行会因为名称是空的被藏起来）
    const box = document.getElementById('locationSearch');
    if (box) box.value = '';
    filterLocationRows();
}

function removeLocationRow(btn) {
    btn.closest('.location-row').remove();
    renumberLocationRows();
    filterLocationRows();
}

// 按名称搜索已添加的地点：只是把不匹配的行藏起来（display:none），行还在表单里，
// 所以保存时所有地点照常提交，搜索不会造成漏存；行的编号也不受影响。
function filterLocationRows() {
    const rows = document.querySelectorAll('#locationRows .location-row');
    const kw = (document.getElementById('locationSearch').value || '').trim().toLowerCase();
    let shown = 0;
    rows.forEach(row => {
        const name = (row.querySelector('[data-field="Name"]').value || '').toLowerCase();
        const hit = !kw || name.includes(kw);
        row.style.display = hit ? '' : 'none';
        if (hit) shown++;
    });
    document.getElementById('locationSearchBar').style.display = rows.length ? 'flex' : 'none';
    document.getElementById('locationSearchCount').textContent = kw ? `${shown} / ${rows.length}` : `共 ${rows.length} 个`;
    document.getElementById('locationNoMatch').style.display = (kw && rows.length && !shown) ? 'block' : 'none';
}

// 重新按当前 DOM 顺序，把每一行的 name 属性编成 Locations[0]、Locations[1]……连续序号，
// 顺带把左上角的"地点 N"角标也刷新一遍（增删之后序号要跟着变）
function renumberLocationRows() {
    document.querySelectorAll('#locationRows .location-row').forEach((row, idx) => {
        row.querySelectorAll('[data-field]').forEach(input => {
            input.name = `Locations[${idx}].${input.dataset.field}`;
        });
        const badge = row.querySelector('.location-index');
        if (badge) badge.textContent = `地点 ${idx + 1}`;
    });
}

// 清空后按传入的地点列表重新渲染（编辑时回显 / 新建时清空）
function setLocations(list) {
    document.getElementById('locationRows').innerHTML = '';
    list.forEach(l => addLocationRow({ name: l.name, lat: l.lat, lng: l.lng, radius: l.radius }));
    document.getElementById('locationSearch').value = '';
    filterLocationRows();
}

// "获取当前位置"按钮：直接用管理员当前所在的浏览器定位，自动填进这一行的经纬度框
function fillMyLocation(btn) {
    if (!navigator.geolocation) { alert('浏览器不支持定位'); return; }
    const row = btn.closest('.location-row');
    navigator.geolocation.getCurrentPosition(pos => {
        row.querySelector('[data-field="Latitude"]').value = pos.coords.latitude.toFixed(6);
        row.querySelector('[data-field="Longitude"]').value = pos.coords.longitude.toFixed(6);
    }, () => alert('获取位置失败，请手动输入'));
}

// ── 高德地图选点：搜地址/点地图/拖图钉，取代手动查经纬度 ──────────────────────────
// 高德地图用的是 GCJ-02（"火星坐标系"），和员工手机打卡时浏览器 GPS 给出的 WGS-84 坐标
// 不是一回事，中国大陆范围内两者能差出几十到几百米。本系统的打卡范围判定全程用的是 WGS-84
// （navigator.geolocation 原始坐标），所以从地图上选完点之后，必须先把坐标纠偏转换成 WGS-84
// 再写回表单，不然算出来的"是否在范围内"会跟员工实际打卡的地方对不上。
// gcj02ToWgs84 / wgs84ToGcj02 是业界公开的坐标纠偏算法（GCJ-02 与 WGS-84 之间的标准换算公式），只是本地数学换算；
// 两个方向共用同一套"偏移量"计算（_gcjWgsDelta：偏移量只跟大致所在位置有关，在几百米范围内可以双向近似复用，
// 这是国内地图坐标转换的通行做法）。
function _outOfChina(lng, lat) { return lng < 72.004 || lng > 137.8347 || lat < 0.8293 || lat > 55.8271; }
function _gcjWgsDelta(lng, lat) {
    const PI = Math.PI, A = 6378245.0, EE = 0.00669342162296594323;
    function transformLat(x, y) {
        let ret = -100 + 2 * x + 3 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.sqrt(Math.abs(x));
        ret += (20 * Math.sin(6 * x * PI) + 20 * Math.sin(2 * x * PI)) * 2 / 3;
        ret += (20 * Math.sin(y * PI) + 40 * Math.sin(y / 3 * PI)) * 2 / 3;
        ret += (160 * Math.sin(y / 12 * PI) + 320 * Math.sin(y * PI / 30)) * 2 / 3;
        return ret;
    }
    function transformLng(x, y) {
        let ret = 300 + x + 2 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.sqrt(Math.abs(x));
        ret += (20 * Math.sin(6 * x * PI) + 20 * Math.sin(2 * x * PI)) * 2 / 3;
        ret += (20 * Math.sin(x * PI) + 40 * Math.sin(x / 3 * PI)) * 2 / 3;
        ret += (150 * Math.sin(x / 12 * PI) + 300 * Math.sin(x / 30 * PI)) * 2 / 3;
        return ret;
    }
    let dLat = transformLat(lng - 105, lat - 35);
    let dLng = transformLng(lng - 105, lat - 35);
    const radLat = lat / 180 * PI;
    let magic = Math.sin(radLat);
    magic = 1 - EE * magic * magic;
    const sqrtMagic = Math.sqrt(magic);
    dLat = (dLat * 180) / ((A * (1 - EE)) / (magic * sqrtMagic) * PI);
    dLng = (dLng * 180) / (A / sqrtMagic * Math.cos(radLat) * PI);
    return [dLng, dLat];
}
function gcj02ToWgs84(lng, lat) {
    if (_outOfChina(lng, lat)) return [lng, lat];
    const [dLng, dLat] = _gcjWgsDelta(lng, lat);
    return [lng - dLng, lat - dLat];
}
// 反过来，把数据库里存的 WGS-84 坐标转换成高德地图要用的 GCJ-02——重新打开选点弹窗、要在地图上
// 显示"已经存过的位置"时要用。以前这里没转换，直接拿 WGS-84 坐标当 GCJ-02 用来定位地图中心/图钉，
// 在国内会偏移出几十到几百米（2026-09-29 反馈：编辑已保存的定位打卡地点，重新打开选点弹窗，图钉位置和
// 保存时选的地址对不上）。只影响这个弹窗重新打开时"图钉显示在哪"，不影响保存逻辑——保存时仍然是按
// 这次在地图上重新选/确认的点，通过 gcj02ToWgs84 换算出 WGS-84 再写回表单。
function wgs84ToGcj02(lng, lat) {
    if (_outOfChina(lng, lat)) return [lng, lat];
    const [dLng, dLat] = _gcjWgsDelta(lng, lat);
    return [lng + dLng, lat + dLat];
}

let amapMap, amapMarker, amapGeocoder, amapPlaceSearch, amapTargetRow, amapPickerState, amapSearchTimer;

// 首次打开选点弹窗时才初始化地图（弹窗没显示之前容器没有尺寸，地图会渲染不正常）
function initAMapPicker() {
    if (amapMap) return;
    amapMap = new AMap.Map('amapPickerMap', { zoom: 15, center: [113.75, 22.98] });
    amapMarker = new AMap.Marker({ position: amapMap.getCenter(), draggable: true, cursor: 'move' });
    amapMarker.setMap(amapMap);

    // AMap.Geocoder / AMap.PlaceSearch 是"插件"：虽然上面加载地图脚本的地址里已经带了 plugin= 参数，
    // 但那只是让浏览器提前把插件代码下载好，不代表这两个类此刻就能直接 new 出来——必须用 AMap.plugin(...)
    // 包一层回调，确认插件真正加载完成后再使用；之前没包这一层，导致偶尔"点了搜索没反应"。
    // （原来用的 AMap.AutoComplete"输入提示"插件实测会内部报错、一直查不出结果，换成 PlaceSearch 这个
    // 地点搜索插件，用法类似但更稳定。）
    AMap.plugin(['AMap.Geocoder', 'AMap.PlaceSearch'], () => {
        amapGeocoder    = new AMap.Geocoder();
        amapPlaceSearch = new AMap.PlaceSearch({ pageSize: 8 });   // 不绑定 input，只用来在下面手动调用 .search()
    });

    amapMarker.on('dragend', () => {
        const pos = amapMarker.getPosition();
        reverseGeocodeAMap([pos.lng, pos.lat]);
    });
    amapMap.on('click', e => {
        hideAMapSearchResults();
        amapMarker.setPosition(e.lnglat);
        reverseGeocodeAMap([e.lnglat.lng, e.lnglat.lat]);
    });

    document.getElementById('amapAddressInput').addEventListener('input', onAMapAddressInput);
}

// 反查某个坐标（高德 GCJ-02）对应的地址文字（拖图钉/点地图之后用）
function reverseGeocodeAMap(gcjLngLat) {
    if (!amapGeocoder) { setAMapPickerState(gcj02ToWgs84(gcjLngLat[0], gcjLngLat[1]), ''); return; }
    amapGeocoder.getAddress(gcjLngLat, (status, result) => {
        const name = (status === 'complete' && result.regeocode) ? result.regeocode.formattedAddress : '';
        setAMapPickerState(gcj02ToWgs84(gcjLngLat[0], gcjLngLat[1]), name);
    });
}

// 记下当前选中的坐标（统一存 WGS-84，和数据库口径一致）+ 地址文字，供"使用此位置"按钮取用
function setAMapPickerState(wgsLngLat, name) {
    amapPickerState = { wgs: wgsLngLat, name };
    document.getElementById('amapPickerResult').textContent =
        (name ? `已选：${name}　` : '') + `坐标：${wgsLngLat[1].toFixed(6)}, ${wgsLngLat[0].toFixed(6)}`;
}

// ── 地址搜索：只搜中国境内（用高德的地点搜索），不再找境外/全球——公司打卡地点本来就都在国内，
// 境外搜索走的是国外网站，国内网络常年访问不了，留着只会拖慢/卡住整个搜索，不如直接去掉。
// 按输入停顿 400ms 后再查（不是每敲一个字就查一次），避免搜索请求过于频繁。
function onAMapAddressInput(e) {
    clearTimeout(amapSearchTimer);
    const keyword = e.target.value.trim();
    if (keyword.length < 2) { hideAMapSearchResults(); return; }
    amapSearchTimer = setTimeout(() => runAMapAddressSearch(keyword), 400);
}

async function runAMapAddressSearch(keyword) {
    const pois = await searchAMapPlaces(keyword);
    const items = pois.map(p => ({
        label: [p.name, p.district || p.address].filter(Boolean).join('　'),
        gcj: [p.location.lng, p.location.lat]
    }));
    renderAMapSearchResults(items);
}

// 高德的 search() 偶尔会内部出错、回调一直不触发，加个兜底超时，不然会一直卡着不出结果。
function searchAMapPlaces(keyword) {
    return new Promise(resolve => {
        if (!amapPlaceSearch) { resolve([]); return; }
        let done = false;
        const finish = list => { if (!done) { done = true; resolve(list); } };
        const safetyTimer = setTimeout(() => finish([]), 3000);
        try {
            amapPlaceSearch.search(keyword, (status, result) => {
                clearTimeout(safetyTimer);
                const pois = status === 'complete' && result.poiList && result.poiList.pois ? result.poiList.pois : [];
                finish(pois.filter(p => p.location));
            });
        } catch {
            clearTimeout(safetyTimer);
            finish([]);
        }
    });
}

function renderAMapSearchResults(items) {
    const box = document.getElementById('amapSearchResults');
    if (items.length === 0) { hideAMapSearchResults(); return; }
    box.innerHTML = items.map((it, i) =>
        `<button type="button" class="list-group-item list-group-item-action py-1 px-2 small" data-idx="${i}">${escapeHtml(it.label)}</button>`
    ).join('');
    box.style.display = 'block';
    box.querySelectorAll('[data-idx]').forEach(btn => {
        btn.addEventListener('click', () => {
            const it = items[parseInt(btn.dataset.idx, 10)];
            document.getElementById('amapAddressInput').value = it.label;
            hideAMapSearchResults();
            // 高德给的是 GCJ-02，地图直接用，存库前再转成 WGS-84
            amapMap.setCenter(it.gcj); amapMap.setZoom(17); amapMarker.setPosition(it.gcj);
            setAMapPickerState(gcj02ToWgs84(it.gcj[0], it.gcj[1]), it.label);
        });
    });
}

function hideAMapSearchResults() {
    const box = document.getElementById('amapSearchResults');
    box.style.display = 'none';
    box.innerHTML = '';
}

// 点弹窗里搜索框/结果列表以外的地方（比如地图、空白处）时，把结果下拉收起来
document.getElementById('amapPickerModal').addEventListener('click', e => {
    if (!e.target.closest('#amapAddressInput') && !e.target.closest('#amapSearchResults')) hideAMapSearchResults();
});

// 点某一行的"地图选点"按钮：记下目标行，打开弹窗，首次显示时才初始化地图（此时容器才有尺寸）
function openAMapPicker(btn) {
    amapTargetRow = btn.closest('.location-row');
    // 上一次选点留下的坐标/地址不能带到这一次：给"还没填坐标"的行选点时，如果定位/反查还没返回就点
    // "使用此位置"，会把上一行的坐标和地址名静默写进当前行（围栏配错，员工在正确地点反而打不上卡）
    amapPickerState = null;
    document.getElementById('amapPickerResult').textContent = '';
    document.getElementById('amapAddressInput').value = '';
    hideAMapSearchResults();
    const modalEl = document.getElementById('amapPickerModal');
    const pickerModal = bootstrap.Modal.getOrCreateInstance(modalEl);
    const onShown = () => {
        initAMapPicker();
        if (typeof amapMap.resize === 'function') amapMap.resize();   // 弹窗打开前地图容器是隐藏的，尺寸不对，显示后要重算一次

        const lat = amapTargetRow.querySelector('[data-field="Latitude"]').value;
        const lng = amapTargetRow.querySelector('[data-field="Longitude"]').value;
        if (lat && lng) {
            // 这一行已经存过坐标：数据库里存的是 WGS-84，地图上要用高德的 GCJ-02 显示，
            // 先转换一次再定位地图中心/图钉，不然图钉位置会跟保存时选的地址明显偏移。
            // 保存时仍然是按这次在地图上重新选/确认的点重新计算 WGS-84，不会把近似值带回数据库。
            const lnglat = wgs84ToGcj02(parseFloat(lng), parseFloat(lat));
            amapMap.setCenter(lnglat);
            amapMarker.setPosition(lnglat);
            reverseGeocodeAMap(lnglat);
        } else if (navigator.geolocation) {
            navigator.geolocation.getCurrentPosition(pos => {
                const lnglat = [pos.coords.longitude, pos.coords.latitude];
                amapMap.setCenter(lnglat);
                amapMarker.setPosition(lnglat);
                reverseGeocodeAMap(lnglat);
            });
        }
        modalEl.removeEventListener('shown.bs.modal', onShown);
    };
    modalEl.addEventListener('shown.bs.modal', onShown);
    pickerModal.show();
}

// 点"使用此位置"：把（WGS-84 的）经纬度和地址名称写回目标行的表单
function confirmAMapPicker() {
    if (!amapPickerState) { alert('请先搜索地址，或在地图上点选/拖动图钉'); return; }
    if (amapPickerState.name) amapTargetRow.querySelector('[data-field="Name"]').value = amapPickerState.name;
    amapTargetRow.querySelector('[data-field="Latitude"]').value = amapPickerState.wgs[1].toFixed(6);
    amapTargetRow.querySelector('[data-field="Longitude"]').value = amapPickerState.wgs[0].toFixed(6);
    bootstrap.Modal.getInstance(document.getElementById('amapPickerModal')).hide();
}

// escapeHtml 已在 _Layout.cshtml 里全局定义一份，这里不再重复声明（2026-09-21 去重）。
