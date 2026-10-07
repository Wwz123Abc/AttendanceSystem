namespace AttendanceSystem.Services.Interfaces;

/// <summary>模型请求调用某个工具。</summary>
public record AgentToolCall(string Id, string Name, string ArgumentsJson);
