using System.Reflection;
using CluadeX.Models;
using CluadeX.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>SI — opening a chat keeps the sidebar's own row (owner 2026-11-02): it was swapped for the database copy,
    /// so a pinned chat fell out of Pinned and its related folders were lost. And the context count is not redone for
    /// rows that did not change.</summary>
    static async Task TestSidebarIdentity()
    {
        Log("\n## SI — opening a chat keeps its sidebar row (pin, related folders); the context count is cached per row");
        await FreshChat();

        // A stored chat, in the sidebar as the start-up list loads it: a header-only row, no messages.
        var stored = new ChatSession { Title = "si pinned chat", ProjectPath = Vm.WorkingDirectory ?? "", UpdatedAt = DateTime.Now.AddDays(-3) };
        stored.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "si hello" });
        stored.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "si hi there" });
        Persist.SaveSession(stored);
        var row = new ChatSession
        {
            Id = stored.Id, Title = "si stale title", ProjectPath = stored.ProjectPath,
            CreatedAt = stored.CreatedAt, UpdatedAt = stored.UpdatedAt,
        };
        Vm.Sessions.Add(row);
        Vm.TogglePinCommand.Execute(row.Id);
        row.ExtraFolders.Add(@"C:\si-related-folder");
        await Render();
        Check("SI (before) the row is pinned, first in the list", row.IsPinned && Vm.VisibleSessions.IndexOf(row) == 0,
            $"pinned={row.IsPinned} at={Vm.VisibleSessions.IndexOf(row)}");

        await Open(row.Id);
        var inList = Vm.Sessions.Where(s => s.Id == row.Id).ToList();
        Check("SI1 the opened chat IS the sidebar's row — the same object, once in the list",
            ReferenceEquals(Vm.CurrentSession, row) && inList.Count == 1 && ReferenceEquals(inList[0], row) && Vm.VisibleSessions.Contains(row),
            $"same={ReferenceEquals(Vm.CurrentSession, row)} copies={inList.Count}");
        Check("SI2 it stays pinned: first in the list, in the pinned group",
            row.IsPinned && Vm.VisibleSessions.IndexOf(row) == 0 && row.DateGroupLabel == L("sidebar.group.pinned"),
            $"pinned={row.IsPinned} at={Vm.VisibleSessions.IndexOf(row)} group={row.DateGroupLabel}");
        Check("SI3 its related folders are kept", row.ExtraFolders.SequenceEqual([@"C:\si-related-folder"]), string.Join(", ", row.ExtraFolders));
        Check("SI4 the stored rows and title are read onto it",
            Vm.Messages.Count == 2 && row.Messages.Count == 2 && row.Title == "si pinned chat",
            $"screen={Vm.Messages.Count} row={row.Messages.Count} title={row.Title}");

        // The 5 s tick recounts only when the rows changed: a new row marks the count, the debounced count clears it.
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.System, Content = "si a new row" });
        bool marked = Field<bool>(Vm, "_ctxDirty");
        bool cleared = await WaitUntil(() => !Field<bool>(Vm, "_ctxDirty"), 3000);
        Check("SI5 a new row marks the context count; the count clears it, so the idle tick skips it", marked && cleared,
            $"marked={marked} cleared={cleared}");

        // Each row's estimate is kept with the strings it was counted from, and redone only when one is replaced.
        var ctx = Sp.GetRequiredService<ContextMemoryService>();
        var cacheField = typeof(ChatMessage).GetField("ContextTokens", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var msg = new ChatMessage { Role = MessageRole.ToolAction, Content = "read_file a.cs", ToolOutput = string.Join("\n", Enumerable.Repeat("int x = 1;", 200)) };
        int first = ctx.CalculateTotalTokens([msg]);
        var cache1 = cacheField.GetValue(msg);
        int again = ctx.CalculateTotalTokens([msg]);
        var cache2 = cacheField.GetValue(msg);
        Check("SI6 a row is counted once: the second count reuses it", first > 0 && again == first && cache1 != null && ReferenceEquals(cache1, cache2),
            $"first={first} again={again} reused={ReferenceEquals(cache1, cache2)}");
        msg.ToolOutput += "\nint y = 2;";
        int grown = ctx.CalculateTotalTokens([msg]);
        msg.Content += " and more words here";
        int longer = ctx.CalculateTotalTokens([msg]);
        Check("SI7 a changed ToolOutput or Content is counted again", grown > first && longer > grown, $"first={first} grown={grown} longer={longer}");

        Vm.TogglePinCommand.Execute(row.Id);
        await FreshChat();
    }
}
