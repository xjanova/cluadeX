using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

public abstract class ApiProviderBase : IAiProvider
{
    protected readonly SettingsService _settingsService;
    protected readonly HttpClient _httpClient;

    public abstract string ProviderId { get; }
    public abstract string DisplayName { get; }
    public bool IsReady { get; protected set; }
    public bool IsLoading { get; protected set; }
    public string StatusMessage { get; protected set; } = "Not configured";

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    protected ApiProviderBase(SettingsService settingsService)
    {
        _settingsService = settingsService;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
            EnableMultipleHttp2Connections = true,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
    }

    protected ProviderConfig GetConfig()
    {
        if (_settingsService.Settings.ProviderConfigs.TryGetValue(ProviderId, out var config))
            return config;
        return new ProviderConfig();
    }

    protected void SetStatus(string message)
    {
        StatusMessage = message;
        OnStatusChanged?.Invoke(message);
    }

    protected void SetLoading(bool loading)
    {
        IsLoading = loading;
        OnLoadingChanged?.Invoke(loading);
    }

    protected void RaiseError(string message)
    {
        OnError?.Invoke(message);
    }

    // Simple record to avoid reflection on anonymous types
    protected record ChatMsg(string role, string content);

    protected List<object> BuildChatMessages(List<ChatMessage> history, string userMessage, string? systemPrompt,
        string systemRole = "system", string userRole = "user", string assistantRole = "assistant")
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new ChatMsg(systemRole, systemPrompt));

        // Build history ensuring strict user/assistant alternation.
        // ToolAction and CodeExecution are merged into adjacent messages
        // to avoid consecutive same-role messages.
        string? lastRole = !string.IsNullOrWhiteSpace(systemPrompt) ? systemRole : null;

        foreach (var msg in history)
        {
            if (string.IsNullOrWhiteSpace(msg.Content)) continue;

            string role;
            string content;

            switch (msg.Role)
            {
                case MessageRole.User:
                    role = userRole;
                    content = msg.Content;
                    break;
                case MessageRole.Assistant:
                    role = assistantRole;
                    content = msg.Content;
                    break;
                case MessageRole.CodeExecution:
                    // Merge into preceding assistant message, or treat as assistant context
                    role = assistantRole;
                    content = $"[Code Execution Result]\n{msg.Content}";
                    break;
                case MessageRole.ToolAction:
                    // Merge into preceding assistant message
                    role = assistantRole;
                    content = $"[Tool: {msg.ToolName}] {msg.Content}";
                    break;
                case MessageRole.System:
                    // Skip mid-conversation system messages (already handled via systemPrompt)
                    continue;
                default:
                    continue;
            }

            // Merge consecutive same-role messages
            if (role == lastRole && messages.Count > 0)
            {
                var prev = (ChatMsg)messages[^1];
                string prevContent = prev.content;
                messages[^1] = new ChatMsg(role, prevContent + "\n" + content);
            }
            else
            {
                messages.Add(new ChatMsg(role, content));
            }
            lastRole = role;
        }

        // Ensure current user message doesn't create consecutive user messages
        if (lastRole == userRole && messages.Count > 0)
        {
            // Merge with the last user message
            var prev = (ChatMsg)messages[^1];
            messages[^1] = new ChatMsg(userRole, prev.content + "\n" + userMessage);
        }
        else
        {
            messages.Add(new ChatMsg(userRole, userMessage));
        }

        return messages;
    }

    // GetContentFromAnonymous removed — replaced by ChatMsg record (no reflection needed)

    public abstract Task InitializeAsync(CancellationToken ct = default);

    public abstract IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default);

    public abstract Task<string> GenerateAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default);

    public abstract Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default);

    // Phase 5: Native tool use support (default: not supported)
    public virtual bool SupportsNativeToolUse => false;

    public virtual Task<NativeToolResponse> ChatWithToolsAsync(
        List<NativeMessage> messages,
        string systemPrompt,
        List<ToolSchema> tools,
        CancellationToken ct = default)
        => Task.FromResult(new NativeToolResponse
        {
            TextContent = "Native tool use not supported by this provider.",
            StopReason = "end_turn",
        });

    public virtual void Dispose()
    {
        _httpClient.Dispose();
    }
}
