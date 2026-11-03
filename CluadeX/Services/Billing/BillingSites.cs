namespace CluadeX.Services.Billing;

/// <summary>Each provider's own billing / usage page — the only place an account's credit balance is shown (no provider
/// has an API for the prepaid balance). Keyed by <see cref="Models.AiProviderType"/> name; the day ledger's "Google"
/// is the Gemini API.</summary>
public static class BillingSites
{
    public static string? For(string? key) => key switch
    {
        "Anthropic" => "https://platform.claude.com/settings/billing",
        "OpenAI" => "https://platform.openai.com/settings/organization/billing",
        "Gemini" or "Google" => "https://aistudio.google.com/usage",
        "ClaudeDev" => "https://claude.ai/settings/usage",
        _ => null,
    };
}
