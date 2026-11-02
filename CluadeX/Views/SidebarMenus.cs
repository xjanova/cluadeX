using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>
/// The sidebar's right-click menus and its drag-a-chat-onto-a-project, built in code like the window's other small menus
/// (the project list changes, so the "Move to project" submenu is made when it opens). MainWindow's handlers only call
/// these; the harness builds the same menus and clicks their items.
/// </summary>
public static class SidebarMenus
{
    /// <summary>The data format a dragged chat row carries (its id).</summary>
    public const string ChatIdFormat = "CluadeX.ChatId";

    private static string L(string key) => LocalizedResources.Instance[key];

    private static MenuItem Item(string header, Action click, bool enabled = true, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, IsCheckable = isChecked, IsChecked = isChecked };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>A chat row's menu: pin, rename, move to a project (or out, or into a new one), delete.</summary>
    public static ContextMenu ForChat(ChatViewModel vm, ChatSession chat)
    {
        var menu = new ContextMenu();
        string id = chat.Id;
        menu.Items.Add(Item(L(chat.IsPinned ? "sidebar.unpinTip" : "sidebar.pinTip"), () => vm.TogglePinCommand.Execute(id)));
        menu.Items.Add(Item(L("sidebar.rename"), () => vm.RenameChatCommand.Execute(id)));

        var move = new MenuItem { Header = L("sidebar.project.moveTo") };
        foreach (var p in vm.ChatProjects)
        {
            string pid = p.Id;
            move.Items.Add(Item(p.Name, () => vm.MoveChatToProject(id, pid), isChecked: chat.ProjectId == pid));
        }
        if (vm.ChatProjects.Count > 0) move.Items.Add(new Separator());
        bool inProject = vm.ChatProjects.Any(p => p.Id == chat.ProjectId);
        move.Items.Add(Item(L("sidebar.project.moveOut"), () => vm.MoveChatToProject(id, ""), enabled: inProject));
        move.Items.Add(Item(L("sidebar.project.moveToNew"), () =>
        {
            if (vm.AskNewProject() is { } created) vm.MoveChatToProject(id, created.Id);
        }));
        menu.Items.Add(move);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L("sidebar.delete"), () => vm.DeleteSessionCommand.Execute(id)));
        return menu;
    }

    /// <summary>A project row's menu: a new chat in it, rename, bind / unbind a folder, delete.</summary>
    public static ContextMenu ForProject(ChatViewModel vm, ChatProject project)
    {
        var menu = new ContextMenu();
        string id = project.Id;
        menu.Items.Add(Item(L("sidebar.project.newChat"), () => vm.NewChatInProject(id)));
        menu.Items.Add(Item(L("sidebar.project.rename"), () => vm.RenameProjectCommand.Execute(id)));
        menu.Items.Add(Item(L("sidebar.project.bindFolder"), () => vm.BindProjectFolderCommand.Execute(id)));
        menu.Items.Add(Item(L("sidebar.project.unbindFolder"), () => vm.SetProjectFolder(id, ""), enabled: project.HasFolder));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L("sidebar.project.delete"), () => vm.DeleteProject(id)));
        return menu;
    }

    /// <summary>Opens <paramref name="menu"/> at the mouse over <paramref name="target"/>.</summary>
    public static void Open(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // ─── drag a chat onto a project ───

    private static Point? _pressAt;
    private static string? _pressChat;

    /// <summary>The left button went down on a chat row: a drag may start from here.</summary>
    public static void ChatRowPressed(FrameworkElement row, MouseButtonEventArgs e)
    {
        _pressAt = e.GetPosition(row);
        _pressChat = (row.DataContext as ChatSession)?.Id;
    }

    /// <summary>The mouse moved on a chat row: past the system's drag distance with the button held, the chat is dragged.</summary>
    public static void ChatRowMoved(FrameworkElement row, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressAt is not { } at || _pressChat is not { } id) return;
        var now = e.GetPosition(row);
        if (Math.Abs(now.X - at.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - at.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressAt = null;
        _pressChat = null;
        DragDrop.DoDragDrop(row, new DataObject(ChatIdFormat, id), DragDropEffects.Move);
    }

    /// <summary>A drag over a project row ("" = the "No project" row): it takes a chat.</summary>
    public static void ProjectRowDragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(ChatIdFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>A chat dropped on project <paramref name="projectId"/> ("" = out of any project) moves there.</summary>
    public static void ProjectRowDrop(ChatViewModel vm, string? projectId, DragEventArgs e)
    {
        if (e.Data.GetData(ChatIdFormat) is string chatId) vm.MoveChatToProject(chatId, projectId ?? "");
        e.Handled = true;
    }
}
