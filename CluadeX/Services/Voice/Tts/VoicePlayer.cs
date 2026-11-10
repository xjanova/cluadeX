using System.IO;
using System.Windows.Media;

namespace CluadeX.Services.Voice.Tts;

/// <summary>
/// Plays synthesized speech inside CluadeX (WPF MediaPlayer) when the mic window is not open, and voice previews.
/// One sound at a time: a new one stops the last. Use it from the UI thread. The audio is written to
/// <c>&lt;data&gt;\voice\tts</c> and the older files are removed as new ones arrive.
/// </summary>
public sealed class VoicePlayer : IDisposable
{
    private readonly string _folder;
    private MediaPlayer? _player;
    private string? _file;
    private int _n;

    /// <summary>A sound ended, was stopped or could not play (the message, or null).</summary>
    public event Action<string?>? Finished;
    public bool IsPlaying { get; private set; }

    public VoicePlayer(string dataRoot) => _folder = Path.Combine(dataRoot, "voice", "tts");

    public void Play(SpeechAudio audio)
    {
        Stop();
        Directory.CreateDirectory(_folder);
        Sweep();
        _file = Path.Combine(_folder, $"say-{Environment.ProcessId}-{++_n}{audio.Extension}");
        File.WriteAllBytes(_file, audio.Bytes);
        Open(new Uri(_file));
    }

    /// <summary>A voice's sample from the web (ElevenLabs preview_url).</summary>
    public void PlayUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        Stop();
        Open(uri);
    }

    private void Open(Uri uri)
    {
        var p = new MediaPlayer();
        _player = p;
        p.MediaEnded += (_, _) => { if (_player == p) End(null); };
        p.MediaFailed += (_, e) => { if (_player == p) End(e.ErrorException?.Message ?? "?"); };
        p.Open(uri);
        p.Play();
        IsPlaying = true;
    }

    private void End(string? error)
    {
        var p = _player;
        _player = null;
        IsPlaying = false;
        try { p?.Close(); } catch { }
        Finished?.Invoke(error);
    }

    /// <summary>Hold the sound where it is (the voice player's pause); <see cref="Resume"/> carries on from there.</summary>
    public void Pause()
    {
        try { _player?.Pause(); } catch { }
    }

    public void Resume()
    {
        try { _player?.Play(); } catch { }
    }

    public void Stop()
    {
        if (_player == null) return;
        try { _player.Stop(); } catch { }
        End(null);
    }

    /// <summary>Remove the files of earlier sounds (and of earlier runs) — only the playing one stays.</summary>
    private void Sweep()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_folder, "say-*"))
                if (!string.Equals(f, _file, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { /* still open in a player that is closing */ }
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        _file = null;
        Sweep();
    }
}
