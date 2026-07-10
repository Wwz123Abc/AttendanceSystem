namespace AttendanceSystem.Models.Options;

/// <summary>
/// 高德地图 Web端(JS API) 配置（绑定 appsettings.json 的 "AMap" 节）。
/// 用途：在"考勤组管理"页配置打卡地点时，可以搜索地址或直接在地图上选点，
/// 系统自动换算出精确的经纬度，不用管理员自己去查、手动输入经纬度数字。
/// 密钥申请地址：https://lbs.amap.com（控制台 → 应用管理 → 创建应用 → 添加 Key，
/// 服务平台选"Web端(JS API)"；2021 年之后申请的 Key 还需要配套勾选生成"安全密钥"）。
/// 两个值都留空时，"考勤组管理"页的地图选点按钮会自动隐藏，仍可以手动输入经纬度作为兜底。
/// </summary>
public class AMapOptions
{
    public const string SectionName = "AMap";

    /// <summary>Web端(JS API) Key。</summary>
    public string JsApiKey { get; set; } = "";

    /// <summary>安全密钥（jscode，和 Key 一起在高德开放平台申请）。</summary>
    public string SecurityJsCode { get; set; } = "";
}
