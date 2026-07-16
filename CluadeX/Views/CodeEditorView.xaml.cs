using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Models;
using CluadeX.ViewModels;
using CluadeX.Views.Editor;
using ICSharpCode.AvalonEdit.Rendering;

namespace CluadeX.Views;

public partial class CodeEditorView : UserControl
{
    private readonly AgentGlowRenderer _glow = new();
    private System.Windows.Threading.DispatcherTimer? _glowTimer;
    private CodeEditorViewModel? _vm;
    private OpenFileTab? _boundTab;
    private string _lastSynced = "";
    private bool _suppressSync;
    private bool _hooked;

    public CodeEditorView()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CodeEditorViewModel vm)
        {
            if (!_hooked)
            {
                _hooked = true;
                _vm = vm;
                vm.ScrollToLineRequested += OnScrollToLine;
                vm.AgentEditFlashRequested += OnAgentEditFlash;
                vm.PropertyChanged += OnVmPropertyChanged;

                // Editor chrome XAML can't reach (TextArea exists only at runtime)
                Editor.TextArea.Caret.CaretBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xDF, 0xFF));
                Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xA6, 0x72, 0xFF));
                Editor.TextArea.SelectionBorder = null;
                Editor.TextArea.TextView.BackgroundRenderers.Add(_glow);
                Editor.Options.EnableHyperlinks = false;
                Editor.Options.EnableEmailHyperlinks = false;
                Editor.TextChanged += OnEditorTextChanged;
                Minimap.Attach(Editor);

                BindActiveTab(vm.ActiveTab);
            }
            if (vm.Tree.Count == 0) await vm.RefreshTreeAsync();
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeEditorViewModel.ActiveTab))
            BindActiveTab(_vm?.ActiveTab);
    }

    /// <summary>Point the single editor control at a different tab: swap content, highlighting, glow.</summary>
    private void BindActiveTab(OpenFileTab? tab)
    {
        if (ReferenceEquals(_boundTab, tab)) return;
        if (_boundTab != null) _boundTab.PropertyChanged -= OnTabContentChanged;
        _boundTab = tab;
        _glow.Clear();

        _suppressSync = true;
        try
        {
            if (tab == null)
            {
                Editor.Clear();
                _lastSynced = "";
                return;
            }
            tab.PropertyChanged += OnTabContentChanged;
            Editor.Text = tab.Content;
            _lastSynced = tab.Content;
            Editor.SyntaxHighlighting = NeonHighlighting.ForExtension(tab.Extension);
            Editor.ScrollToHome();
        }
        finally { _suppressSync = false; }
    }

    // ── VM → editor ──
    // Agent edits and the typewriter-reveal frames arrive here as Content changes. Applied as an
    // INCREMENTAL Document.Replace (prefix/suffix diff) so highlighting stays cheap, scroll state
    // survives, and the caret rides the insertion point — which is what makes the reveal look typed.
    private void OnTabContentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OpenFileTab.Content) || _suppressSync || _boundTab == null) return;

        string newText = _boundTab.Content;
        string oldText = _lastSynced;
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) return;

        int limit = Math.Min(oldText.Length, newText.Length);
        int prefix = 0;
        while (prefix < limit && oldText[prefix] == newText[prefix]) prefix++;
        int suffix = 0;
        while (suffix < limit - prefix
            && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;

        int oldMid = oldText.Length - prefix - suffix;
        int newMid = newText.Length - prefix - suffix;

        _suppressSync = true;
        try
        {
            Editor.Document.Replace(prefix, oldMid, newText.Substring(prefix, newMid));
            _lastSynced = newText;

            // Agent-driven change (keyboard focus is elsewhere): follow the typing point and glow
            // the chunk that just landed. Each frame restarts the fade, so the glow stays bright
            // while typing and dissolves on its own when the reveal stops.
            if (!Editor.TextArea.IsKeyboardFocusWithin)
            {
                int caret = Math.Min(prefix + newMid, Editor.Document.TextLength);
                Editor.TextArea.Caret.Offset = caret;
                Editor.TextArea.Caret.BringCaretToView();
                if (newMid > 0)
                {
                    _glow.ShowFlash(prefix, newMid);
                    EnsureGlowTimer();
                }
            }
        }
        catch
        {
            // Diff/replace mismatch (e.g. concurrent change) — hard resync rather than desync.
            try { Editor.Text = newText; _lastSynced = newText; } catch { }
        }
        finally { _suppressSync = false; }
    }

    // ── editor → VM ── (the human typing in the editor; preserves IsDirty tracking + Save commands)
    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressSync || _boundTab == null) return;
        _suppressSync = true;
        try
        {
            _lastSynced = Editor.Text;
            _boundTab.Content = _lastSynced;
        }
        finally { _suppressSync = false; }
    }

    // Live-follow: bring the line the agent just changed into view.
    private void OnScrollToLine(int line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                int clamped = Math.Clamp(line, 1, Math.Max(1, Editor.Document.LineCount));
                Editor.ScrollToLine(clamped);
            }
            catch { /* best-effort scroll */ }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // Reveal complete: flash the whole freshly-typed region (fades out via the renderer).
    private void OnAgentEditFlash(int charStart, int charLength)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (charStart < 0 || charLength <= 0) return;
                _glow.ShowFlash(charStart, charLength);
                EnsureGlowTimer();
            }
            catch { /* cosmetic */ }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Repaints the background layer at ~20fps while the glow is fading, then stops.</summary>
    private void EnsureGlowTimer()
    {
        if (_glowTimer != null) return;
        _glowTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _glowTimer.Tick += (_, _) =>
        {
            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            if (!_glow.IsActive)
            {
                _glowTimer?.Stop();
                _glowTimer = null;
            }
        };
        _glowTimer.Start();
    }

    private async void OnNodeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is FileTreeNode node
            && DataContext is CodeEditorViewModel vm)
        {
            if (vm.ToggleNodeCommand is ICommand cmd && cmd.CanExecute(node))
                cmd.Execute(node);
            await Task.Yield();
        }
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is OpenFileTab tab
            && DataContext is CodeEditorViewModel vm)
        {
            vm.ActiveTab = tab;
        }
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is OpenFileTab tab
            && DataContext is CodeEditorViewModel vm)
        {
            vm.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    // ── Embedded terminal ──

    private void OnTerminalOutputChanged(object sender, TextChangedEventArgs e)
    {
        // Follow the tail like a real terminal.
        TerminalOut.ScrollToEnd();
    }

    private void OnTerminalInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is CodeEditorViewModel vm)
        {
            vm.RunTerminalCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Review the result like a human would: open the active file with its default app
    // (HTML lands in the browser). Dirty tabs are saved first so the preview matches the editor.
    private async void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not CodeEditorViewModel vm || vm.ActiveTab is not { } tab) return;
            if (tab.IsDirty && vm.SaveActiveCommand.CanExecute(null))
            {
                vm.SaveActiveCommand.Execute(null);
                await Task.Delay(150); // let the async save land before the external app reads the file
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tab.FullPath)
            {
                UseShellExecute = true,
            });
        }
        catch { /* preview is best-effort (no associated app, file deleted, …) */ }
    }
}
