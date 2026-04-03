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
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
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

    protected List<object> BuildChatMessages(List<ChatMessage> history, string userMessage, string? systemPrompt,
        string systemRole = "system", string userRole = "user", string assistantRole = "assistant")
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new { role = systemRole, content = systemPrompt });

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
                var prev = messages[^1];
                string prevContent = GetContentFromAnonymous(prev);
                messages[^1] = new { role, content = prevContent + "\n" + content };
            }
            else
            {
                messages.Add(new { role, content });
            }
            lastRole = role;
        }

        // Ensure current user message doesn't create consecutive user messages
        if (lastRole == userRole && messages.Count > 0)
        {
            // Merge with the last user message
            var prev = messages[^1];
            string prevContent = GetContentFromAnonymous(prev);
            messages[^1] = new { role = userRole, content = prevContent + "\n" + userMessage };
        }
        else
        {
            messages.Add(new { role = userRole, content = userMessage });
        }

        return messages;
    }

    /// <summary>Extract content string from anonymous { role, content } object.</summary>
    private static string GetContentFromAnonymous(object obj)
    {
        var prop = obj.GetType().GetProperty("content");
        return prop?.GetValue(obj)?.ToString() ?? "";
    }

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

    public virtual void Dispose()
    {
        _httpClient.Dispose();
    }
}
