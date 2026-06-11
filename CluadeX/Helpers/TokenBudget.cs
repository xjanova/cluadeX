namespace CluadeX.Helpers;

/// <summary>
/// Shared generation-budget math so the prompt is never starved of the context window.
///
/// A common mis-set is <c>MaxTokens == ContextSize</c> (e.g. both 4096): generation alone would then try
/// to consume the entire window, leaving ZERO room for the prompt → the model stalls or emits nothing
/// (the documented "local chat hangs / never answers" bug). The in-process LLamaSharp path already clamps
/// (LlamaInferenceService), but the llama-server / Ollama HTTP path — which local GPU mode now routes
/// through after commit c95a986 — did not. This centralizes the clamp so every local path shares it.
/// </summary>
public static class TokenBudget
{
    /// <summary>
    /// Rough token estimate from raw text. ASCII ≈ 4 chars/token; non-ASCII (Thai, CJK, emoji) is
    /// counted at 1 token/char — BPE vocabularies split Thai into 1-2 tokens per character, so the old
    /// flat /4 UNDER-counted Thai prompts 2-4×, over-allocating generation and re-opening the
    /// prompt-starvation hang for exactly the Thai-language sessions this clamp exists to protect.
    /// Intentionally biased high overall (JSON keys/quotes inflate ASCII counts) — the SAFE direction.
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int nonAscii = 0;
        foreach (char c in text) if (c > 127) nonAscii++;
        return (text.Length - nonAscii) / 4 + nonAscii;
    }

    /// <summary>
    /// Clamp the requested generation budget so prompt + generation fits the context window.
    /// Returns <c>Min(requestedMaxTokens, ctx - promptTokens - margin)</c>, floored so there is always
    /// real room to answer. <paramref name="ctxSize"/> is floored at 512 to tolerate a tiny mis-config.
    /// </summary>
    /// <param name="ctxSize">The model's context window (settings.ContextSize).</param>
    /// <param name="approxPromptTokens">Estimated prompt footprint (see <see cref="EstimateTokens"/>).</param>
    /// <param name="requestedMaxTokens">The user's MaxTokens / num_predict. &lt;=0 means "as much as fits".</param>
    /// <param name="margin">Safety slack reserved on top of the prompt estimate.</param>
    /// <param name="floor">Minimum generation budget — never clamp below this (one tool call still fits).</param>
    public static int ClampMaxTokens(int ctxSize, int approxPromptTokens, int requestedMaxTokens,
        int margin = 64, int floor = 256)
    {
        ctxSize = Math.Max(512, ctxSize);
        if (requestedMaxTokens <= 0) requestedMaxTokens = ctxSize; // 0/unset = "as much as fits"
        int roomForGen = Math.Max(floor, ctxSize - approxPromptTokens - margin);
        return Math.Min(requestedMaxTokens, roomForGen);
    }
}
