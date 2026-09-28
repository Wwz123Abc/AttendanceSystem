namespace AttendanceSystem.Services.Interfaces;

/// <summary>
/// AGENT 工具执行器：模型只负责"决定调哪个工具、填什么参数"，
/// 真正的数据访问和权限校验全部在这里按"当前登录管理员"实时执行（复用 IDeptScopeService）。
/// 安全红线（继承系统隔离约定）：
/// - 受限管理员（ScopedDepartmentId 有值）只能查到自己部门 + 下级部门的数据；
/// - 无部门归属的数据（未分配员工/无部门登记/总部共用设备）只对不受限总部管理员可见；
/// - 工具返回给模型的内容一律脱敏（身份证号/完整手机号/住址/照片等永不出现），且限制行数；
/// - 写类工具（*_propose）只生成"待确认动作"（AgentPendingAction），绝不直接改业务数据，
///   真正的执行由 IAgentActionService 在管理员点确认后完成。
/// </summary>
public interface IAgentToolExecutor
{
    /// <summary>全部可用工具的定义（喂给模型，让模型知道能调什么）。</summary>
    IReadOnlyList<AgentToolDefinition> GetDefinitions();

    /// <summary>
    /// 执行一次工具调用。conversationId 供写类工具把"提案"挂到对应会话下。
    /// 返回给模型的文本结果；业务性失败以"错误：…"文本返回（不让模型看到异常堆栈）。
    /// </summary>
    Task<string> ExecuteAsync(int operatorUserId, int conversationId, string toolName, string argsJson, CancellationToken ct);

    /// <summary>
    /// 悬浮窗打开时的主动播报：今日考勤异常/待审批/待确认登记各自的数量（按操作者范围实时统计）。
    /// 不经过大模型，直接查库，快且零 Token 成本；返回一句话摘要，全部为 0 时返回空字符串（不打扰）。
    /// </summary>
    Task<string> GetQuickBriefAsync(int operatorUserId, CancellationToken ct);
}
