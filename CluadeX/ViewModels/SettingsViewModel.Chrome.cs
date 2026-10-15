using CluadeX.Services.Chrome;

namespace CluadeX.ViewModels;

/// <summary>Settings ▸ Agent ▸ "Your Chrome": the agent may drive the user's own Chrome through Claude in Chrome.</summary>
public partial class SettingsViewModel
{
    private ClaudeInChromeConnector? _chrome;

    public bool HasChromeConnector => _chrome != null;

    public bool ChromeConnectorOn
    {
        get => _chrome?.Enabled ?? false;
        set
        {
            if (_chrome == null || value == _chrome.Enabled) return;
            _ = ApplyChromeAsync(value);
        }
    }

    private string _chromeStatus = "";
    public string ChromeStatus { get => _chromeStatus; set => SetProperty(ref _chromeStatus, value); }

    private void InitChrome(ClaudeInChromeConnector? chrome)
    {
        _chrome = chrome;
        RefreshChromeStatus();
    }

    private void RefreshChromeStatus()
    {
        if (_chrome == null) return;
        ChromeStatus = !_chrome.ClaudeCodeFound ? T("chrome.status.noClaudeCode")
            : !_chrome.ExtensionSetUp ? T("chrome.status.noExtension")
            : _chrome.Enabled ? T("chrome.status.on") : T("chrome.status.off");
        OnPropertyChanged(nameof(ChromeConnectorOn));
    }

    private async Task ApplyChromeAsync(bool on)
    {
        try
        {
            ChromeStatus = T(on ? "chrome.status.starting" : "chrome.status.stopping");
            bool ok = await _chrome!.SetEnabledAsync(on);
            RefreshChromeStatus();
            if (on && !ok) ChromeStatus = T("chrome.status.startFailed");
        }
        catch (Exception ex)
        {
            _log?.Warn("Chrome", "switching Claude in Chrome failed", ex);
            ChromeStatus = $"{T("settings.failedPrefix")}: {DescribeFailure(ex)}";
            OnPropertyChanged(nameof(ChromeConnectorOn));
        }
    }
}
