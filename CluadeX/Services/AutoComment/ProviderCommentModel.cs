using System.Text.Json;
using CluadeX.Services.Providers;

namespace CluadeX.Services.AutoComment;

/// <summary>
/// Auto-comment's model call on a real provider. With native tool use the answer is structured output: the provider is
/// forced to call <see cref="AutoCommentService.ToolName"/>, whose input is exactly <c>{"comments":[{line,text}]}</c>.
/// Without it (a local model, a CLI wrapper) the prompt asks for that JSON as plain text. <paramref name="model"/> (null =
/// the provider's configured one) goes out through <see cref="Agents.SubAgentModel"/>, for this call's flow only.
/// </summary>
public sealed class ProviderCommentModel(IAiProvider provider, string? model) : IAutoCommentModel
{
    public async Task<string> AnswerAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        Agents.SubAgentModel.Use(model);
        if (provider.SupportsNativeToolUse)
        {
            var messages = new List<NativeMessage>
            {
                new() { Role = "user", Content = { new ContentBlock { Type = "text", Text = userPrompt } } },
            };
            var resp = await provider.ChatWithToolsAsync(messages, systemPrompt, new List<ToolSchema> { AutoCommentService.ToolSchema() },
                null, ct, AutoCommentService.ToolName).ConfigureAwait(false);
            var call = resp.ToolCalls.FirstOrDefault(c => c.Name == AutoCommentService.ToolName) ?? resp.ToolCalls.FirstOrDefault();
            if (call != null && call.Input.ValueKind == JsonValueKind.Object) return call.Input.GetRawText();
            if (!string.IsNullOrWhiteSpace(resp.TextContent)) return resp.TextContent!;
        }
        return await provider.GenerateAsync(new List<Models.ChatMessage>(), userPrompt, systemPrompt, ct).ConfigureAwait(false);
    }
}
