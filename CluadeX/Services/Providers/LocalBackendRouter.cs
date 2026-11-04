using System.IO;

namespace CluadeX.Services.Providers;

/// <summary>Where <see cref="LocalGgufProvider"/> loads a GGUF.</summary>
public enum LocalBackendRoute
{
    /// <summary>In-process LLamaSharp.</summary>
    InProcess,
    /// <summary>llama-server, because the in-process llama.cpp cannot read this architecture — no way back.</summary>
    Server,
    /// <summary>llama-server for GPU offload / vision; a failure there degrades to in-process (CPU).</summary>
    PreferServer,
    /// <summary>No llama.cpp this copy can run knows the architecture: fail before touching memory or the GPU.</summary>
    Unsupported,
}

/// <summary>
/// The backend decision of <see cref="LocalGgufProvider.LoadModelAsync"/>, kept free of I/O so it can be proven
/// without a model, a GPU or a server process.
/// </summary>
public static class LocalBackendRouter
{
    /// <param name="architecture">general.architecture of the GGUF (null = could not be read).</param>
    /// <param name="inProcessSupports">Does LLamaSharp's llama.dll know it (null = dll could not be scanned).</param>
    /// <param name="serverAvailable">A llama-server.exe was found.</param>
    /// <param name="serverSupports">Does the llama.dll next to that exe know it (null = unknown, e.g. a PATH server).</param>
    public static LocalBackendRoute Decide(string? architecture, bool? inProcessSupports, bool serverAvailable,
                                           bool? serverSupports, bool wantGpu, bool hasProjector)
    {
        // An unreadable header (null arch) goes in-process and fails there with the loader's own words, as before.
        bool inProcessOk = string.IsNullOrEmpty(architecture)
                           || (inProcessSupports ?? !GgufMetadataReader.IsOnFallbackList(architecture));
        if (!inProcessOk)
            return serverAvailable && serverSupports != false ? LocalBackendRoute.Server : LocalBackendRoute.Unsupported;

        // A server whose llama.cpp is known not to read this model is no GPU path: it would only fail and degrade.
        bool serverUsable = serverAvailable && serverSupports != false;
        return serverUsable && (wantGpu || hasProjector) ? LocalBackendRoute.PreferServer : LocalBackendRoute.InProcess;
    }

    /// <summary>The user-facing refusal, in the app's language (English for anything but "th").</summary>
    public static ModelArchitectureNotSupportedException Refusal(string architecture, string modelPath, string? serverExe, string? language)
    {
        string lang = language == "th" ? "th" : "en";
        string name = Path.GetFileName(modelPath);
        string key = serverExe == null ? "local.archUnsupported" : "local.archUnsupportedServer";
        string format = LocalizationService.Lookup(key, lang) ?? key;
        string message = string.Format(format, architecture, name, serverExe ?? "");
        string shortFormat = LocalizationService.Lookup("local.archUnsupportedShort", lang) ?? "local.archUnsupportedShort";
        return new ModelArchitectureNotSupportedException(architecture, modelPath, message, string.Format(shortFormat, architecture));
    }
}

/// <summary>
/// A GGUF whose architecture no llama.cpp of this copy can load (the bundled LLamaSharp one is older than the model,
/// and there is no llama-server, or it is just as old). <see cref="Exception.Message"/> is localized and names the
/// ways out; <see cref="ShortMessage"/> fits a status bar.
/// </summary>
public sealed class ModelArchitectureNotSupportedException : InvalidOperationException
{
    public string Architecture { get; }
    public string ModelPath { get; }
    public string ShortMessage { get; }

    public ModelArchitectureNotSupportedException(string architecture, string modelPath, string message, string shortMessage)
        : base(message)
    {
        Architecture = architecture;
        ModelPath = modelPath;
        ShortMessage = shortMessage;
    }
}
