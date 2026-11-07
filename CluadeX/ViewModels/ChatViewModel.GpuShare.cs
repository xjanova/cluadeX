using System.IO;
using CluadeX.Models;

namespace CluadeX.ViewModels;

public partial class ChatViewModel
{
    /// <summary>
    /// The GPU share slider's "Reload model now": unload the local GGUF that is loaded and load it again so the new
    /// share (-ngl) takes effect. Only on the owner's click, after a confirm, never while an answer or a load runs.
    /// Returns true when the model is loaded again.
    /// </summary>
    public async Task<bool> ReloadLocalModelForGpuShareAsync()
    {
        string? path = _settingsService.Settings.SelectedModelPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        if (IsGenerating || IsLoadingModel || _llamaService.IsLoading || (_localGgufProvider?.IsLoading ?? false))
        {
            StatusText = Loc("gpushare.reloadBusy");
            return false;
        }
        string name = Path.GetFileNameWithoutExtension(path);
        int pct = Services.Gpu.GpuShare.Clamp(_settingsService.Settings.GpuUsagePercent);
        if (!Confirm(LocF("gpushare.reloadConfirm", name, pct), Loc("gpushare.reloadTitle"))) return false;

        IsLoadingModel = true;
        StatusText = LocF("gpushare.reloading", name, pct);
        try
        {
            if (_providerManager.ActiveProviderType != AiProviderType.Local)
                await _providerManager.SwitchProviderAsync(AiProviderType.Local);
            var progress = new Progress<string>(s => App.Current?.Dispatcher.BeginInvoke(() => StatusText = s));
            if (_localGgufProvider != null)
            {
                await _localGgufProvider.UnloadAsync();
                await _localGgufProvider.LoadModelAsync(path, progress);
            }
            else
            {
                _llamaService.UnloadModel();
                await _llamaService.LoadModelAsync(path, progress);
            }
            StatusText = LocF("gpushare.reloaded", name, pct);
            return true;
        }
        catch (Exception ex)
        {
            _debugLog?.Error("Chat", $"Reloading {path} for the GPU share ({pct}%) failed", ex);
            StatusText = LocF("gpushare.reloadFailed", name);
            return false;
        }
        finally
        {
            IsLoadingModel = false;
        }
    }
}
