namespace AttendanceSystem.Services.Interfaces;

/// <summary>暴露给模型的工具定义（OpenAI tools 数组里的一项）。</summary>
public record AgentToolDefinition(string Name, string Description, string ParametersJson);
