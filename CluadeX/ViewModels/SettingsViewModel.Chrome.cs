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

    /// <summary>Until the extension has connected: the button is the way in, so it also works with the switch still off.</summary>
    public bool ChromeBridgeNeedsExtension => _chromeBridge is { } b && !b.Bridge.IsConnected;

    /// <summary>Each browser's "add the extension" button shows only where that browser is installed.</summary>
    public bool ChromeBridgeCanAddToChrome => ChromeBridgeNeedsExtension && _chromeBridge!.ChromeFound;
    public bool ChromeBridgeCanAddToEdge => ChromeBridgeNeedsExtension && _chromeBridge!.EdgeFound;

    private bool _chromeBridgeGuideOpen;
    /// <summary>The user pressed the button. The steps stay up through every status change until the browser connects.</summary>
    public bool ChromeBridgeGuideOpen
    {
        get => _chromeBridgeGuideOpen;
        set
        {
            if (SetProperty(ref _chromeBridgeGuideOpen, value)) RaiseChromeBridgeGuide();
        }
    }

    /// <summary>The steps on screen are Edge's (its Developer mode switch and Load unpacked button sit elsewhere).</summary>
    private bool _chromeBridgeGuideForEdge;

    public bool ChromeBridgeGuideVisible => _chromeBridgeGuideOpen && !_chromeBridgeGuideForEdge && ChromeBridgeNeedsExtension;
    public bool EdgeBridgeGuideVisible => _chromeBridgeGuideOpen && _chromeBridgeGuideForEdge && ChromeBridgeNeedsExtension;

    private void RaiseChromeBridgeGuide()
    {
        OnPropertyChanged(nameof(ChromeBridgeGuideVisible));
        OnPropertyChanged(nameof(EdgeBridgeGuideVisible));
    }

    /// <summary>What "Load unpacked" asks for, shown as well as copied: the clipboard can be overwritten before Chrome is reached.</summary>
    public string ChromeBridgeExtensionFolder => _chromeBridge?.ExtensionFolder ?? "";

    private ICommand? _addChromeExtension;
    public ICommand AddChromeExtensionCommand => _addChromeExtension ??= new RelayCommand(() => AddBridgeExtension(edge: false));

    private ICommand? _addEdgeExtension;
    public ICommand AddEdgeExtensionCommand => _addEdgeExtension ??= new RelayCommand(() => AddBridgeExtension(edge: true));

    private ICommand? _copyChromeExtensionFolder;
    public ICommand CopyChromeExtensionFolderCommand => _copyChromeExtensionFolder ??= new RelayCommand(() => CopyChromeExtensionFolder());

    private bool CopyChromeExtensionFolder()
    {
        try { Clipboard.SetText(ChromeBridgeExtensionFolder); return true; }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // Another app holding the clipboard is no reason to stop: the path is on screen.
            _log?.Warn("Chrome", "copying the extension folder failed", ex);
            return false;
        }
    }

    /// <summary>Everything an app may do of the one step the browser keeps for the user: switch on, copy the folder, open
    /// the extensions page (Chrome's or Edge's), then stay beside them with that browser's steps until the extension connects.</summary>
    private void AddBridgeExtension(bool edge)
    {
        if (_chromeBridge == null) return;
        ChromeBridgeOn = true;
        if (!_chromeBridge.Enabled) return;   // the setter has said why
        CopyChromeExtensionFolder();
        OnPropertyChanged(nameof(ChromeBridgeExtensionFolder));
        try
        {
            if (!_chromeBridge.OpenExtensionsPage(edge)) { ChromeBridgeStatus = T(edge ? "chromeBridge.status.noEdge" : "chromeBridge.status.noChrome"); return; }
            _chromeBridgeGuideForEdge = edge;
            ChromeBridgeGuideOpen = true;
            RaiseChromeBridgeGuide();   // the other browser's steps may have been up already
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log?.Warn("Chrome", $"opening {(edge ? "Edge" : "Chrome")}'s extensions page failed", ex);
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
            : !_chromeBridge.ChromeFound && !_chromeBridge.EdgeFound ? T("chromeBridge.status.noBrowser")
            : bridge.LastError is { } error ? $"{T("settings.failedPrefix")}: {error}"
            : !bridge.IsConnected ? T("chromeBridge.status.waiting")
            : bridge.Paused ? T(_chromeBridge.ConnectedIsEdge ? "chromeBridge.status.pausedEdge" : "chromeBridge.status.paused")
            : T(_chromeBridge.ConnectedIsEdge ? "chromeBridge.status.connectedEdge" : "chromeBridge.status.connected");
        OnPropertyChanged(nameof(ChromeBridgeOn));
        OnPropertyChanged(nameof(ChromeBridgeNeedsExtension));
        OnPropertyChanged(nameof(ChromeBridgeCanAddToChrome));
        OnPropertyChanged(nameof(ChromeBridgeCanAddToEdge));
        RaiseChromeBridgeGuide();
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

    /// <summary>A switch of Claude in Chrome is in flight — its "starting…" line must not be replaced by a re-read.</summary>
    private bool _chromeApplying;

    /// <summary>The two status lines again, in the language just picked (they are built once, as text).</summary>
    private void RelocalizeChromeStatus()
    {
        RefreshChromeBridgeStatus();
        if (!_chromeApplying) RefreshChromeStatus();
    }

    private async Task ApplyChromeAsync(bool on)
    {
        _chromeApplying = true;
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
        finally { _chromeApplying = false; }
    }
}
