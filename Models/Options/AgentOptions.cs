namespace AttendanceSystem.Models.Options;

/// <summary>
/// 管理员助手（AGENT）配置：绑定 appsettings.json 的 "Agent" 节。
/// 密钥（ApiKey）不写进 appsettings，从环境变量 AGENT_API_KEY 读取（见 Program.cs 里的 PostConfigure）。
/// </summary>
public class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>总开关：false 时助手页给出"未启用"提示，不调用模型。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>OpenAI 兼容接口地址（DeepSeek 的 /v1 前缀）。</summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>模型 id。若平台没有该 id（如报 model not found），换成平台实际 id（如 deepseek-chat）。</summary>
    public string Model { get; set; } = "deepseek-v4-flash";

    /// <summary>API Key。优先读环境变量 AGENT_API_KEY，配置节里留空即可。</summary>
    public string? ApiKey { get; set; }

    /// <summary>单轮对话里"模型↔工具"最多来回几轮（本阶段还没有工具，保留给二期）。</summary>
    public int MaxRounds { get; set; } = 6;

    /// <summary>单次模型请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>模型回复的最大 token 数（防止一次回答过长烧 token）。</summary>
    public int MaxTokens { get; set; } = 1500;

    /// <summary>温度：管理场景要确定性，取低值。</summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>每个用户每分钟最多发起几次对话请求。</summary>
    public int RateLimitPerMinute { get; set; } = 10;

    /// <summary>每个用户每天最多发起几次对话请求。</summary>
    public int DailyLimit { get; set; } = 100;

    /// <summary>历史里保留最近多少条消息喂给模型（超出截断，控制 token 成本）。</summary>
    public int HistoryMessageCount { get; set; } = 30;

    /// <summary>单个用户每日 token 预算告警线（prompt+completion 合计）；null=不告警。超过后完整页顶部会有提醒，不会拦截使用。</summary>
    public int? DailyTokenBudget { get; set; }
}
