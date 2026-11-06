using CluadeX.Services.Providers;

namespace CluadeX.Services.ModelPicker;

/// <summary>Where a model stands right now, as its card in the picker says it.</summary>
public enum PickStatus
{
    /// <summary>It answers this chat now.</summary>
    Current,
    /// <summary>Can be picked.</summary>
    Ready,
    /// <summary>Can be picked, but the vendor said its rate limit is used up until a time.</summary>
    RateLimited,
    /// <summary>Its vendor has no API key yet — the card opens the way to add one.</summary>
    NeedsKey,
    /// <summary>Its CLI is not installed — the card opens the install wizard.</summary>
    NeedsInstall,
    /// <summary>Its CLI is installed but signed out — the card opens the sign-in wizard.</summary>
    NeedsSignIn,
    /// <summary>A cloud model without the Pro licence — the card opens the wizard (the same gate as Settings).</summary>
    NeedsPro,
    /// <summary>A service on this PC / the brain that does not answer now — the card opens its wizard.</summary>
    Offline,
    /// <summary>A GGUF while another provider answers: loading weights into VRAM is the local wizard's step.</summary>
    LoadViaWizard,
    /// <summary>A GGUF is being loaded right now: wait.</summary>
    Loading,
    /// <summary>A GGUF whose architecture no llama.cpp in this copy reads: cannot be picked.</summary>
    ArchUnsupported,
}

/// <summary>What clicking a model's card does.</summary>
public enum PickAction
{
    /// <summary>Nothing — the card says why.</summary>
    None,
    /// <summary>Close the picker (it is already the model).</summary>
    Close,
    /// <summary>Make it the chat's model, exactly as the old model box did.</summary>
    Choose,
    /// <summary>Open the provider's wizard / settings page (key, install, sign in, licence, load).</summary>
    Connect,
}

/// <summary>The facts one model card's status is decided from (all read by the caller; this is pure).</summary>
/// <param name="NeedsSetup">The model box marks it not connected (ChatModelChoice.NeedsKey).</param>
/// <param name="NeedsPro">A cloud provider other than the one answering, and no Pro licence.</param>
/// <param name="SignedIn">A CLI's last sign-in look: true / false / null (not looked yet, or the look failed).</param>
/// <param name="Route">For a GGUF: where it would load (null when not a GGUF or not read).</param>
/// <param name="LocalAnswering">A GGUF on this PC answers the chat now (the local picker may load another).</param>
/// <param name="LocalBusy">A GGUF is loading, or a reply is running — loading another must wait.</param>
public sealed record PickStatusInput(
    PickerProviderKind Kind,
    bool IsCurrent,
    bool NeedsSetup,
    bool NeedsPro = false,
    bool? SignedIn = null,
    RateLimitSnapshot? Rate = null,
    DateTimeOffset? Now = null,
    LocalBackendRoute? Route = null,
    bool LocalAnswering = false,
    bool LocalBusy = false);

public sealed record PickVerdict(PickStatus Status, PickAction Action)
{
    public bool CanChoose => Action is PickAction.Choose or PickAction.Connect or PickAction.Close;
}

/// <summary>
/// The status of a model card. Order matters: a model that answers now is "current" whatever else is true; a GGUF is
/// decided by the file and the local backend; then what is missing (key, CLI, sign-in, licence), and only then the rate
/// limit, which never blocks a pick — it resets within minutes, and the run would wait for it anyway.
/// </summary>
public static class PickStatusRules
{
    public static PickVerdict Decide(PickStatusInput i)
    {
        if (i.IsCurrent) return new(PickStatus.Current, PickAction.Close);

        if (i.Kind == PickerProviderKind.LocalGguf)
        {
            if (i.Route == LocalBackendRoute.Unsupported) return new(PickStatus.ArchUnsupported, PickAction.None);
            if (i.LocalBusy) return new(PickStatus.Loading, PickAction.None);
            return i.LocalAnswering ? new(PickStatus.Ready, PickAction.Choose) : new(PickStatus.LoadViaWizard, PickAction.Connect);
        }

        if (i.NeedsSetup)
            return new(i.Kind switch
            {
                PickerProviderKind.Cli => PickStatus.NeedsInstall,
                PickerProviderKind.Ollama or PickerProviderKind.Room => PickStatus.Offline,
                _ => PickStatus.NeedsKey,
            }, PickAction.Connect);

        if (i.Kind == PickerProviderKind.Cli && i.SignedIn == false) return new(PickStatus.NeedsSignIn, PickAction.Connect);
        if (i.NeedsPro) return new(PickStatus.NeedsPro, PickAction.Connect);
        if (IsRateLimited(i.Rate, i.Now ?? DateTimeOffset.Now)) return new(PickStatus.RateLimited, PickAction.Choose);
        return new(PickStatus.Ready, PickAction.Choose);
    }

    /// <summary>The vendor's last answer said a bucket is empty and its reset is still ahead.</summary>
    public static bool IsRateLimited(RateLimitSnapshot? rate, DateTimeOffset now) =>
        rate is { PercentLeft: <= 0.5, Reset: { } reset } && reset > now;
}
