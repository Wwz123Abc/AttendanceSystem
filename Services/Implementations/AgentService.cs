using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Helpers;

namespace AttendanceSystem.Services.Implementations;

/// <inheritdoc cref="IAgentService"/>
public class AgentService(
    AttendanceDbContext db,
    IAgentEngine engine,
    IAgentToolExecutor toolExecutor,
    IOptions<AgentOptions> options,
    ILogger<AgentService> logger,
    TimeProvider? timeProvider = null) : IAgentService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;


    private readonly AgentOptions _opt = options.Value;

    // 系统提示词：身份 + 边界。刻意不放任何真实业务数据/人名；
    // "当前操作者可见范围"由工具层在执行时实时校验并只返回范围内的数据。
    private const string SystemPrompt =
        "你是考勤管理系统的管理员助手。你的职责是帮助登录系统的管理员完成考勤相关工作，" +
        "包括：查询员工与考勤情况、解释报表口径、协助处理异常考勤与登记审核。\n" +
        "行为准则：\n" +
        "1. 只回答与考勤系统管理相关的问题；无关话题礼貌拒绝。\n" +
        "2. 查数据一律调用系统提供的查询工具，以工具返回的内容为准，绝不编造数字；工具返回为空就如实说没有。\n" +
        "3. 工具会按当前登录者的权限自动限制范围（分公司管理员只能查到本公司），回答时不要声称看到过范围外的数据。\n" +
        "4. 涉及删除/拉黑/改密码/改管理范围等敏感操作的，明确说明需要在界面里由本人确认执行，本助手不代执行。\n" +
        "5. 工具返回的内容已脱敏（手机号打码等），不要向用户索要或拼凑身份证号等敏感信息。\n" +
        "6. 回答简洁、用中文，能分点就分点。\n" +
        "7. 带 _propose 后缀的工具（如 punch_adjust_propose）是\"提案式写操作\"：调用后会生成一条待确认动作，【不会直接执行】。你应告诉管理员\"已生成待确认动作，请点确认执行\"（悬浮窗和完整页都能直接点），不要声称已经完成修改，也不要说必须去某个具体页面。\n" +
        "8. 如果管理员一次要对多名员工做同一个停用/启用操作，优先调用 employee_batch_toggle_propose 一次性生成一条批量待确认动作，不要为每个人分别生成多条，减少确认次数。\n" +
        "9. 涉及部门 id（deptId）或考勤组 id（groupId）的工具（如 employee_create_propose）不要凭空猜数字或用名字代替，先调用 department_list / attendance_group_list 查到真实 id 再用。\n" +
        "10. 员工本人不方便操作系统、由管理员代为提交请假/加班/出差申请时，用 approval_submit_on_behalf_propose；提交后仍会进入正常审批流程给该员工的审批人处理，不是直接生效，回答时要讲清楚这一点。";

    /// <summary>
    /// 用于兜底识别"模型嘴上说完成了但这轮其实没调用任何 _propose 工具"的情况（已知的模型遵从性波动）。
    /// 命中即在回复末尾追加一句提醒，避免管理员误以为操作已经生效。
    /// </summary>
    /// 实测发现：模型偶尔会在没有真正调用任何 _propose 工具的情况下，直接编出"已生成待确认动作 #N"这类
    /// 官方措辞（跟真实工具返回的文案几乎一样），照抄历史消息里出现过的格式——这类必须一并识别，
    /// 不能因为它是"正确说法"就放过，否则管理员会看到一个压根不存在的动作编号，误以为真生成了。
    private static readonly string[] UnconfirmedActionClaimPhrases =
        ["已经修改", "已修改", "已经删除", "已删除", "已经调整", "已调整好", "已经处理完成", "操作已完成", "已经批准", "已经拒绝", "已经重置", "已经停用", "已经启用", "已经拉黑",
         "已生成待确认动作", "已提交", "已发布", "已撤下", "已建档"];

    private static readonly string[] WeekdayNames = ["日", "一", "二", "三", "四", "五", "六"];

    /// <summary>
    /// 系统提示词 + 当前日期。模型本身不知道"今天"是哪天（历史训练数据没有实时时钟），
    /// 每轮都把服务器当前日期/星期塞进去，避免每次问"今天/本周"相关问题都要先反问用户日期。
    /// </summary>
    private string BuildSystemPrompt()
    {
        var now = clock.LocalNow();
        return SystemPrompt + $"\n当前日期：{now:yyyy-MM-dd}（星期{WeekdayNames[(int)now.DayOfWeek]}）。涉及\"今天/本周/本月\"等相对日期的提问，直接按这个日期换算，不要反问管理员今天是几号。";
    }

    private static string AppendActionClaimGuardIfNeeded(string replyText, bool proposedThisTurn)
    {
        if (proposedThisTurn) return replyText;
        if (!UnconfirmedActionClaimPhrases.Any(p => replyText.Contains(p))) return replyText;
        return replyText + "\n\n⚠️ 提醒：这轮对话没有检测到实际生成的待确认动作，以上如果涉及数据修改，请换个说法让我重新生成一次，或到页面上手动操作，避免误以为已经生效。";
    }

    public async Task<List<AgentConversationDto>> GetConversationsAsync(int userId)
    {
        var convs = await db.AgentConversations
            .Where(c => c.UserId == userId && c.IsActive)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new AgentConversationDto
            {
                Id          = c.Id,
                Title       = c.Title ?? "新对话",
                UpdatedAtText = c.UpdatedAt.ToString("MM-dd HH:mm"),
                MessageCount = db.AgentMessages.Count(m => m.ConversationId == c.Id)
            })
            .ToListAsync();

        // 每个会话的累计 Token 用量（有记录的才算，用于费用估算）
        var convIds = convs.Select(c => c.Id).ToList();
        if (convIds.Count > 0)
        {
            var tokens = await db.AgentMessages
                .Where(m => convIds.Contains(m.ConversationId) && (m.PromptTokens.HasValue || m.CompletionTokens.HasValue))
                .GroupBy(m => m.ConversationId)
                .Select(g => new { ConversationId = g.Key, Tokens = g.Sum(m => (m.PromptTokens ?? 0) + (m.CompletionTokens ?? 0)) })
                .ToDictionaryAsync(x => x.ConversationId, x => x.Tokens);
            foreach (var c in convs)
                c.TokenTotal = tokens.GetValueOrDefault(c.Id);
        }
        return convs;
    }

    /// <inheritdoc/>
    public async Task<int> GetTodayTokenUsageAsync(int userId)
    {
        var todayStart = clock.LocalNow().Date;
        return await db.AgentMessages
            .Where(m => m.CreatedAt >= todayStart && m.Conversation!.UserId == userId)
            .SumAsync(m => (m.PromptTokens ?? 0) + (m.CompletionTokens ?? 0));
    }

    public async Task<int> CreateConversationAsync(int userId)
    {
        var conv = new AgentConversation
        {
            UserId    = userId,
            Title     = null,
            IsActive  = true,
            CreatedAt = clock.LocalNow(),
            UpdatedAt = clock.LocalNow()
        };
        db.AgentConversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    public async Task<bool> DeleteConversationAsync(int userId, int conversationId)
    {
        var conv = await db.AgentConversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId && c.IsActive);
        if (conv is null) return false;   // 不存在或不是本人的会话，一律当"没有"处理（不泄露存在性）

        conv.IsActive  = false;
        conv.UpdatedAt = clock.LocalNow();
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<AgentMessageDto>> GetMessagesAsync(int userId, int conversationId)
    {
        // 归属校验：不是本人会话 → 空列表（不泄露他人会话存在）
        var owned = await db.AgentConversations
            .AnyAsync(c => c.Id == conversationId && c.UserId == userId && c.IsActive);
        if (!owned) return [];

        return await db.AgentMessages
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Id)
            .Select(m => new AgentMessageDto
            {
                Id            = m.Id,
                Role          = m.Role,
                Content       = m.Content,
                CreatedAtText = m.CreatedAt.ToString("MM-dd HH:mm"),
                ToolTraceText = m.ToolTraceText
            })
            .ToListAsync();
    }

    public async Task<string> SendAsync(int userId, int conversationId, string text, CancellationToken ct)
    {
        if (!_opt.Enabled)
            throw new BusinessException("智能助手功能未启用，请联系系统管理员在配置中开启");

        if (string.IsNullOrWhiteSpace(text))
            throw new BusinessException("请输入内容");
        if (text.Length > 4000)
            throw new BusinessException("单次提问不能超过 4000 字");

        // 归属校验
        var conv = await db.AgentConversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId && c.IsActive, ct);
        if (conv is null)
            throw new BusinessException("会话不存在或已被删除");

        var now = clock.LocalNow();

        // 第一次提问时用提问开头生成会话标题
        if (string.IsNullOrWhiteSpace(conv.Title))
        {
            conv.Title = text.Trim().Length <= 20 ? text.Trim() : text.Trim()[..20] + "…";
            conv.UpdatedAt = now;
        }

        // 1) 存提问
        db.AgentMessages.Add(new AgentMessage
        {
            ConversationId = conversationId,
            Role           = "user",
            Content        = text.Trim(),
            CreatedAt      = now
        });
        await db.SaveChangesAsync(ct);

        // 2) 取历史（最近 N 条 user/assistant，做上下文窗口裁剪）
        var history = await db.AgentMessages
            .Where(m => m.ConversationId == conversationId &&
                        (m.Role == "user" || m.Role == "assistant"))
            .OrderByDescending(m => m.Id)
            .Take(_opt.HistoryMessageCount)
            .ToListAsync(ct);
        history.Reverse();

        var messages = new List<AgentChatMessage> { new("system", BuildSystemPrompt()) };
        messages.AddRange(history.Select(m => new AgentChatMessage(m.Role, m.Content)));

        // 3) 模型 ↔ 工具循环：模型可先请求工具，把结果回填后再继续，直到给出文字回答或轮数耗尽
        var defs = toolExecutor.GetDefinitions();
        string? finalContent = null;
        int? promptTokens = 0, completionTokens = 0;
        var modelName = (string?)null;
        var usedRounds = 0;
        var proposedThisTurn = false;
        var toolTrace = new List<string>();

        for (var round = 0; round < _opt.MaxRounds; round++)
        {
            usedRounds = round + 1;
            var result = await engine.CompleteAsync(messages, defs, ct);
            promptTokens      += result.PromptTokens      ?? 0;
            completionTokens  += result.CompletionTokens  ?? 0;
            modelName          = result.Model;

            if (result.ToolCalls is { Count: > 0 } calls)
            {
                // 把模型这次的"工具请求"原样回显（OpenAI 协议要求 assistant 消息带 tool_calls）
                messages.Add(new AgentChatMessage("assistant", result.Content, calls));
                logger.LogInformation("AGENT 会话 {Conv} 第 {Round} 轮：模型请求 {N} 个工具调用", conversationId, round + 1, calls.Count);
                if (calls.Any(c => c.Name.EndsWith("_propose"))) proposedThisTurn = true;

                foreach (var call in calls)
                {
                    var argPreview = call.ArgumentsJson.Length > 200 ? call.ArgumentsJson[..200] + "…" : call.ArgumentsJson;
                    logger.LogInformation("AGENT 会话 {Conv} 工具 {Tool} 参数 {Args}", conversationId, call.Name, argPreview);
                    toolTrace.Add($"{call.Name}({(call.ArgumentsJson.Length > 60 ? call.ArgumentsJson[..60] + "…" : call.ArgumentsJson)})");
                    var toolText = await toolExecutor.ExecuteAsync(userId, conversationId, call.Name, call.ArgumentsJson, ct);
                    var toolPreview = toolText.Replace("\n", " ").Length > 160 ? toolText.Replace("\n", " ")[..160] + "…" : toolText.Replace("\n", " ");
                    logger.LogInformation("AGENT 会话 {Conv} 工具 {Tool} 返回：{Result}", conversationId, call.Name, toolPreview);
                    messages.Add(new AgentChatMessage("tool", toolText, ToolCallId: call.Id));
                }
                continue;   // 继续下一轮，让模型基于工具结果组织回答
            }

            finalContent = result.Content;
            logger.LogInformation("AGENT 会话 {Conv} 第 {Round} 轮结束：内容长度 {Len}（null={IsNull}）", conversationId, round + 1, result.Content?.Length ?? 0, result.Content is null);
            break;
        }

        string replyText;
        if (!string.IsNullOrWhiteSpace(finalContent))
            replyText = finalContent.Trim();
        else if (usedRounds >= _opt.MaxRounds)
            replyText = "（这轮查询的工具步骤较多，还没能整理出最终回答；请把问题问得再具体一点，或分几次问。）";
        else
            replyText = "（模型没有返回内容，请换个问法再试一次）";
        replyText = AppendActionClaimGuardIfNeeded(replyText, proposedThisTurn);

        // 4) 存回复 + 更新会话时间
        db.AgentMessages.Add(new AgentMessage
        {
            ConversationId    = conversationId,
            Role              = "assistant",
            Content           = replyText,
            ModelName         = modelName,
            PromptTokens      = promptTokens,
            CompletionTokens  = completionTokens,
            ToolTraceText     = toolTrace.Count > 0 ? string.Join(" → ", toolTrace) : null,
            CreatedAt         = clock.LocalNow()
        });
        conv.UpdatedAt = clock.LocalNow();
        await db.SaveChangesAsync(ct);

        // 5) 审计：Token 用量进日志（不含对话内容，避免把管理员提问原文刷进日志）
        logger.LogInformation(
            "AGENT 会话 {ConversationId} 用户 {UserId} 完成一轮（{Rounds} 次模型调用）：模型 {Model}，prompt={Prompt}，completion={Completion}",
            conversationId, userId, usedRounds, modelName, promptTokens, completionTokens);

        return replyText;
    }

    /// <inheritdoc/>
    public async Task<string> SendStreamingAsync(int userId, int conversationId, string text, Func<string, Task> onDelta, CancellationToken ct)
    {
        if (!_opt.Enabled) throw new BusinessException("智能助手功能未启用，请联系系统管理员在配置中开启");
        if (string.IsNullOrWhiteSpace(text)) throw new BusinessException("请输入内容");
        if (text.Length > 4000) throw new BusinessException("单次提问不能超过 4000 字");

        var conv = await db.AgentConversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId && c.IsActive, ct);
        if (conv is null) throw new BusinessException("会话不存在或已被删除");

        var now = clock.LocalNow();
        if (string.IsNullOrWhiteSpace(conv.Title))
        {
            conv.Title = text.Trim().Length <= 20 ? text.Trim() : text.Trim()[..20] + "…";
            conv.UpdatedAt = now;
        }

        db.AgentMessages.Add(new AgentMessage
        {
            ConversationId = conversationId, Role = "user", Content = text.Trim(), CreatedAt = now
        });
        await db.SaveChangesAsync(ct);

        var history = await db.AgentMessages
            .Where(m => m.ConversationId == conversationId && (m.Role == "user" || m.Role == "assistant"))
            .OrderByDescending(m => m.Id).Take(_opt.HistoryMessageCount).ToListAsync(ct);
        history.Reverse();

        var messages = new List<AgentChatMessage> { new("system", BuildSystemPrompt()) };
        messages.AddRange(history.Select(m => new AgentChatMessage(m.Role, m.Content)));

        var defs = toolExecutor.GetDefinitions();
        string? finalContent = null;
        int? promptTokens = 0, completionTokens = 0;
        var modelName = (string?)null;
        var usedRounds = 0;
        var proposedThisTurn = false;
        var toolTrace = new List<string>();

        for (var round = 0; round < _opt.MaxRounds; round++)
        {
            usedRounds = round + 1;
            // 流式：逐块回传；无 onDelta 时退化为整段返回
            var result = await engine.CompleteStreamingAsync(messages, defs, onDelta, ct);
            promptTokens     += result.PromptTokens     ?? 0;
            completionTokens += result.CompletionTokens ?? 0;
            modelName = result.Model;

            if (result.ToolCalls is { Count: > 0 } calls)
            {
                messages.Add(new AgentChatMessage("assistant", result.Content, calls));
                logger.LogInformation("AGENT 会话 {Conv} 第 {Round} 轮：模型请求 {N} 个工具调用", conversationId, round + 1, calls.Count);
                if (calls.Any(c => c.Name.EndsWith("_propose"))) proposedThisTurn = true;
                foreach (var call in calls)
                {
                    toolTrace.Add($"{call.Name}({(call.ArgumentsJson.Length > 60 ? call.ArgumentsJson[..60] + "…" : call.ArgumentsJson)})");
                    var toolText = await toolExecutor.ExecuteAsync(userId, conversationId, call.Name, call.ArgumentsJson, ct);
                    messages.Add(new AgentChatMessage("tool", toolText, ToolCallId: call.Id));
                }
                continue;
            }

            finalContent = result.Content;
            break;
        }

        string replyText;
        if (!string.IsNullOrWhiteSpace(finalContent)) replyText = finalContent.Trim();
        else if (usedRounds >= _opt.MaxRounds)
            replyText = "（这轮查询的工具步骤较多，还没能整理出最终回答；请把问题问得再具体一点，或分几次问。）";
        else
            replyText = "（模型没有返回内容，请换个问法再试一次）";

        var guardedReplyText = AppendActionClaimGuardIfNeeded(replyText, proposedThisTurn);
        if (guardedReplyText != replyText)
            await onDelta(guardedReplyText[replyText.Length..]);   // 兜底提醒也走流式补发，保证悬浮窗/完整页当场就能看到
        replyText = guardedReplyText;

        db.AgentMessages.Add(new AgentMessage
        {
            ConversationId = conversationId, Role = "assistant", Content = replyText,
            ModelName = modelName, PromptTokens = promptTokens, CompletionTokens = completionTokens,
            ToolTraceText = toolTrace.Count > 0 ? string.Join(" → ", toolTrace) : null,
            CreatedAt = clock.LocalNow()
        });
        conv.UpdatedAt = clock.LocalNow();
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "AGENT 会话 {ConversationId} 用户 {UserId} 完成一轮流式（{Rounds} 次模型调用）：模型 {Model}，prompt={Prompt}，completion={Completion}",
            conversationId, userId, usedRounds, modelName, promptTokens, completionTokens);
        return replyText;
    }
}
