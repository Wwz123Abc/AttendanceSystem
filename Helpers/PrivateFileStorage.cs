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

    /// <summary>
    /// 删掉人脸参考照的文件：留档版，加上同目录下比对用的 "_verify.jpg" 瘦身版。只删 PrivateUploads 目录里的文件
    /// （路径穿越直接忽略）；文件不存在不算错。删除失败只记日志、不抛异常——清理文件不能盖过调用方本来的结果。
    /// </summary>
    public static void DeleteFaceReferenceFiles(IWebHostEnvironment env, string? url, ILogger logger)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            var root = Path.GetFullPath(GetRoot(env));
            var path = Path.GetFullPath(Path.Combine(root, url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(path)) File.Delete(path);
            var verifyPath = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}_verify.jpg");
            if (File.Exists(verifyPath)) File.Delete(verifyPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "清理人脸参考照文件失败：{Url}", url);
        }
    }
}
