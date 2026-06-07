using System.Text;

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
    internal static readonly string[] TurnEndMarkers =
    {
        "<|im_end|>", "<|endoftext|>", "<|eot_id|>", "<|end_of_turn|>", "<|end|>",
        "<｜end▁of▁sentence｜>", "<|end▁of▁sentence|>", "</s>",
    };

    // Role / structural tokens that should never be shown to the user.
    internal static readonly string[] StrayControlTokens =
    {
        "<|im_start|>", "<|im_sep|>", "<|assistant|>", "<|user|>", "<|system|>",
        "<｜Assistant｜>", "<｜User｜>", "<｜begin▁of▁sentence｜>",
    };

    /// <summary>
    /// Clean one streamed chunk. Returns the displayable text and whether the turn ended
    /// (the caller should stop streaming once stop=true).
    /// NOTE: this is the stateless per-chunk version. It CANNOT catch a control token that the model
    /// streams split across chunks (e.g. "&lt;|im_" then "end|&gt;"). For streaming, prefer
    /// <see cref="StreamingSanitizer"/>, which buffers across chunks. Kept for non-streaming callers.
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

/// <summary>
/// Stateful, streaming-safe sanitizer. The per-chunk <see cref="ModelOutputSanitizer.SanitizeStreamChunk"/>
/// misses control tokens the model emits split across chunks (token boundaries rarely align with a marker
/// like "&lt;|im_end|&gt;"), so those leaked into the chat verbatim. This buffers a small tail (the longest
/// possible marker minus one char) and only releases text that cannot be the start of a marker — so a
/// split marker is reassembled and stripped before it is ever shown.
/// One instance per generation; call <see cref="Push"/> per chunk and <see cref="Flush"/> at the end.
/// </summary>
public sealed class StreamingSanitizer
{
    private readonly StringBuilder _buf = new();
    private static readonly int MaxMarkerLen = ComputeMaxMarkerLen();

    private static int ComputeMaxMarkerLen()
    {
        int m = 1;
        foreach (var s in ModelOutputSanitizer.TurnEndMarkers) if (s.Length > m) m = s.Length;
        foreach (var s in ModelOutputSanitizer.StrayControlTokens) if (s.Length > m) m = s.Length;
        return m;
    }

    /// <summary>Feed one streamed chunk. Returns the text safe to display now + whether the turn ended.</summary>
    public (string emit, bool stop) Push(string? chunk)
    {
        if (!string.IsNullOrEmpty(chunk)) _buf.Append(chunk);

        StripCompleteStrayTokens();

        string s = _buf.ToString();

        // A COMPLETE turn-end marker → emit everything before it, then signal stop.
        int cut = EarliestTurnEnd(s);
        if (cut >= 0)
        {
            string head = s[..cut];
            _buf.Clear();
            return (head, true);
        }

        // Otherwise hold back a tail that could be the START of a (turn-end or stray) marker.
        int hold = System.Math.Min(MaxMarkerLen - 1, s.Length);
        int safeLen = s.Length - hold;
        if (safeLen <= 0) return (string.Empty, false);

        string emit = s[..safeLen];
        _buf.Remove(0, safeLen);
        return (emit, false);
    }

    /// <summary>Release whatever is left when the stream ends (cut at any turn-end, strip strays).</summary>
    public string Flush()
    {
        StripCompleteStrayTokens();
        string s = _buf.ToString();
        _buf.Clear();
        int cut = EarliestTurnEnd(s);
        if (cut >= 0) s = s[..cut];
        return s;
    }

    private static int EarliestTurnEnd(string s)
    {
        int cut = -1;
        foreach (var m in ModelOutputSanitizer.TurnEndMarkers)
        {
            int idx = s.IndexOf(m, System.StringComparison.Ordinal);
            if (idx >= 0 && (cut < 0 || idx < cut)) cut = idx;
        }
        return cut;
    }

    private void StripCompleteStrayTokens()
    {
        string s = _buf.ToString();
        bool changed = false;
        foreach (var t in ModelOutputSanitizer.StrayControlTokens)
        {
            if (s.Contains(t, System.StringComparison.Ordinal)) { s = s.Replace(t, ""); changed = true; }
        }
        if (changed) { _buf.Clear(); _buf.Append(s); }
    }
}
