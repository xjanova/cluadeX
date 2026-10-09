namespace CluadeX.Models;

public class ProviderConfig
{
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
    public string? SelectedModel { get; set; }
    public string? CustomModelId { get; set; }
    public bool UseCustomModel { get; set; }

    /// <summary>Anthropic only: sent as the anthropic-workspace-id header. Required by API keys that
    /// aren't scoped to a single workspace (wrkspc_…); ignored by other providers.</summary>
    public string? WorkspaceId { get; set; }

    public string? EffectiveModelId => UseCustomModel && !string.IsNullOrWhiteSpace(CustomModelId)
        ? CustomModelId
        : SelectedModel;
}
