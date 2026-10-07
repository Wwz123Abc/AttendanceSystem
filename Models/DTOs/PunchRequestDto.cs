using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

// 「DTO（数据传输对象）」= 专门用来打包一组数据、在网页/接口和程序之间传递的简单类。
// 它不直接对应数据库表，只为某个具体功能服务（比如“打卡请求”“考勤展示”）。
// 本文件放的是和考勤相关的 DTO。

// ── 打卡 ──────────────────────────────────────────────────────────────────────

/// <summary>打卡请求：员工点“打卡”时，网页传给后台的数据。</summary>
public class PunchRequestDto
{
    public PunchType PunchType  { get; set; }   // 上班还是下班
    public double?   Latitude   { get; set; }   // 当前位置纬度（定位打卡用）
    public double?   Longitude  { get; set; }   // 当前位置经度
    public double?   Accuracy   { get; set; }   // 浏览器定位精度半径（米），用于定位校验时的误差容错
    public string?   Address    { get; set; }   // 位置文字地址
    public string?   DeviceInfo { get; set; }   // 设备信息
}
