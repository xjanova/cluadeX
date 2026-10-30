using System.IO;
using System.Windows;
using System.Windows.Input;
using CluadeX.Services.Chrome;

namespace CluadeX.ViewModels;

/// <summary>Settings ▸ Agent ▸ "Your Chrome": the agent may drive the user's own Chrome, through CluadeX's own extension
/// or through Claude in Chrome.</summary>
public partial class SettingsViewModel
{
    private ClaudeInChromeConnector? _chrome;
    private ChromeBridgeConnector? _chromeBridge;

    public bool HasChromeConnector => _chrome != null || _chromeBridge != null;

    public bool ChromeBridgeOn
    {
        get => _chromeBridge?.Enabled ?? false;
        set
        {
            if (_chromeBridge == null || value == _chromeBridge.Enabled) return;
            try { _chromeBridge.SetEnabled(value); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _log?.Warn("Chrome", "switching the Chrome connection failed", ex);
                ChromeBridgeStatus = $"{T("settings.failedPrefix")}: {DescribeFailure(ex)}";
                OnPropertyChanged(nameof(ChromeBridgeOn));
                return;
            }
            RefreshChromeBridgeStatus();
        }
    }

    private string _chromeBridgeStatus = "";
    public string ChromeBridgeStatus { get => _chromeBridgeStatus; set => SetProperty(ref _chromeBridgeStatus, value); }

    /// <summary>Only while the extension is not in Chrome yet: once it has connected there is nothing left to add.</summary>
    public bool ChromeBridgeNeedsExtension => _chromeBridge is { Enabled: true } b && !b.Bridge.IsConnected;

    private ICommand? _addChromeExtension;
    public ICommand AddChromeExtensionCommand => _addChromeExtension ??= new RelayCommand(AddChromeExtension);

    /// <summary>The one step Chrome keeps for the user: the page opens, the folder is on the clipboard for "Load unpacked".</summary>
    private void AddChromeExtension()
    {
        if (_chromeBridge == null) return;
        try
        {
            Clipboard.SetText(_chromeBridge.ExtensionFolder);
            ChromeBridgeStatus = _chromeBridge.OpenExtensionsPage() ? T("chromeBridge.status.steps") : T("chromeBridge.status.noChrome");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            _log?.Warn("Chrome", "opening Chrome's extensions page failed", ex);
            ChromeBridgeStatus = $"{T("settings.failedPrefix")}: {DescribeFailure(ex)}";
        }
    }

    private void InitChromeBridge(ChromeBridgeConnector? bridge)
    {
        _chromeBridge = bridge;
        if (bridge == null) return;
        bridge.Bridge.StateChanged += () => Application.Current?.Dispatcher.BeginInvoke(RefreshChromeBridgeStatus);
        RefreshChromeBridgeStatus();
    }

    private void RefreshChromeBridgeStatus()
    {
        if (_chromeBridge == null) return;
        var bridge = _chromeBridge.Bridge;
        ChromeBridgeStatus = !_chromeBridge.Enabled ? T("chrome.status.off")
            : !_chromeBridge.ChromeFound ? T("chromeBridge.status.noChrome")
            : bridge.LastError is { } error ? $"{T("settings.failedPrefix")}: {error}"
            : !bridge.IsConnected ? T("chromeBridge.status.waiting")
            : bridge.Paused ? T("chromeBridge.status.paused")
            : T("chromeBridge.status.connected");
        OnPropertyChanged(nameof(ChromeBridgeOn));
        OnPropertyChanged(nameof(ChromeBridgeNeedsExtension));
    }

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
