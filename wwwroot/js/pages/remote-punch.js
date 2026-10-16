function updateTime() {
    const el = document.getElementById('currentTime');
    if (!el) return;   // 没有打卡权限/没录入人脸时页面不渲染时间块
    const now = new Date();
    el.textContent =
        now.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
}
updateTime();
setInterval(updateTime, 1000);

// 页面内的醒目红色提示（代替 alert 弹窗：弹窗在手机上容易被忽略，也不会留在页面上让员工截图）
function showClientError(msg) {
    const box = document.getElementById('clientAlert');
    if (!box) { alert(msg); return; }
    document.getElementById('clientAlertText').textContent = msg;
    box.classList.remove('d-none');
    box.scrollIntoView({ behavior: 'smooth', block: 'center' });
}
function clearClientError() {
    const box = document.getElementById('clientAlert');
    if (box) box.classList.add('d-none');
}
// 服务器返回的错误提示（页面顶部那条红字）如果不在屏幕里，滚到它那里
document.addEventListener('DOMContentLoaded', () => {
    const serverError = document.querySelector('.alert-danger:not(#clientAlert)');
    if (serverError) serverError.scrollIntoView({ block: 'center' });
});

// 微信内置浏览器对摄像头/定位这两个标准网页接口限制比较多（不是走微信官方 JS-SDK 认证过的网页，
// getUserMedia/navigator.geolocation 经常直接拿不到权限），提前提示引导用户切到系统浏览器，
// 不然用户会看不懂"定位/摄像头失败"到底是哪里的问题，白白重试好几次。
if (/MicroMessenger/i.test(navigator.userAgent)) {
    const wechatWarning = document.getElementById('wechatWarning');
    if (wechatWarning) wechatWarning.classList.remove('d-none');
}

// 远程打卡的定位是必填项——这里没有围栏可跳过，拿不到定位就没法提交。
// 手机第一次定位经常是靠基站/WiFi 粗略估出来的，精度差（几十上百米很常见），过几秒 GPS
// 信号锁定后会越来越准——所以不再是"拿到第一个结果就完事"，而是持续监听一小段时间，
// 一直取精度最好的一次，直到精度已经很好（≤20米）或者等够了 20 秒才停下来。
// 20 秒（原来 10 秒）是为了照顾厂房/车间这类钢结构建筑内 GPS 信号弱、锁定慢的场景，
// 给手机多一点时间搜星，减少"其实过几秒能定位到，但等待时间不够被判定失败"的情况。
let bestPosition = null;
let locationWatchId = null;
const LOCATION_GOOD_ENOUGH_METERS = 20;
const LOCATION_MAX_WAIT_MS = 20000;

function applyLocation(pos) {
    bestPosition = pos;
    const lat = pos.coords.latitude.toFixed(6);
    const lng = pos.coords.longitude.toFixed(6);
    document.getElementById('lat').value = lat;
    document.getElementById('lng').value = lng;
    document.getElementById('accuracy').value = pos.coords.accuracy;
    const info = document.getElementById('locationInfo');
    if (info) {
        info.classList.remove('d-none');
        document.getElementById('locationText').textContent =
            `${lat}, ${lng}（精度 ±${Math.round(pos.coords.accuracy)}m）`;
    }
    scheduleLocationCheck(lat, lng, pos.coords.accuracy);
    // 定位成功了，之前（比如信号一开始弱、隔了几秒才锁定）弹出来的失败提示要跟着收起，
    // 不然明明已经定位成功了，页面上还挂着一条"未能获取位置"的红字，容易让人误以为定位没成功
    const alertBox = document.getElementById('locationAlert');
    if (alertBox) alertBox.classList.add('d-none');
}

// 位置在不在考勤组允许的打卡范围内：拿到定位就问服务器一次（定位会连续刷新几次，隔 1 秒再问，只问最后一次），
// 超出范围立刻红字提示，不用等点了"打卡"才知道。这里只是提前提醒，真正打卡时服务器还会再校验一遍。
let locationCheckTimer = null;
let locationOutOfRangeMessage = '';
function scheduleLocationCheck(lat, lng, acc) {
    clearTimeout(locationCheckTimer);
    locationCheckTimer = setTimeout(async () => {
        try {
            const resp = await fetch(`?handler=CheckLocation&lat=${lat}&lng=${lng}&acc=${acc}`, { headers: { 'Accept': 'application/json' } });
            if (!resp.ok) return;
            const r = await resp.json();
            const box = document.getElementById('locationWarn');
            if (!box) return;
            locationOutOfRangeMessage = r.valid ? '' : (r.message || '当前位置不在打卡范围内');
            document.getElementById('locationWarnText').textContent = locationOutOfRangeMessage;
            box.classList.toggle('d-none', r.valid);
            setLocationOk(!!r.valid);
        } catch (e) {
            // 检查本身失败（网络抖动等）：不拦员工，直接放行去开摄像头，提交时服务器还会再校验一遍
            setLocationOk(true);
        }
    }, 1000);
}

if (navigator.geolocation) {
    locationWatchId = navigator.geolocation.watchPosition(
        pos => {
            if (!bestPosition || pos.coords.accuracy < bestPosition.coords.accuracy) {
                applyLocation(pos);
            }
            if (pos.coords.accuracy <= LOCATION_GOOD_ENOUGH_METERS && locationWatchId !== null) {
                navigator.geolocation.clearWatch(locationWatchId);
                locationWatchId = null;
            }
        },
        err => {
            if (bestPosition) return;   // 已经有一次能用的定位了，后续报错（比如信号暂时丢失）不用管
            const alertBox = document.getElementById('locationAlert');
            const status = document.getElementById('locationStatus');
            if (alertBox && status) {
                // 按报错原因给出具体的处理办法，而不是统一一句"未能获取位置"
                const reasons = {
                    1: '您拒绝了定位权限。请到手机"设置 → 浏览器（Safari/Chrome）→ 位置"里改成"允许"，然后刷新本页（远程打卡必须要有定位）',
                    2: '手机暂时定不到位置。请打开手机的定位（GPS）开关，走到信号好的地方（室外或窗边），然后刷新本页',
                    3: '获取位置超时。请检查网络和手机定位是否开启，走到信号好的地方后刷新本页重试'
                };
                status.textContent = reasons[err.code] || '未能获取位置，请检查浏览器定位权限后刷新重试（远程打卡必须要有定位）';
                alertBox.classList.remove('d-none');
            }
            setLocationOk(false, true);
        },
        { timeout: 15000, maximumAge: 0, enableHighAccuracy: true }
    );
    setTimeout(() => {
        if (locationWatchId !== null) {
            navigator.geolocation.clearWatch(locationWatchId);
            locationWatchId = null;
        }
    }, LOCATION_MAX_WAIT_MS);
} else {
    const alertBox = document.getElementById('locationAlert');
    const status = document.getElementById('locationStatus');
    if (alertBox && status) {
        status.textContent = '当前浏览器不支持定位，无法使用远程打卡';
        alertBox.classList.remove('d-none');
    }
}

// ── 摄像头实时预览：不再是"选一张照片上传"，而是页面上直接开摄像头，
//    打卡的一瞬间截取当前画面去识别，更接近考勤机现场刷脸的感觉 ──
const cameraVideo = document.getElementById('cameraPreview');
let cameraReady = false;
let cameraStream = null;
let cameraWatchTimer = null;

// 检查摄像头到底有没有画面：getUserMedia 成功只说明拿到了摄像头流，预览可能仍是黑的
function checkCameraFrames() {
    if (cameraVideo.videoWidth > 0 && cameraVideo.videoHeight > 0) {
        document.getElementById('cameraReadyBadge').classList.remove('d-none');
        document.getElementById('cameraAlert').classList.add('d-none');
        document.getElementById('cameraRetry').classList.add('d-none');
    } else {
        showCameraError('摄像头预览没有出画面（黑屏）。请点右边"重试摄像头"；还不行就刷新页面、重新允许摄像头，或改用"提交补卡申请"。');
        document.getElementById('cameraRetry').classList.remove('d-none');
    }
}
function restartCamera() {
    if (cameraStream) cameraStream.getTracks().forEach(t => t.stop());
    cameraVideo.srcObject = null;
    document.getElementById('cameraReadyBadge').classList.add('d-none');
    startCamera();
}

async function startCamera() {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        showCameraError(window.isSecureContext === false
            ? '当前页面不是安全连接（https），浏览器不允许使用摄像头，请用正式网址打开本页'
            : '当前浏览器不支持摄像头识别，请换用手机自带的浏览器（iPhone 用 Safari，安卓用 Chrome）打开本页');
        return;
    }
    try {
        const stream = await navigator.mediaDevices.getUserMedia({
            video: { facingMode: 'user', width: { ideal: 480 }, height: { ideal: 480 } },
            audio: false
        });
        cameraStream = stream;
        cameraVideo.srcObject = stream;
        // iPhone 上 autoplay 有时不生效，摄像头已授权（绿点亮着）但预览是黑的；显式 play() 一次
        cameraVideo.play().catch(() => { /* 被拦就等下面的检测提示重试 */ });
        cameraReady = true;
        document.getElementById('cameraReadyBadge').classList.add('d-none');   // 真的出画面了才显示"已出画面"
        // 2.5 秒后还是没有画面（宽度 0）：不能让员工对着黑屏干等，提示重试
        clearTimeout(cameraWatchTimer);
        cameraWatchTimer = setTimeout(checkCameraFrames, 2500);
        cameraVideo.onplaying = () => setTimeout(checkCameraFrames, 300);
    } catch (err) {
        const msgs = {
            NotAllowedError: '摄像头权限被拒绝。请到手机"设置 → 浏览器 → 摄像头"改成"允许"，然后刷新本页',
            PermissionDeniedError: '摄像头权限被拒绝。请到手机"设置 → 浏览器 → 摄像头"改成"允许"，然后刷新本页',
            NotFoundError: '没有找到可用的摄像头，请确认手机前置摄像头正常',
            NotReadableError: '摄像头正被别的程序占用（比如视频通话、其他浏览器页面）。请先关闭它们再点"重试摄像头"',
            OverconstrainedError: '摄像头不支持所需的拍摄设置，请换一部手机或浏览器再试',
            SecurityError: '浏览器出于安全原因禁止使用摄像头，请用正式网址（https）打开本页'
        };
        showCameraError(msgs[err && err.name] || '未能打开摄像头，请检查浏览器摄像头权限后刷新重试');
        document.getElementById('cameraRetry').classList.remove('d-none');
    }
}
function showCameraError(msg) {
    const alertBox = document.getElementById('cameraAlert');
    const status = document.getElementById('cameraStatus');
    if (alertBox && status) {
        status.textContent = msg;
        alertBox.classList.remove('d-none');
    }
}
// 先校验位置、通过了才打开摄像头：位置不在打卡范围内，打开摄像头也打不了卡，还会让员工对着黑屏/画面干着急
// （2026-10-16 陈贻路：离打卡点 158 米，页面只显示摄像头黑屏，看不到"超出范围"）。
// 位置核对结果由 scheduleLocationCheck 回来后调用 setLocationOk。
let locationOk = null;   // null = 还没核对；true = 在范围内；false = 超出范围
function setLocationOk(ok, locationUnavailable) {
    locationOk = ok;
    const wait = document.getElementById('cameraWait');
    const btn = document.getElementById('punchBtn');
    if (ok) {
        if (wait) wait.classList.add('d-none');
        if (btn) btn.disabled = false;
        if (cameraVideo && !cameraStream) startCamera();
        refreshPunchPreview();
    } else {
        // 超出范围：不开摄像头；已经开着的（定位先差后好又变差）关掉
        if (cameraStream) { cameraStream.getTracks().forEach(t => t.stop()); cameraStream = null; cameraVideo.srcObject = null; cameraReady = false; }
        document.getElementById('cameraReadyBadge').classList.add('d-none');
        if (wait) {
            wait.textContent = locationUnavailable
                ? '没有拿到您的位置，不能打卡（摄像头不会打开）——请按上面的提示处理后刷新本页'
                : '当前位置不在打卡范围内，不能打卡（摄像头不会打开）';
            wait.classList.remove('d-none');
        }
        if (btn) btn.disabled = true;
        hidePunchPreview();
    }
}

// 从视频当前画面截一帧，压成不大的 JPEG（限宽 480，够人脸识别用，不用传原始高清大图）
function captureFrame() {
    const canvas = document.getElementById('captureCanvas');
    if (!cameraVideo.videoWidth || !cameraVideo.videoHeight) return '';   // 摄像头还没出画面：不能截（会得到 0×0 的空画布）
    const maxW = 480;
    const scale = cameraVideo.videoWidth > maxW ? maxW / cameraVideo.videoWidth : 1;
    canvas.width  = Math.round(cameraVideo.videoWidth * scale);
    canvas.height = Math.round(cameraVideo.videoHeight * scale);
    canvas.getContext('2d').drawImage(cameraVideo, 0, 0, canvas.width, canvas.height);
    return canvas.toDataURL('image/jpeg', 0.85);
}

function submitPunch() {
    clearClientError();
    if (locationOk === false) {
        showClientError(locationOutOfRangeMessage || '当前位置不在打卡范围内，不能打卡');
        return;
    }
    if (locationOk === null) {
        showClientError('正在核对您的位置，请稍候几秒再点打卡；如果一直没有反应，请检查浏览器定位权限后刷新页面');
        return;
    }
    if (previewBlockedMessage) {
        showClientError(previewBlockedMessage);
        return;
    }
    if (!cameraReady) {
        showClientError('摄像头还没准备好，请稍候再试，或点"重试摄像头"/刷新页面重新授权摄像头权限');
        return;
    }
    // 防连点：提交后立即禁用按钮，避免网络慢时手快连点两下，同时打出两个请求——
    // 每个都会真的调一次收费的阿里云接口，白白多花钱，也会在识别记录里留下容易看着困惑的重复条目
    const btn = document.getElementById('punchBtn');
    if (btn.disabled) return;
    // getUserMedia 成功只代表"拿到了摄像头流"，画面可能还没出来（手机上常见）——截不到画面就不提交
    const photo = captureFrame();
    if (!photo || photo.length < 1000) {
        showClientError('摄像头还没出画面，请等预览画面出现后再点打卡；如果一直没有画面，请点"重试摄像头"或刷新页面并允许摄像头权限');
        return;
    }
    btn.disabled = true;
    document.getElementById('punchBtnText').textContent = '正在识别并打卡…';
    document.getElementById('capturedPhotoData').value = photo;
    // 网络慢或中途断开时页面会一直停在这里：25 秒还没跳转，明确告诉员工别重复点、怎么确认有没有打上
    setTimeout(() => {
        showClientError('等了很久还没有结果，可能是网络太慢或已断开。请先不要反复点打卡：稍后到"我的记录"看看这次有没有打上，没有再刷新本页重试。');
    }, 25000);
    document.getElementById('punchForm').submit();
}

// ── 打卡前预览：这次会记成什么卡（上班 / 下班 / 午间），以及要提醒的事 ──
// 位置核对通过后问服务器一次，之后每分钟刷新一次（时间过了午间窗口、快到下班时间，记成什么卡会变）。
let previewBlockedMessage = '';
let previewTimer = null;
function hidePunchPreview() {
    const box = document.getElementById('punchPreview');
    if (box) box.classList.add('d-none');
    previewBlockedMessage = '';
    clearInterval(previewTimer);
    previewTimer = null;
}
async function refreshPunchPreview() {
    try {
        const resp = await fetch('?handler=Preview', { headers: { 'Accept': 'application/json' } });
        if (!resp.ok) return;
        const r = await resp.json();
        const box = document.getElementById('punchPreview');
        if (!box) return;
        document.getElementById('punchPreviewType').textContent = r.type + '打卡';
        const hint = document.getElementById('punchPreviewHint');
        hint.textContent = (r.hint || '').replaceAll('**', '');
        box.classList.remove('d-none');
        box.classList.toggle('alert-info', !r.blocked);
        box.classList.toggle('alert-danger', !!r.blocked);
        previewBlockedMessage = r.blocked ? (r.hint || '') : '';
        const btn = document.getElementById('punchBtn');
        if (btn && locationOk === true) btn.disabled = !!r.blocked;
        const text = document.getElementById('punchBtnText');
        if (text && !btn.disabled) text.textContent = '打卡（记为' + r.type + '卡）';
        if (!previewTimer) previewTimer = setInterval(refreshPunchPreview, 60000);
    } catch (e) { /* 预览失败不影响打卡，提交时服务器会自己判断 */ }
}

