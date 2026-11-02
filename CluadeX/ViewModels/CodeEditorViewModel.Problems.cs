using System.Text;
using CluadeX.Services.Problems;

namespace CluadeX.ViewModels;

/// <summary>
/// The editor's terminal feeds the Problems list: `dotnet build`, `flutter analyze`, `npx tsc`… typed there turn into
/// squiggles like the agent's own runs. The output of one command is read again after each pause (a long build prints
/// its errors as it goes) — a known toolchain's run replaces its last verdict, so reading the growing output again
/// only ever converges on the whole run.
/// </summary>
public partial class CodeEditorViewModel
{
    private const int TerminalReadPauseMs = 1200;

    private readonly object _termRunGate = new();
    private readonly StringBuilder _termRun = new();
    private string? _termRunCommand;
    private Timer? _termRunTimer;
    private bool _termRunHooked;

    /// <summary>From RunTerminalInput: a new command starts a new run to read.</summary>
    private void NoteTerminalCommand(string command)
    {
        if (!_termRunHooked)
        {
            _termRunHooked = true;
            _terminal.OutputReceived += OnTerminalLineForProblems;
        }
        lock (_termRunGate)
        {
            _termRun.Clear();
            _termRunCommand = command;
        }
    }

    private void OnTerminalLineForProblems(string line)
    {
        lock (_termRunGate)
        {
            if (_termRunCommand == null || _termRun.Length > BuildOutputParser.MaxChars) return;
            _termRun.Append(line).Append('\n');
            _termRunTimer ??= new Timer(_ => ReadTerminalRun(), null, Timeout.Infinite, Timeout.Infinite);
            _termRunTimer.Change(TerminalReadPauseMs, Timeout.Infinite);
        }
    }

    private void ReadTerminalRun()
    {
        try
        {
            string text, command;
            lock (_termRunGate)
            {
                if (_termRunCommand == null) return;
                text = _termRun.ToString();
                command = _termRunCommand;
            }
            string? root = _workspace.HasWorkingDirectory ? _workspace.WorkingDirectory : null;
            BuildProblemsService.Current?.Ingest(text, root, command, succeeded: null);
        }
        catch (Exception ex) { _log?.Warn("Editor", "reading terminal output for problems failed", ex); }
    }
}
