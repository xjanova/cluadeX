using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Voice.Tts;

namespace CluadeX.Views;

/// <summary>The voice player box (VoicePlayerBar.xaml). DataContext = <see cref="ReplyVoiceQueue"/>.</summary>
public partial class VoicePlayerBar : UserControl
{
    private ReplyVoiceQueue? _queue;

    /// <summary>A click on the reply's first words: the chat scrolls to its bubble.</summary>
    public event Action<ChatMessage>? ShowMessage;

    public VoicePlayerBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ReplyVoiceQueue);
        LocalizedResources.Instance.PropertyChanged += (_, _) => UpdateWaiting();
    }

    private void Attach(ReplyVoiceQueue? queue)
    {
        if (_queue != null) _queue.PropertyChanged -= OnQueueChanged;
        _queue = queue;
        if (_queue != null) _queue.PropertyChanged += OnQueueChanged;
        UpdateWaiting();
    }

    private void OnQueueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReplyVoiceQueue.Waiting) or null) UpdateWaiting();
    }

    /// <summary>"· 2 waiting" — the replies that will be read after this one.</summary>
    private void UpdateWaiting()
    {
        int n = _queue?.Waiting ?? 0;
        WaitingRun.Text = n > 0 ? " · " + string.Format(LocalizedResources.Instance["voice.player.waiting"], n) : "";
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_queue?.Current?.Message is { } m) ShowMessage?.Invoke(m);
    }
}
