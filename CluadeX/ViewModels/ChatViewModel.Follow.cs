using System.IO;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// What the window shows while several chats run at once.
/// <list type="bullet">
/// <item>"ตามดูโค้ด" — a tick per chat: when that chat's agent edits a file, the Code page comes forward with the file
/// open at the edit (owner 2026-10-28: "การติ๊ก ตามดูโค๊ด เมื่อมีการแก้โค๊ด ควรเด้งไปหน้า editor"). Without the tick the
/// editor still opens the file behind the page the user is on (Settings: LiveEditFollow) and the screen stays put — the
/// default for a chat nobody ticked is Settings' AutoOpenEditorOnAgentEdit, off since 2026-10-20 ("ไม่จำเป็นต้องสลับไปดู
/// แบบ real time อัตโนมัติแล้ว"). Only the chat on screen ever moves the editor, and only for files of the project the
/// window has open. Owner 2026-10-17: "ถ้ามีหลายงานทำพร้อมกัน การสลับไปหน้า editor จะไว้และอาจมั่ว ให้มีการติ๊กในช่องแชทได้ว่า
/// จะให้สลับหน้าต่างเองอัตโนมัติหรือไม่".</item>
/// <item>The call being written: while the model streams a long tool input (a 300-line write_file takes a minute),
/// the chat showed only the half sentence before it. A status row now says which tool, which file and how much is
/// written, and gives way to the tool's own row when it runs.</item>
/// </list>
/// </summary>
public partial class ChatViewModel
{
    /// <summary>Chats whose tick the owner changed; the rest follow Settings (AutoOpenEditorOnAgentEdit). Session-only,
    /// like Batch.</summary>
    private readonly Dictionary<string, bool> _followChoice = new();

    private bool FollowsEdits(ChatSession? s) =>
        s != null && _followChoice.TryGetValue(s.Id, out bool v) ? v : _settingsService.Settings.AutoOpenEditorOnAgentEdit;

    /// <summary>Raised (on the UI thread) after <see cref="FileMutatedByAgent"/> when the chat's "ตามดูโค้ด" tick is on:
    /// bring the Code page forward.</summary>
    public event Action? ShowEditorRequested;

    /// <summary>The tick in the composer: this chat's edits bring the editor forward while it is on screen.</summary>
    public bool FollowEditsHere
    {
        get => FollowsEdits(CurrentSession);
        set
        {
            if (CurrentSession is not { } s || FollowsEdits(s) == value) return;
            _followChoice[s.Id] = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Should an agent edit move the editor? Only for the chat on screen, when it follows edits (its tick, or
    /// Settings' LiveEditFollow for the editor behind the page), and only when the run works in the project the window
    /// has open (another project's file would sit under this one's title).</summary>
    public static bool ShouldFollowEdit(bool chatOnScreen, bool ticked, string? runFolder, string? windowFolder) =>
        chatOnScreen && ticked
        && !string.IsNullOrWhiteSpace(windowFolder)
        && (string.IsNullOrWhiteSpace(runFolder) || SameFolder(runFolder, windowFolder));

    public static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Is <paramref name="fullPath"/> the folder itself or inside it (not merely a name that starts the same)?</summary>
    public static bool IsInsideFolder(string fullPath, string folder)
    {
        try
        {
            string f = Path.GetFullPath(folder).TrimEnd('\\', '/');
            string p = Path.GetFullPath(fullPath);
            return string.Equals(p.TrimEnd('\\', '/'), f, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(f + '/', StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ─── the screen belongs to the user ───
    // Owner 2026-10-17: "เพิ่มไม่ให้การสลับหน้าต่างมั่วเกินไประหว่างโปรเจค คุณต้องมีระบบป้องกัน". One rule: the window shows
    // the project of the chat on screen (title, tree, editor). Work that arrives from outside (a pipe task) may bring its
    // chat on screen only when the user is not using the window; otherwise it runs in its own chat in the background
    // (sidebar badge, status bar) and the screen does not move. At most one such take-over per ScreenTakeCooldown.

    /// <summary>The user typed or switched chats this recently: they are using the window.</summary>
    public static readonly TimeSpan UserActiveWindow = TimeSpan.FromSeconds(45);
    /// <summary>At most one automatic take-over of the screen in this long.</summary>
    public static readonly TimeSpan ScreenTakeCooldown = TimeSpan.FromSeconds(90);

    private DateTime _lastUserActivityUtc = DateTime.MinValue;
    private DateTime _lastScreenTakeUtc = DateTime.MinValue;

    private void NoteUserActivity() => _lastUserActivityUtc = DateTime.UtcNow;

    /// <summary>May work that arrives from outside bring its chat on screen now?</summary>
    public static bool MayTakeScreen(DateTime nowUtc, DateTime lastUserActivityUtc, DateTime lastTakeUtc, bool screenChatRunning) =>
        !screenChatRunning
        && nowUtc - lastUserActivityUtc >= UserActiveWindow
        && nowUtc - lastTakeUtc >= ScreenTakeCooldown;

    private bool IsOnScreen(ChatSession? s) =>
        s == null || ReferenceEquals(s, CurrentSession) || (CurrentSession != null && s.Id == CurrentSession.Id);

    // ─── the call being written ───

    /// <summary>A tool input shorter than this arrives before anyone could read a line about it: no row.</summary>
    public const int DraftShowChars = 600;

    private void OnToolDrafting(ChatRun run, string tool, int chars, string? path)
    {
        if (chars < DraftShowChars) return;
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_runs.Contains(run)) return;
            string text = DraftText(tool, chars, path);
            if (run.DraftRow is { } row) { row.Content = text; return; }
            run.DraftRow = new ChatMessage { Role = MessageRole.AgentStatus, Content = text, IsStreaming = true, ToolKind = tool };
            run.Messages.Add(run.DraftRow);
            if (IsOnScreen(run.Session)) ScrollToBottom?.Invoke();
        });
    }

    /// <summary>"⏵ เขียนไฟล์ unpaid_bills.dart — กำลังเขียน 3,200 ตัวอักษร…"</summary>
    private string DraftText(string tool, int chars, string? path)
    {
        var (_, verbKey) = ToolLook.Of(tool);
        string verb = verbKey == "tool.verb.mcp" ? (ToolLook.McpServer(tool) ?? Loc(verbKey)) : Loc(verbKey);
        string target = string.IsNullOrWhiteSpace(path) ? "" : " " + Path.GetFileName(path.TrimEnd('\\', '/'));
        return "⏵ " + LocF("chat.tool.drafting", verb + target, chars.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The draft row gives way: the tool is starting (its own row follows), or the turn ended.</summary>
    private static void DropDraft(ChatRun run)
    {
        if (run.DraftRow is not { } row) return;
        run.DraftRow = null;
        run.Messages.Remove(row);
    }
}
