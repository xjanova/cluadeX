namespace CluadeX.Models;

public class ProviderConfig
{
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
    public string? SelectedModel { get; set; }
    public string? CustomModelId { get; set; }
    public bool UseCustomModel { get; set; }

    public string? EffectiveModelId => UseCustomModel && !string.IsNullOrWhiteSpace(CustomModelId)
        ? CustomModelId
        : SelectedModel;
}
