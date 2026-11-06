namespace AttendanceSystem.Models.Exceptions;

/// <summary>
/// 业务校验异常：消息是写给用户看的中文提示（"工号已存在""无权操作该账号"这类），可以原样展示。
/// 继承 <see cref="InvalidOperationException"/>，所以原来 <c>catch (InvalidOperationException)</c> 的地方都照常接得住；
/// 新代码抛业务提示用它，框架/配置类的错误（缺密钥、EF Core 内部错误）仍用原来的类型，不要混在一起。
/// </summary>
public class BusinessException : InvalidOperationException
{
    public BusinessException(string message) : base(message) { }

    /// <summary>带上底层异常（比如"拿不到数据库行锁"），调用方记日志时能看到根因；消息仍然直接给用户看。</summary>
    public BusinessException(string message, Exception inner) : base(message, inner) { }
}
