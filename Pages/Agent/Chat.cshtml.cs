using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Pages;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Agent;

/// <summary>
/// 智能助手（AGENT）独立页：管理员在这里与助手对话、并确认/拒绝助手生成的"写操作提案"。
/// 权限：ManagePolicy（Admin/Clerk，含分公司受限管理员——对话与工具执行都按操作者范围实时校验）。
/// 安全模型：助手生成的任何写操作都只是"待确认动作"，必须由管理员在本页点【确认执行】才会真正落库。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class ChatModel(
    IAgentService agent,
    IAgentActionService actionService,
    IAgentToolExecutor toolExecutor,
    IAntiforgery antiforgery,
    IOptions<AgentOptions> agentOptions,
    ILogger<ChatModel> logger) : AppPageModel
{
    public List<AgentConversationDto> Conversations { get; set; } = [];
    public List<AgentMessageDto> Messages { get; set; } = [];
    public List<AgentPendingActionDto> PendingActions { get; set; } = [];
    public int CurrentConversationId { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }
    public bool Enabled => agentOptions.Value.Enabled;
    public int TodayTokenUsage { get; set; }
    public int? DailyTokenBudget => agentOptions.Value.DailyTokenBudget;

    private AgentOptions Opt => agentOptions.Value;

    public async Task OnGetAsync(int id = 0)
    {
        var uid = CurrentUserId;
        Conversations = await agent.GetConversationsAsync(uid);
        if (DailyTokenBudget.HasValue)
            TodayTokenUsage = await agent.GetTodayTokenUsageAsync(uid);

        if (id > 0)
        {
            CurrentConversationId = id;
            await LoadConversationAsync(uid, id);
        }
        else if (Conversations.Count > 0)
        {
            // 没指定会话时默认打开最近一个
            CurrentConversationId = Conversations[0].Id;
            await LoadConversationAsync(uid, CurrentConversationId);
        }
    }

    private async Task LoadConversationAsync(int uid, int conversationId)
    {
        CurrentConversationId = conversationId;
        Messages = await agent.GetMessagesAsync(uid, conversationId);
        PendingActions = await actionService.GetActionsAsync(uid, conversationId);
        if (DailyTokenBudget.HasValue)
            TodayTokenUsage = await agent.GetTodayTokenUsageAsync(uid);
    }

    public async Task<IActionResult> OnPostNewAsync()
    {
        var cid = await agent.CreateConversationAsync(CurrentUserId);
        return RedirectToPage(new { id = cid });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        await agent.DeleteConversationAsync(CurrentUserId, id);
        return RedirectToPage();
    }

    [BindProperty] public int SendConversationId { get; set; }
    [BindProperty] public string SendText { get; set; } = string.Empty;

    public async Task<IActionResult> OnPostSendAsync()
    {
        var uid = CurrentUserId;

        if (SendConversationId <= 0)
        {
            ErrorMessage = "请先新建或选择一个会话";
        }
        else if (!AgentRateLimiter.TryConsume(uid, Opt.RateLimitPerMinute, Opt.DailyLimit, out var reason))
        {
            ErrorMessage = reason;
        }
        else
        {
            try
            {
                await agent.SendAsync(uid, SendConversationId, SendText, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                if (!AgentErrorText.IsBusinessMessage(ex)) logger.LogWarning(ex, "智能助手对话失败");
                ErrorMessage = AgentErrorText.ForUser(ex);
            }
        }

        Conversations = await agent.GetConversationsAsync(uid);
        if (SendConversationId > 0)
            await LoadConversationAsync(uid, SendConversationId);
        return Page();
    }

    /// <summary>
    /// 流式对话端点：SSE（text/event-stream）。事件格式：data: {"t":"d","x":"文本块"} / {"t":"err","x":"..."} / {"t":"done"}。
    /// 前端 fetch 边收边渲染；流结束后由前端刷新页面以落库展示（动作卡片等）。
    /// </summary>
    public async Task<IActionResult> OnPostStreamAsync()
    {
        var uid = CurrentUserId;

        // SSE 事件 JSON 统一用小驼峰（t/x），前端按小写解析
        var sseJson = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };

        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.ContentType = "text/event-stream; charset=utf-8";

        async Task SendEventAsync(string json)
        {
            await Response.WriteAsync("data: " + json + "\n\n", HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);
        }

        async Task FailAsync(string message)
        {
            try { await SendEventAsync(System.Text.Json.JsonSerializer.Serialize(new { t = "err", x = message }, sseJson)); }
            catch { /* 客户端已断开 */ }
        }

        try
        {
            if (SendConversationId <= 0)
            {
                await FailAsync("请先新建或选择一个会话");
                return new EmptyResult();
            }
            if (!AgentRateLimiter.TryConsume(uid, Opt.RateLimitPerMinute, Opt.DailyLimit, out var reason))
            {
                await FailAsync(reason);
                return new EmptyResult();
            }

            string? reply = null;
            Exception? sendErr = null;
            try
            {
                reply = await agent.SendStreamingAsync(uid, SendConversationId, SendText, async chunk =>
                {
                    await SendEventAsync(System.Text.Json.JsonSerializer.Serialize(new { t = "d", x = chunk }, sseJson));
                }, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                sendErr = ex;
            }

            if (sendErr is not null)
            {
                if (!AgentErrorText.IsBusinessMessage(sendErr)) logger.LogWarning(sendErr, "智能助手对话失败（流式）");
                await FailAsync(AgentErrorText.ForUser(sendErr));
            }
            else
            {
                await SendEventAsync(System.Text.Json.JsonSerializer.Serialize(new { t = "done", x = reply ?? "" }, sseJson));
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开：静默结束
        }
        return new EmptyResult();
    }

    /// <summary>确认执行一个待确认动作。</summary>
    public async Task<IActionResult> OnPostReviewApproveAsync(int actionId)
        => await ReviewAsync(actionId, approve: true);

    /// <summary>拒绝一个待确认动作。</summary>
    public async Task<IActionResult> OnPostReviewRejectAsync(int actionId)
        => await ReviewAsync(actionId, approve: false);

    /// <summary>撤回一个已执行的动作（按快照还原现场）。</summary>
    public async Task<IActionResult> OnPostReviewUndoAsync(int actionId)
    {
        var uid = CurrentUserId;
        var (ok, message) = await actionService.UndoAsync(uid, actionId);
        if (ok) SuccessMessage = message;
        else ErrorMessage = message;

        Conversations = await agent.GetConversationsAsync(uid);
        var convId = ReviewConversationId > 0 ? ReviewConversationId : CurrentConversationId;
        if (convId > 0)
            await LoadConversationAsync(uid, convId);
        return Page();
    }

    /// <summary>批量确认/拒绝多条待确认动作（完整页勾选后一次提交，逐条调用同一套 ReviewAsync，不额外加原子性保证——某一条失败不影响其余条继续处理）。</summary>
    public async Task<IActionResult> OnPostBatchReviewAsync(List<int> actionIds, bool approve)
    {
        var uid = CurrentUserId;
        var okCount = 0;
        var failMessages = new List<string>();
        foreach (var id in actionIds ?? [])
        {
            var (ok, message) = await actionService.ReviewAsync(uid, id, approve);
            if (ok) okCount++;
            else failMessages.Add($"#{id}：{message}");
        }

        if (failMessages.Count == 0)
            SuccessMessage = $"已{(approve ? "确认执行" : "拒绝")} {okCount} 条动作";
        else
            ErrorMessage = $"成功 {okCount} 条，{failMessages.Count} 条失败：" + string.Join("；", failMessages);

        Conversations = await agent.GetConversationsAsync(uid);
        var convId = ReviewConversationId > 0 ? ReviewConversationId : CurrentConversationId;
        if (convId > 0)
            await LoadConversationAsync(uid, convId);
        return Page();
    }

    /// <summary>悬浮助手初始化数据（GET）：最近会话 + 消息历史 + 待确认动作 + 是否启用。无会话时返回 ConversationId=0。</summary>
    public async Task<JsonResult> OnGetWidgetDataAsync()
    {
        var uid = CurrentUserId;
        var enabled = agentOptions.Value.Enabled;
        var convs = await agent.GetConversationsAsync(uid);
        if (convs.Count == 0)
            return new JsonResult(new { Enabled = enabled, ConversationId = 0, Title = "", Messages = Array.Empty<object>(), Actions = Array.Empty<object>() });

        var conv = convs[0];
        var msgs = await agent.GetMessagesAsync(uid, conv.Id);
        var actions = await actionService.GetActionsAsync(uid, conv.Id);
        return new JsonResult(new
        {
            Enabled = enabled,
            ConversationId = conv.Id,
            Title = conv.Title,
            Messages = msgs.Select(m => new { m.Role, m.Content }),
            Actions = WidgetActionDtos(actions)
        });
    }

    /// <summary>悬浮助手用：确保存在一个会话并返回其 id（POST）。</summary>
    public async Task<JsonResult> OnPostWidgetEnsureAsync()
    {
        var cid = await agent.CreateConversationAsync(CurrentUserId);
        return new JsonResult(new { ConversationId = cid });
    }

    /// <summary>悬浮助手用：取一个当前会话有效的防伪令牌（GET，直接以请求头方式携带，绕开 DOM 解析的脆弱性）。</summary>
    public JsonResult OnGetTokenAsync()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return new JsonResult(new { token = tokens.RequestToken ?? "" });
    }

    /// <summary>悬浮助手用：登录后/打开对话框时的主动播报（今日异常/待审批/待确认登记速览，不经过大模型）。</summary>
    public async Task<JsonResult> OnGetWidgetBriefAsync()
    {
        var text = await toolExecutor.GetQuickBriefAsync(CurrentUserId, HttpContext.RequestAborted);
        return new JsonResult(new { text });
    }

    /// <summary>悬浮助手用：刷新某会话下的待确认动作列表（发送/确认/拒绝/撤回后调用）。</summary>
    public async Task<JsonResult> OnGetWidgetActionsAsync(int conversationId)
    {
        var actions = await actionService.GetActionsAsync(CurrentUserId, conversationId);
        return new JsonResult(new { Actions = WidgetActionDtos(actions) });
    }

    /// <summary>悬浮助手用：在悬浮窗内直接确认/拒绝/撤回一个动作（JSON 返回，不整页刷新）。</summary>
    public async Task<JsonResult> OnPostWidgetReviewAsync(int actionId, int conversationId, string act)
    {
        var uid = CurrentUserId;
        (bool ok, string message) result = act switch
        {
            "approve" => await actionService.ReviewAsync(uid, actionId, approve: true),
            "reject"  => await actionService.ReviewAsync(uid, actionId, approve: false),
            "undo"    => await actionService.UndoAsync(uid, actionId),
            _ => (false, "未知操作")
        };
        var actions = await actionService.GetActionsAsync(uid, conversationId);
        return new JsonResult(new { result.ok, result.message, Actions = WidgetActionDtos(actions) });
    }

    private static object[] WidgetActionDtos(List<AgentPendingActionDto> actions) =>
        actions.Select(a => new
        {
            a.Id, a.ToolNameText, a.SummaryText, a.StatusText, a.IsPending,
            a.HighRisk, a.Undoable, a.CanUndo, a.UndoHint, a.ResultText, a.ErrorText
        }).Cast<object>().ToArray();

    /// <summary>动作所属会话（由动作卡片表单随请求带回，见 Chat.cshtml）。</summary>
    [BindProperty] public int ReviewConversationId { get; set; }

    private async Task<IActionResult> ReviewAsync(int actionId, bool approve)
    {
        var uid = CurrentUserId;
        var (ok, message) = await actionService.ReviewAsync(uid, actionId, approve);
        if (ok) SuccessMessage = message;
        else ErrorMessage = message;

        Conversations = await agent.GetConversationsAsync(uid);
        var convId = ReviewConversationId > 0 ? ReviewConversationId : CurrentConversationId;
        if (convId > 0)
            await LoadConversationAsync(uid, convId);
        return Page();
    }
}
