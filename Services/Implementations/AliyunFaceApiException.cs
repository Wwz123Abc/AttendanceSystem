using AlibabaCloud.SDK.Facebody20191230.Models;
using AlibabaCloud.TeaUtil.Models;
using Microsoft.Extensions.Options;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;
using SixLabors.ImageSharp;
using Tea;

namespace AttendanceSystem.Services.Implementations;

/// <summary>阿里云人脸识别接口调用失败（网络/签名/服务端错误），消息已经是给管理员/日志看的中文说明。</summary>
public class AliyunFaceApiException(string message) : Exception(message);
