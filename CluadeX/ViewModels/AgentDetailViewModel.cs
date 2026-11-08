using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// One sub-agent's own window (owner 2026-11-07: "คลิกดูได้ว่ามันกำลังรันอะไรทำอะไร เป็นหน้าต่างลอยแยกขึ้นมา"): its task,
/// what it runs now, every tool it ran with the result, its model, time, tool count and cost. It reads the same
/// <see cref="AgentNode"/> the map card draws — the chat's event handler changes it, the window follows — and ticks the
/// clock once a second while the agent works (stopped once it ends or the window closes).
/// </summary>
public sealed class AgentDetailViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _clock;
    private bool _disposed;

    public AgentNode Node { get; }
    public ObservableCollection<AgentActivityStep> Activity => Node.Activity;

    public AgentDetailViewModel(AgentNode node)
    {
        Node = node;
        Node.PropertyChanged += OnNodeChanged;
        Node.Activity.CollectionChanged += OnActivityChanged;
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => OnPropertyChanged(nameof(ElapsedText));
        if (node.IsRunning) _clock.Start();
        StopCommand = new RelayCommand(() => Stop(), () => CanStop);
        SendCommand = new RelayCommand(() => Send(), () => CanSend && Draft.Trim().Length > 0);
        UpgradeCommand = new RelayCommand(() => Upgrade(), () => CanUpgrade);
    }

    // ─── steering it (owner 2026-11-07: "สั่งยกเลิกหรือหยุดเอเจนต์ หรือพิมพ์แทรกคำสั่งเข้าไปในเอเจนต์นั้นได้") ───

    /// <summary>Asks before stopping an agent that may edit files (title, text → yes). Replaced by the harness.</summary>
    public static Func<string, string, bool> ConfirmStop { get; set; } = (title, text) =>
        System.Windows.MessageBox.Show(text, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
        == System.Windows.MessageBoxResult.Yes;

    public RelayCommand StopCommand { get; }
    public RelayCommand SendCommand { get; }
    /// <summary>Moves a cheap/mid agent onto the run's own model from its next step (owner 2026-11-08: an Upgrade
    /// button in the agent window).</summary>
    public RelayCommand UpgradeCommand { get; }

    private string _draft = "";
    /// <summary>The message being typed for it.</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            if (!SetProperty(ref _draft, value ?? "")) return;
            if (_draft.Length > 0) SendError = "";   // typing again clears the last refusal
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _sendError = "";
    public string SendError { get => _sendError; private set { if (SetProperty(ref _sendError, value)) OnPropertyChanged(nameof(HasSendError)); } }
    public bool HasSendError => _sendError.Length > 0;

    private bool Steerable => Node.IsRunning && Node.Control is { IsClosed: false, StopRequested: false };
    public bool CanStop => Steerable;
    public bool CanSend => Steerable;
    /// <summary>It runs on a cheaper tier and can still be moved up (once).</summary>
    public bool CanUpgrade => Steerable && Node.CanUpgrade && Node.Control?.UpgradeRequested != true;
    /// <summary>The upgrade was asked for and its next step has not picked it up yet.</summary>
    public bool UpgradePending => Node.IsRunning && Node.Control?.UpgradeRequested == true;

    /// <summary>Ask it to move to the run's model from its next step. False when it cannot (already deep, ended,
    /// stopping, or asked before).</summary>
    public bool Upgrade()
    {
        if (!CanUpgrade || Node.Control is not { } ctl) return false;
        bool asked = ctl.RequestUpgrade();
        RaiseControl();
        return asked;
    }
    public bool StopPending => Node.IsRunning && Node.Control?.StopRequested == true;
    /// <summary>Why it cannot be steered (empty while it can): ended, stopping, or not run by CluadeX itself.</summary>
    public string ControlNote
    {
        get
        {
            if (!Node.IsRunning)
                return Node.State switch
                {
                    "done" => Loc("agentdetail.ctl.done"),
                    "failed" => Loc("agentdetail.ctl.failed"),
                    _ => Loc(Node.StoppedByOwner ? "agentdetail.ctl.stoppedByYou" : "agentdetail.ctl.stopped"),
                };
            if (Node.Control == null)
                return Loc(Node.Kind is "worker" or "fixer" or "reviewer" ? "agentdetail.ctl.dispatch" : "agentdetail.ctl.cli");
            if (Node.Control.StopRequested) return Loc("agentdetail.ctl.stopping");
            if (Node.Control.IsClosed) return Loc("agentdetail.ctl.ended");
            return "";
        }
    }
    public bool HasControlNote => ControlNote.Length > 0;

    /// <summary>Stop this agent only. False when nothing was stopped: it cannot be steered, the owner said no, or it was
    /// already stopping (a second press is a no-op).</summary>
    public bool Stop()
    {
        if (!CanStop || Node.Control is not { } ctl) return false;
        if (ctl.CanEdit && !ConfirmStop(Loc("agentdetail.stop.confirmTitle"), string.Format(Loc("agentdetail.stop.confirmText"), Node.Label)))
            return false;
        bool stopped = ctl.Stop();
        RaiseControl();
        return stopped;
    }

    /// <summary>Queue the draft for its next step; its row shows until the agent reads it.</summary>
    public bool Send()
    {
        string text = Draft.Trim();
        if (text.Length == 0) return false;
        // Not steerable: the note under the box already says why (a red copy of it would only repeat it).
        if (!CanSend || Node.Control is not { } ctl) { SendError = ""; return false; }
        string? id = ctl.Inject(text);
        if (id == null) { SendError = Loc("agentdetail.send.refused"); return false; }
        Node.BeginStep(id, "💬 " + (text.Length > Services.Agents.SubAgentControl.MaxNoteChars ? text[..Services.Agents.SubAgentControl.MaxNoteChars] : text));
        Draft = "";
        SendError = "";
        return true;
    }

    private void RaiseControl()
    {
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanUpgrade));
        OnPropertyChanged(nameof(UpgradePending));
        OnPropertyChanged(nameof(StopPending));
        OnPropertyChanged(nameof(ControlNote));
        OnPropertyChanged(nameof(HasControlNote));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The clock is running (the harness checks it stops once the agent ends).</summary>
    public bool ClockRunning => _clock.IsEnabled;

    public string Title => Node.Label;
    public string StateText => Node.State switch
    {
        "done" => Loc("agentdetail.state.done"),
        "failed" => Loc("agentdetail.state.failed"),
        "stopped" => Loc("agentdetail.state.stopped"),
        _ => Loc("agentdetail.state.running"),
    };
    /// <summary>Live while it works (from its start), the recorded time once it ended.</summary>
    public string ElapsedText
    {
        get
        {
            if (!Node.IsRunning && Node.Elapsed.Length > 0) return Node.Elapsed;
            var took = DateTime.Now - Node.StartedAt;
            return took.TotalMinutes >= 1 ? $"{(int)took.TotalMinutes}m {took.Seconds}s" : $"{Math.Max(0, took.Seconds)}s";
        }
    }
    public string ModelText => Node.Model.Length > 0 ? Node.Model : Loc("agentdetail.modelOfRun");
    public string CostText => Node.CostUsd > 0 ? $"${Node.CostUsd:0.000}" : "—";
    public string ToolsText => Node.ToolCalls.ToString();
    /// <summary>Its line on the card: what it runs now, or how it ended.</summary>
    public string NowLabel => Node.IsRunning ? Loc("agentdetail.now") : Node.IsDone ? Loc("agentdetail.report") : Loc("agentdetail.error");
    public bool HasActivity => Node.Activity.Count > 0;
    /// <summary>An agent read back from a saved chat: its steps were not kept (only what the card shows).</summary>
    public bool ActivityNotKept => !Node.IsRunning && Node.Activity.Count == 0;
    public bool WaitingFirstStep => Node.IsRunning && Node.Activity.Count == 0;

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AgentNode.State):
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(NowLabel));
                OnPropertyChanged(nameof(ElapsedText));
                OnPropertyChanged(nameof(ActivityNotKept));
                OnPropertyChanged(nameof(WaitingFirstStep));
                if (!Node.IsRunning) _clock.Stop();
                else if (!_disposed) _clock.Start();
                OnPropertyChanged(nameof(ClockRunning));
                RaiseControl();
                break;
            case nameof(AgentNode.Control):
            case nameof(AgentNode.CanUpgrade):
            case nameof(AgentNode.StoppedByOwner): RaiseControl(); break;
            case nameof(AgentNode.Elapsed): OnPropertyChanged(nameof(ElapsedText)); break;
            case nameof(AgentNode.Model): OnPropertyChanged(nameof(ModelText)); break;
            case nameof(AgentNode.CostUsd): OnPropertyChanged(nameof(CostText)); break;
            case nameof(AgentNode.ToolCalls): OnPropertyChanged(nameof(ToolsText)); break;
        }
    }

    private void OnActivityChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(ActivityNotKept));
        OnPropertyChanged(nameof(WaitingFirstStep));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clock.Stop();
        Node.PropertyChanged -= OnNodeChanged;
        Node.Activity.CollectionChanged -= OnActivityChanged;
    }

    private static string Loc(string key) => LocalizedResources.Instance[key];
}
