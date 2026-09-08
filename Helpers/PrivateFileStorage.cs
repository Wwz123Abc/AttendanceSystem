namespace AttendanceSystem.Helpers;

/// <summary>
/// 身份证照/人脸照/审批附件/考勤机抓拍照片这类敏感文件的物理存储位置——统一放在 wwwroot 之外
/// （content root 下的 PrivateUploads 目录），不会被 UseStaticFiles 匿名下发；要访问必须登录后
/// 走 Controllers/PrivateFilesController.cs，按文件类型再判一次权限。
/// 目录内部结构（{UploadPath}/idcards/... 等）跟以前放 wwwroot 时完全一样，数据库里存的 URL
/// 字符串（IdCardPhotoUrl 等）也完全不用改，只是物理落盘位置和读取方式变了。
/// </summary>
public static class PrivateFileStorage
{
    public static string GetRoot(IWebHostEnvironment env) => Path.Combine(env.ContentRootPath, "PrivateUploads");
}
