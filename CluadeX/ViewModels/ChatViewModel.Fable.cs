using System.Collections.ObjectModel;
using System.Text.Json;
using CluadeX.Helpers;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Tools;

namespace CluadeX.ViewModels;

/// <summary>
/// Very hard tasks to Claude Fable 5.2 (owner 2026-11-10). Three doors, none of them automatic:
/// <list type="bullet">
/// <item>the agent's <c>new_chat</c> with model "fable" — a new chat that starts from a focused brief (FableBrief), never a
/// fork of this one, runs on Fable (its own model, kept in chat-links.json) and sends its short report back here;</item>
/// <item>the agent's <c>agent_spawn</c> with model "fable" — one part thought through by a Fable sub-agent
/// (CodeAgentService.RunFableSubAgentAsync);</item>
/// <item>CluadeX's own card when a run looks stuck (DispatchPolicy.AssessHardTask) — "ส่งให้ Fable ในแชทใหม่" with the
/// price, and buttons to send it or not.</item>
/// </list>
/// The first two run only when the owner asked in words this turn or said yes on the card (FableGate), and the spend
/// guards still decide (DispatchPolicy.FableBudgetRefusal).
/// </summary>
public partial class ChatViewModel
{
    /// <summary>The longest Fable report the parent chat gets back (it was asked for ≤ 300 words).</summary>
    private const int FableReportChars = 2400;

    /// <summary>A brief's size before it is built, for the card's estimate (FableBrief's bound is ~3K tokens).</summary>
    private const int FableBriefTokensGuess = 3000;

    /// <summary>Chats the card was offered in this app run: once each (a "no" is not asked again on every turn).</summary>
    private readonly HashSet<string> _fableOffered = new(StringComparer.Ordinal);

    /// <summary>The spend guards refused a Fable chat: the card says so in the owner's language, with the estimate.</summary>
    private sealed class FableRefusedException : InvalidOperationException
    {
        public DispatchPolicy.FableEstimate Estimate { get; }
        public FableRefusedException(string message, DispatchPolicy.FableEstimate estimate) : base(message) => Estimate = estimate;
    }

    /// <summary>Called from the constructor, beside new_chat's own handler.</summary>
    private void InitFable()
    {
        // The asking chat is read on the tool's own flow (its run scope), before the hop to the UI thread.
        ProjectToolModule.ModelChatHandler = req =>
        {
            string? parentId = FlowRun?.Session.Id ?? req.AskingChat;
            return App.Current.Dispatcher.InvokeAsync(() => OpenFableChatAsync(req, parentId)).Task.Unwrap();
        };
    }

    // ─── a chat on its own model ───

    /// <summary>
    /// A run starts in <paramref name="session"/>: a chat with a model of its own (a Fable chat) goes out on it, with
    /// effort high and a task budget. Always set, so a run never inherits another chat's model from the flow. A provider
    /// that cannot run it (only the Anthropic API and Claude Dev can) leaves the app's pick, and the chat says so.
    /// </summary>
    private void ApplyChatModel(ChatRun run, ChatSession session, ObservableCollection<ChatMessage> messages)
    {
        string? own = session.OwnModel;
        bool can = own != null && _providerManager.ActiveProviderType is AiProviderType.Anthropic or AiProviderType.ClaudeDev;
        bool fable = can && DispatchPolicy.IsFable(own);
        SubAgentModel.UseChat(can ? own : null);
        SubAgentModel.UseTaskBudget(fable ? DispatchPolicy.FableChatTaskBudget : null);
        if (fable) run.Scope.EffortCap = DispatchPolicy.FableEffort;
        if (own != null && !can)
            messages.Add(new ChatMessage
            {
                Role = MessageRole.System,
                Content = "🧠 " + LocF("fable.chat.providerOff", _providerManager.ActiveProviderType),
            });
        if (own != null) _debugLog?.Info("Fable", $"chat {session.Id} runs on {(can ? own : "the app's pick")} · provider {_providerManager.ActiveProviderType}");
    }

    // ─── new_chat model "fable" (and the card's yes) ───

    /// <summary>
    /// A new chat on Fable from a focused brief of <paramref name="parentId"/>'s task (UI thread). Throws with the reason
    /// when it cannot (the tool fails with it; the card shows it): no asking chat, a provider without Fable, or the spend
    /// guards (<see cref="FableRefusedException"/>). Returns the tool's report.
    /// </summary>
    private async Task<string> OpenFableChatAsync(ProjectToolModule.ModelChatRequest req, string? parentId)
    {
        var parent = Sessions.FirstOrDefault(s => s.Id == parentId)
                     ?? throw new InvalidOperationException("no chat to send the task from");
        if (_agentService.FableUnavailable() is { } unavailable) throw new InvalidOperationException(unavailable);
        string model = DispatchPolicy.FableIdOf(req.Model) ?? DispatchPolicy.FableModel;

        // The asking chat's rows: its live list (running or on screen), else what is stored. Read, never copied over.
        List<ChatMessage> history = LiveListOf(parent.Id)?.ToList()
                                    ?? _persistenceService.LoadSession(parent.Id)?.Messages?.ToList()
                                    ?? new List<ChatMessage>();
        string? folder = req.Path ?? (string.IsNullOrWhiteSpace(parent.ProjectPath) ? _fileSystemService.WorkingDirectory : parent.ProjectPath);
        string git = "";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            git = await AgentHandoff.GitSnapshotAsync(folder, cts.Token);
        }
        catch (Exception ex) { _debugLog?.Warn("Fable", "git snapshot for the brief failed: " + ex.Message); }

        string brief = FableBrief.Compose(req.Problem, req.Files, req.Tried, req.DoneWhen, history, folder,
            parent.ExtraFolders.ToList(), git);
        int briefTokens = TokenBudget.EstimateTokens(brief);
        var est = DispatchPolicy.EstimateFable(briefTokens, _agentService.FableBaseTokens(subAgent: false), DispatchPolicy.FableChatTaskBudget);
        var (refusal, warning) = _agentService.FableBudgetCheck(est, freshRun: true);
        if (refusal != null)
        {
            _debugLog?.Info("Fable", $"new chat refused by the spend guards · {refusal}");
            throw new FableRefusedException(refusal, est);
        }

        int historyTokens = history.Sum(m => TokenBudget.EstimateTokens(m.Content) + TokenBudget.EstimateTokens(m.ToolOutput));
        decimal forkUsd = DispatchPolicy.ForkWriteUsd(historyTokens);
        string title = string.IsNullOrWhiteSpace(req.Title) ? "Fable · " + (FirstLineOf(req.Problem) ?? "task") : req.Title!.Trim();
        string note = "🧠 " + LocF("fable.chat.opened", DispatchPolicy.FableChatTaskBudget, briefTokens, est.LowUsd, est.HighUsd, parent.Title);
        _debugLog?.Info("Fable", $"new chat from {parent.Id} · brief ~{briefTokens} tokens (fork would be ~{historyTokens}, ${forkUsd:0.00} of writes)"
                                 + $" · est ${est.LowUsd:0.00}–${est.HighUsd:0.00}");

        string opened = OpenModelChatForAgent(req.Path, brief, title, parent.Id, fork: false, model, note);
        return opened
             + $" It runs on Claude Fable 5.2 (effort {DispatchPolicy.FableEffort}, task budget {DispatchPolicy.FableChatTaskBudget:N0} tokens) from a focused "
             + $"brief of ~{briefTokens:N0} tokens, not this chat's history (a fork would have written ~{historyTokens:N0} tokens, ~${forkUsd:0.00}, "
             + $"to cache at once). Estimated ${est.LowUsd:0.00}–${est.HighUsd:0.00}. When it ends its turn its short report comes back "
             + "here as a notice."
             + (warning != null ? " " + warning : "");
    }

    // ─── the card: CluadeX's own offer when a run looks stuck ───

    /// <summary>
    /// The turn ended in a run that looks stuck (DispatchPolicy.AssessHardTask: its own fixes kept the build red and the
    /// speculative fixers failed too, or the loop guard stopped it, or the owner's words say the task is layered / spans
    /// repos / must not go wrong): a card offers to send it to Fable in a new chat, with the price. It never switches by
    /// itself. Not after a Stop, not twice in a chat, not when the owner already asked for Fable (the model has that).
    /// </summary>
    private void MaybeOfferFable(ChatRun run)
    {
        try
        {
            var session = run.Session;
            if (run.Stopped || run.Scope == null || _fableOffered.Contains(session.Id)) return;
            if (_agentService.FableUnavailable() != null) return;
            if (FableGate.Asked(session.Id) != DispatchPolicy.FableRoute.None) return;
            var verdict = DispatchPolicy.AssessHardTask(
                _agentService.HardSignalsOf(run.Scope, FableGate.OwnerWordsOf(session.Id)),
                session.OwnModel ?? _agentService.RunModelForVerdict());
            if (!verdict.Hard) return;

            var est = DispatchPolicy.EstimateFable(FableBriefTokensGuess, _agentService.FableBaseTokens(subAgent: false),
                DispatchPolicy.FableChatTaskBudget);
            int historyTokens = run.Messages.Sum(m => TokenBudget.EstimateTokens(m.Content) + TokenBudget.EstimateTokens(m.ToolOutput));
            string why = Loc("fable.lang") == "th" ? verdict.WhyTh : verdict.Why;
            string recap = LocF("fable.offer.recap", why, est.LowUsd, est.HighUsd, DispatchPolicy.ForkWriteUsd(historyTokens));
            string ask = Loc("fable.offer.ask");
            string[] actions = [Loc("fable.offer.yes"), Loc("fable.offer.no")];
            run.Messages.Add(new ChatMessage
            {
                Role = MessageRole.System,
                ToolName = ChatMessage.NextStepTag,
                // NextStep.FromJson reads recap/ask/actions; "fable" marks the card as CluadeX's own (kept with the chat).
                ToolOutput = JsonSerializer.Serialize(new { recap, ask, actions, fable = new { why = verdict.Why } }),
                Content = "🧠 " + recap + "\n" + ask,
            });
            _fableOffered.Add(session.Id);
            _debugLog?.Info("Fable", $"offered in {session.Id} · {verdict.Why} · est ${est.LowUsd:0.00}–${est.HighUsd:0.00}");
        }
        catch (Exception ex) { _debugLog?.Warn("Fable", "the Fable card failed: " + ex.Message); }
    }

    /// <summary>A Fable card's row: why it was offered and its buttons (yes first), or null for any other box.</summary>
    private static (string Why, string Yes)? FableOfferOf(ChatMessage row)
    {
        if (row.ToolName != ChatMessage.NextStepTag || row.ToolOutput is not { Length: > 0 } json || !json.Contains("\"fable\"")) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("fable", out var f) || f.ValueKind != JsonValueKind.Object) return null;
            string why = f.TryGetProperty("why", out var w) ? w.GetString() ?? "" : "";
            string yes = r.TryGetProperty("actions", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0
                ? a[0].GetString() ?? "" : "";
            return (why, yes);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A button on the Fable card: yes opens the Fable chat from a focused brief (the owner's yes is the
    /// permission); no leaves the work here. Neither goes to the model as the owner's message.</summary>
    private async void OnFableOfferPicked(ChatMessage row, string label, (string Why, string Yes) offer)
    {
        if (row.ToolSummary is { Length: > 0 }) return;   // picked once already
        row.ToolSummary = label;
        var chat = Sessions.FirstOrDefault(s => LiveListOf(s.Id)?.Contains(row) == true) ?? CurrentSession;
        if (chat == null) return;
        if (label != offer.Yes)
        {
            AddNotice(chat.Id, "🧠 " + Loc("fable.offer.declined"),
                "The user declined sending this task to Claude Fable 5.2. Keep the work on this model; do not offer Fable again.");
            return;
        }
        try
        {
            FableGate.Grant(chat.Id);
            var rows = LiveListOf(chat.Id)?.ToList() ?? _persistenceService.LoadSession(chat.Id)?.Messages?.ToList() ?? new();
            string? first = rows.FirstOrDefault(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Content))?.Content;
            string? latest = rows.LastOrDefault(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Content))?.Content;
            string task = (latest ?? first ?? chat.Title).Trim();
            if (first != null && latest != null && first != latest)
                task += "\n\n(The chat's first request: " + (first.Length > 1500 ? first[..1500] + "…" : first.Trim()) + ")";
            string problem = task + "\n\nThe model on this chat could not finish it: " + offer.Why + ".";
            await OpenFableChatAsync(new ProjectToolModule.ModelChatRequest(null, problem, null, DispatchPolicy.FableModel,
                null, null, null, chat.Id), chat.Id);
        }
        catch (FableRefusedException ex)
        {
            AddNotice(chat.Id, "🧠 " + LocF("fable.offer.overBudget", ex.Estimate.LowUsd, ex.Estimate.HighUsd), ex.Message);
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("Fable", "the card's yes failed: " + ex.Message);
            AddNotice(chat.Id, "🧠 " + LocF("fable.offer.failed", ex.Message), "Sending the task to Claude Fable 5.2 failed: " + ex.Message);
        }
    }

    // ─── the model button ───

    /// <summary>The chat on screen runs on a model of its own that this provider can run (null = the app's pick).</summary>
    private string? ShownOwnModel =>
        CurrentSession?.OwnModel is { Length: > 0 } own && _providerManager.ActiveProviderType is AiProviderType.Anthropic or AiProviderType.ClaudeDev
            ? own : null;

    /// <summary>The owner picked a model in the picker: a chat with a model of its own goes back to the owner's pick.</summary>
    private void DropOwnModelOnPick()
    {
        if (CurrentSession is not { OwnModel: not null } chat) return;
        if (ChatLinkStore.Current?.SetModel(chat.Id, null) == true)
        {
            chat.RaiseSplitChanged();
            _debugLog?.Info("Fable", $"chat {chat.Id} goes back to the picked model");
        }
    }
}
