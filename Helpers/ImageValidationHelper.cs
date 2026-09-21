namespace AttendanceSystem.Helpers;

/// <summary>
/// 上传图片的文件头（魔数）校验：只看扩展名挡不住"把其他类型的文件伪装成图片上传"
/// （改个后缀名就能绕过）。身份证照片、人脸参考照片这类敏感文件的上传入口都应该调这个，
/// 不要各自维护一份（发现于 2026-09-21 数据核查：员工自助登记、考勤机抓拍照片两处已经在用，
/// 管理员"新增/编辑员工"的身份证照片上传漏了，这里抽成共享方法，三处统一）。
/// </summary>
public static class ImageValidationHelper
{
    /// <summary>按文件头魔数校验内容是不是真的是对应格式的图片：JPEG=FF D8 FF；PNG=89 50 4E 47 0D 0A 1A 0A；
    /// WEBP=开头 "RIFF"、第 8-11 字节 "WEBP"。</summary>
    public static bool IsValidImageHeader(string ext, byte[] header) => ext switch
    {
        ".jpg" or ".jpeg" => header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
        ".png" => header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                                      && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A,
        ".webp" => header.Length >= 12 && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
                                        && header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P',
        _ => false
    };
}
