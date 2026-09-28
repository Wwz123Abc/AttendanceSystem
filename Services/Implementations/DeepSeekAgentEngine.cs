using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// DeepSeek 引擎实现：走 OpenAI 兼容的 /chat/completions 接口，支持 function calling（tools）。
/// 密钥只从 AgentOptions.ApiKey 读（Program.cs 里已把它接到环境变量 AGENT_API_KEY），
/// 绝不落日志；任何异常信息在抛出前都要确认不含密钥原文。
/// </summary>
public class DeepSeekAgentEngine(
    IHttpClientFactory httpClientFactory,
    IOptions<AgentOptions> options) : IAgentEngine
{
    private readonly AgentOptions _opt = options.Value;

    public async Task<AgentEngineResult> CompleteAsync(
        IReadOnlyList<AgentChatMessage> messages,
        IReadOnlyList<AgentToolDefinition>? tools,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new InvalidOperationException("AGENT 未配置 API Key（环境变量 AGENT_API_KEY）");

        var client = httpClientFactory.CreateClient("agent");
        var baseUrl = _opt.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/chat/completions";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);
        req.Content = new StringContent(BuildPayload(messages, tools, stream: false).ToJsonString(), Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opt.TimeoutSeconds));

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"模型调用超时（>{_opt.TimeoutSeconds} 秒），请稍后重试");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"无法连接模型服务：{ex.Message}");
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // 只取错误 message，不含任何密钥信息
            var msg = ExtractError(json);
            throw new InvalidOperationException($"模型调用失败(HTTP {(int)resp.StatusCode})：{msg}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? content = null;
        List<AgentToolCall>? toolCalls = null;

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    content = c.GetString();

                if (message.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    toolCalls = [];
                    var i = 0;
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var id   = "";
                        var name = "";
                        var args = "{}";
                        if (tc.TryGetProperty("id", out var idEl)) id = idEl.GetString() ?? "";
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
                            if (fn.TryGetProperty("arguments", out var a)) args = a.GetString() ?? "{}";
                        }
                        if (string.IsNullOrEmpty(id)) id = $"call_{i}";
                        toolCalls.Add(new AgentToolCall(id, name, args));
                        i++;
                    }
                }
            }
        }

        int? promptTokens = null, completionTokens = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ct2)) completionTokens = ct2.GetInt32();
        }

        var model = root.TryGetProperty("model", out var md) ? md.GetString() : null;

        return new AgentEngineResult(content, toolCalls, promptTokens, completionTokens, model ?? _opt.Model);
    }

    /// <summary>组装 OpenAI 兼容请求体（tools/stream 按需携带）。</summary>
    private JsonObject BuildPayload(IReadOnlyList<AgentChatMessage> messages, IReadOnlyList<AgentToolDefinition>? tools, bool stream)
    {
        var payload = new JsonObject
        {
            ["model"]       = _opt.Model,
            ["temperature"] = _opt.Temperature,
            ["max_tokens"]  = _opt.MaxTokens,
            ["stream"]      = stream,
            ["messages"]    = new JsonArray(messages.Select(MsgToJson).ToArray())
        };
        // 流式默认不带 usage；OpenAI 兼容接口支持这个开关，让最后一个 chunk 补发 usage，
        // 否则 SendStreamingAsync 存下来的 Token 用量永远是 0，预算告警形同虚设。
        if (stream)
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };

        if (tools is { Count: > 0 })
        {
            payload["tools"] = new JsonArray(tools.Select(t =>
                new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"]        = t.Name,
                        ["description"] = t.Description,
                        ["parameters"]  = JsonNode.Parse(t.ParametersJson)
                    }
                }).ToArray());
        }
        return payload;
    }

    /// <summary>
    /// 流式对话：stream=true，按行解析 SSE（data: {...}），content 逐块回调 onDelta；
    /// 若带 tool_calls，按 index 累积 id/name/arguments，结束时返回完整结果。
    /// </summary>
    public async Task<AgentEngineResult> CompleteStreamingAsync(
        IReadOnlyList<AgentChatMessage> messages,
        IReadOnlyList<AgentToolDefinition>? tools,
        Func<string, Task>? onDelta,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new InvalidOperationException("AGENT 未配置 API Key（环境变量 AGENT_API_KEY）");

        var client = httpClientFactory.CreateClient("agent");
        var url = $"{_opt.BaseUrl.TrimEnd('/')}/chat/completions";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);
        req.Content = new StringContent(BuildPayload(messages, tools, stream: true).ToJsonString(), Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opt.TimeoutSeconds * 2));   // 流式整体给更长预算

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"模型响应超时，请稍后重试");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"无法连接模型服务：{ex.Message}");
        }

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"模型调用失败(HTTP {(int)resp.StatusCode})：{ExtractError(errBody)}");
        }

        var content = new StringBuilder();
        // index → (id, name, arguments)
        var toolsByIndex = new Dictionary<int, (string Id, StringBuilder Name, StringBuilder Args)>();
        string? modelName = null;
        int? streamPromptTokens = null, streamCompletionTokens = null;

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            cts.Token.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("model", out var md) && md.ValueKind == JsonValueKind.String)
                    modelName ??= md.GetString();

                // include_usage=true 时最后一个 chunk 是 choices:[] + usage:{...}，要在下面的 choices 空判断之前接住
                if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                {
                    if (usageEl.TryGetProperty("prompt_tokens", out var pt)) streamPromptTokens = pt.GetInt32();
                    if (usageEl.TryGetProperty("completion_tokens", out var ct3)) streamCompletionTokens = ct3.GetInt32();
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;
                if (!choices[0].TryGetProperty("delta", out var delta)) continue;

                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var chunk = c.GetString();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        content.Append(chunk);
                        if (onDelta is not null) await onDelta(chunk);
                    }
                }

                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        int idx = 0;
                        if (tc.TryGetProperty("index", out var ixEl) && ixEl.TryGetInt32(out var ixVal)) idx = ixVal;

                        if (!toolsByIndex.TryGetValue(idx, out var acc))
                        {
                            acc = (string.Empty, new StringBuilder(), new StringBuilder());
                            toolsByIndex[idx] = acc;
                        }
                        if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                            toolsByIndex[idx] = acc with { Id = idEl.GetString() ?? "" };

                        if (tc.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                                toolsByIndex[idx].Name.Append(n.GetString());
                            if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                                toolsByIndex[idx].Args.Append(a.GetString());
                        }
                    }
                }
            }
            catch (JsonException) { /* 忽略无法解析的碎块 */ }
        }

        List<AgentToolCall>? toolCalls = null;
        if (toolsByIndex.Count > 0)
        {
            toolCalls = [];
            foreach (var (_, v) in toolsByIndex.OrderBy(kv => kv.Key))
            {
                var name = v.Name.ToString();
                var args = v.Args.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (string.IsNullOrWhiteSpace(args)) args = "{}";
                toolCalls.Add(new AgentToolCall(
                    string.IsNullOrEmpty(v.Id) ? $"call_{v.Name}" : v.Id,
                    name, args));
            }
            if (toolCalls.Count == 0) toolCalls = null;
        }

        var text = content.ToString();
        return new AgentEngineResult(
            string.IsNullOrWhiteSpace(text) ? null : text,
            toolCalls,
            streamPromptTokens, streamCompletionTokens,
            modelName ?? _opt.Model);
    }

    /// <summary>把一条编排层消息转成 OpenAI 兼容的 JSON 对象。</summary>
    private static JsonObject MsgToJson(AgentChatMessage m)
    {
        var o = new JsonObject
        {
            ["role"]    = m.Role,
            ["content"] = m.Content   // null 也显式写出，assistant 发工具请求时 content 允许为 null
        };

        if (m.ToolCalls is { Count: > 0 })
        {
            o["tool_calls"] = new JsonArray(m.ToolCalls.Select(tc =>
                new JsonObject
                {
                    ["id"]   = tc.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"]      = tc.Name,
                        ["arguments"] = tc.ArgumentsJson
                    }
                }).ToArray());
        }

        if (m.ToolCallId is not null)
            o["tool_call_id"] = m.ToolCallId;

        return o;
    }

    /// <summary>从错误响应里挖出一句能看懂的原因（如 model not found / 余额不足）。</summary>
    private static string ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.TryGetProperty("message", out var m)) return m.GetString() ?? "未知错误";
                return err.ToString();
            }
        }
        catch { /* 非 JSON，走兜底 */ }
        return json.Length > 300 ? json[..300] + "…" : json;
    }
}
