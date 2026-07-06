using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Controllers;

/// <summary>钉钉对接接口：从钉钉拉打卡、按工号映射、导入员工。仅管理员/文员。</summary>
[Authorize(Policy = "ManagePolicy")]
[Route("api/[controller]")]
[ApiController]
public class DingTalkController(
    IDingTalkSyncService syncService,
    ILogger<DingTalkController> logger) : ApiControllerBase
{
    /// <summary>手动同步：拉取 [from, to] 这段时间的钉钉打卡结果（不传则默认昨天~今天）。</summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] DingTalkSyncRequestDto? req, CancellationToken ct)
    {
        var from = req?.From ?? DateTime.Today.AddDays(-1);
        var to   = req?.To   ?? DateTime.Today.AddDays(1).AddSeconds(-1);

        if (to < from)
            return BadRequest(new { Success = false, Message = "结束时间不能早于开始时间" });

        return await RunAsync("同步打卡", () => syncService.SyncAsync(from, to, ct));
    }

    /// <summary>按工号自动回填员工的钉钉 userid 映射（overwrite=true 覆盖已有映射）。</summary>
    [HttpPost("auto-map")]
    public Task<IActionResult> AutoMap([FromQuery] bool overwrite = false, CancellationToken ct = default)
        => RunAsync("按工号映射", () => syncService.AutoMapByJobNumberAsync(overwrite, ct));

    /// <summary>从钉钉通讯录整批导入员工（自动建好 userid 映射，无需再手动映射）。</summary>
    [HttpPost("import-employees")]
    public Task<IActionResult> ImportEmployees(CancellationToken ct)
        => RunAsync("从钉钉导入员工", () => syncService.ImportEmployeesAsync(ct));

    /// <summary>
    /// 统一执行钉钉操作并兜底异常：把钉钉接口的真实错误（凭证/权限/IP 白名单等）
    /// 以 JSON 形式返回前端，而不是直接抛 500，方便用户看懂哪里出了问题。
    /// </summary>
    private async Task<IActionResult> RunAsync<T>(string action, Func<Task<T>> op)
    {
        try
        {
            return Ok(await op());   // 正常执行，返回结果
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "钉钉操作「{Action}」失败", action);
            return Ok(new { Success = false, Message = $"{action}失败：{ex.Message}" });
        }
    }
}
