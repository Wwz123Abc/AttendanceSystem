function updateTime() {
    const el = document.getElementById('currentTime');
    if (!el) return;   // 没有打卡权限/没录入人脸时页面不渲染时间块
    const now = new Date();
    el.textContent =
        now.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
}
updateTime();
setInterval(updateTime, 1000);

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
    // 定位成功了，之前（比如信号一开始弱、隔了几秒才锁定）弹出来的失败提示要跟着收起，
    // 不然明明已经定位成功了，页面上还挂着一条"未能获取位置"的红字，容易让人误以为定位没成功
    const alertBox = document.getElementById('locationAlert');
    if (alertBox) alertBox.classList.add('d-none');
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
                status.textContent = '未能获取位置，请检查浏览器定位权限后刷新重试（远程打卡必须要有定位）';
                alertBox.classList.remove('d-none');
            }
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

async function startCamera() {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        showCameraError('当前浏览器不支持摄像头识别，无法使用远程打卡');
        return;
    }
    try {
        const stream = await navigator.mediaDevices.getUserMedia({
            video: { facingMode: 'user', width: { ideal: 480 }, height: { ideal: 480 } },
            audio: false
        });
        cameraVideo.srcObject = stream;
        cameraReady = true;
        document.getElementById('cameraReadyBadge').classList.remove('d-none');
        document.getElementById('cameraAlert').classList.add('d-none');
    } catch (err) {
        showCameraError('未能打开摄像头，请检查浏览器摄像头权限后刷新重试');
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
if (cameraVideo) startCamera();

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
    if (!cameraReady) {
        alert('摄像头还没准备好，请稍候再试，或刷新页面重新授权摄像头权限');
        return;
    }
    // 防连点：提交后立即禁用按钮，避免网络慢时手快连点两下，同时打出两个请求——
    // 每个都会真的调一次收费的阿里云接口，白白多花钱，也会在识别记录里留下容易看着困惑的重复条目
    const btn = document.getElementById('punchBtn');
    if (btn.disabled) return;
    // getUserMedia 成功只代表"拿到了摄像头流"，画面可能还没出来（手机上常见）——截不到画面就不提交
    const photo = captureFrame();
    if (!photo || photo.length < 1000) {
        alert('摄像头还没出画面，请等预览画面出现后再点打卡；如果一直没有画面，请刷新页面并允许摄像头权限');
        return;
    }
    btn.disabled = true;
    document.getElementById('capturedPhotoData').value = photo;
    document.getElementById('punchForm').submit();
}
