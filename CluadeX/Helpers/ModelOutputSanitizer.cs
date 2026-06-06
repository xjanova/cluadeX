namespace CluadeX.Helpers;

/// <summary>
/// Cleans local-model streaming output. Some GGUF chat models (DeepSeek R1 Distill, Qwen, …) leak
/// their chat-template control tokens into the visible content and sometimes start a fake next turn
/// (e.g. "…GPT-4&lt;|im_end|&gt;&lt;|im_start|&gt;"). This strips those so the user never sees them.
/// Shared by the in-proc (LlamaInferenceService) and llama-server (LlamaServerProvider) paths.
/// </summary>
public static class ModelOutputSanitizer
{
    // Once any of these is seen the assistant turn is over — drop it and everything after it.
    private static readonly string[] TurnEndMarkers =
    {
        "<|im_end|>", "<|endoftext|>", "<|eot_id|>", "<|end_of_turn|>", "<|end|>",
        "<｜end▁of▁sentence｜>", "<|end▁of▁sentence|>", "</s>",
    };

    // Role / structural tokens that should never be shown to the user.
    private static readonly string[] StrayControlTokens =
    {
        "<|im_start|>", "<|im_sep|>", "<|assistant|>", "<|user|>", "<|system|>",
        "<｜Assistant｜>", "<｜User｜>", "<｜begin▁of▁sentence｜>",
    };

    /// <summary>
    /// Clean one streamed chunk. Returns the displayable text and whether the turn ended
    /// (the caller should stop streaming once stop=true).
    /// </summary>
    public static (string text, bool stop) SanitizeStreamChunk(string? s)
    {
        if (string.IsNullOrEmpty(s)) return (string.Empty, false);

        int cut = -1;
        foreach (var m in TurnEndMarkers)
        {
            int idx = s.IndexOf(m, System.StringComparison.Ordinal);
            if (idx >= 0 && (cut < 0 || idx < cut)) cut = idx;
        }

        bool stop = cut >= 0;
        string text = stop ? s[..cut] : s;
        foreach (var t in StrayControlTokens) text = text.Replace(t, "");
        return (text, stop);
    }
}
