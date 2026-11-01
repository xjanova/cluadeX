using System.Collections;
using System.IO;
using System.Net.Http;
using System.Reflection;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.UiStudio;
using CluadeX.Services.UiStudio.Pages;
using CluadeX.ViewModels;
using CluadeX.Views.UiStudio;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>CP — the UI Studio and the Node Graph show the project of the chat on screen (owner 2026-11-01: "เราอยู่แชทไหน
    /// ต้องแสดงของแชทนั้น จะเปลี่ยนต่อเมื่อสลับไปแชทใหม่ทันที"): they change the moment the chat changes and at no other time,
    /// a chat with no project shows nothing of the last one, and nothing late from a project left lands on the next.</summary>
    static async Task TestProjectPagesFollowChat()
    {
        Log("\n## CP — UI Studio and Node Graph show the project of the chat on screen");
        string root = Path.Combine(PipeRoot, "cp");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string Site(string name, string only)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "index.html"), $"<html><body>\n<h1>{name}</h1>\n<script src=\"{only}\"></script>\n</body></html>\n");
            File.WriteAllText(Path.Combine(dir, only), $"console.log('{name}');\n");
            return dir;
        }
        string a = Site("alpha", "alpha-only.js"), b = Site("beta", "beta-only.js"), c = Site("gamma", "gamma-only.js");

        var ui = Sp.GetRequiredService<UiStudioViewModel>();
        var graph = Sp.GetRequiredService<NodeGraphViewModel>();
        var studio = Sp.GetRequiredService<StudioService>();
        var fs = Sp.GetRequiredService<FileSystemService>();
        string folderBefore = Vm.WorkingDirectory;

        ChatSession Chat(string title, string project)
        {
            var chat = new ChatSession { Title = title, ProjectPath = project, UpdatedAt = DateTime.Now };
            chat.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "work on " + title });
            Persist.SaveSession(chat);
            Vm.Sessions.Insert(0, chat);
            return chat;
        }
        bool StudioOn(string p) => p.Length == 0 ? ui.NoProject && !studio.HasProject && ui.ProjectName.Length == 0 : ui.HasProject && SamePath(ui.ProjectPath, p);
        bool GraphHas(string file) => graph.Graph?.ById.ContainsKey(file) == true;
        async Task<bool> GraphShows(string file, string notFile)
        {
            await WaitUntil(() => !graph.IsBusy && GraphHas(file));
            return GraphHas(file) && !GraphHas(notFile);
        }
        var chatA = Chat("cp alpha", a);
        var chatB = Chat("cp beta", b);
        var chatNone = Chat("cp no project", "");

        int moved = 0;
        Action onMoved = () => moved++;
        studio.ProjectChanged += onMoved;
        var navs = new List<string>();
        Action<string> onNav = u => navs.Add(u);
        studio.NavigateRequested += onNav;
        graph.IsVisible = true;

        // ── CP1: every switch, both pages, at once ──
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int wrong = 0;
        string detail = "";
        for (int i = 0; i < 6; i++)
        {
            var (chat, project, own, other) = i % 2 == 0 ? (chatA, a, "alpha-only.js", "beta-only.js") : (chatB, b, "beta-only.js", "alpha-only.js");
            Vm.LoadSessionCommand.Execute(chat.Id);
            // the same tick as the switch: no timer in between shows the last project under this chat
            bool now = Vm.CurrentSession?.Id == chat.Id && StudioOn(project) && SamePath(Vm.ChatProject, project) && !GraphHas(other);
            bool drawn = await GraphShows(own, other);
            if (!now || !drawn || !SamePath(graph.RootName, Path.GetFileName(project))) { wrong++; detail += $" #{i}: now={now} drawn={drawn} studio={ui.ProjectName} graph={graph.RootName}"; }
        }
        Check("CP1 six switches between two projects: the UI Studio is on the chat's project in the same tick, the graph drops the old one at once and draws the chat's", wrong == 0, detail);
        Log($"  (six switches with graph builds: {sw.ElapsedMilliseconds} ms)");
        await WaitUntil(() => studio.IsServing);
        Check("CP1 the preview of the chat's project comes up by itself (a static site)", studio.IsServing && studio.CurrentBaseUrl != null, ui.ProjectKindText);
        using (var http = new HttpClient())
            Check("CP1 …and it serves THAT project's page", (await http.GetStringAsync(studio.CurrentBaseUrl + "index.html")).Contains("beta"));
        Check("CP1 each project left sends the browser to a blank page, so no page stays under another project's name", navs.Count(u => u == StudioService.BlankPage) >= 6, string.Join(",", navs.Take(4)));

        // ── CP2: a chat with no project ──
        ui.AiRequest = "make the header red";
        ui.NotePage("http://localhost:1/x.html");
        await Open(chatNone.Id);
        await Render();
        Check("CP2 a chat with no project: the UI Studio is empty, nothing of the last project remains",
            StudioOn("") && studio.PageUrl == null && !studio.IsServing && ui.Url == "" && ui.AiRequest == "" && ui.Layers.Count == 0, $"{ui.ProjectName} {studio.PageUrl} {ui.Url}");
        Check("CP2 …the graph is empty too, and says why", graph.Graph == null && graph.RootName == "" && !graph.IsBusy && graph.EmptyText == L("graph.noProject"), $"{graph.RootName} / {graph.EmptyText}");
        var th = new LocalizationService();
        th.SetLanguage("th");
        Check("CP2 the words exist in Thai", th.T("studio.empty.noChatProject").Contains("แชทนี้") && th.T("graph.noProject").Contains("แชทนี้"), th.T("graph.noProject"));
        graph.Mode = "live";
        Check("CP2 the graph's live view says the same, not 'open it in the Studio'", graph.EmptyText == L("graph.noProject"), graph.EmptyText);
        graph.Mode = "project";
        await Open(chatB.Id);
        Check("CP2 back in a chat with a project it shows again", StudioOn(b) && await GraphShows("beta-only.js", "alpha-only.js"));
        string gone = Path.Combine(root, "gone");
        Directory.CreateDirectory(gone);
        var chatGone = Chat("cp folder gone", gone);
        Directory.Delete(gone);
        await Open(chatGone.Id);
        Check("CP2 a chat whose folder was deleted counts as no project", StudioOn(""), ui.ProjectPath);
        Vm.NewSession(keepProject: false);
        await Render();
        Check("CP2 the user's New Chat starts with no project: empty, not the last chat's", StudioOn("") && Vm.ChatProject == "");
        Vm.SetWorkingDirectory(c);
        await Render();
        Check("CP2 opening a folder for it: it is this chat's project, both pages follow", StudioOn(c) && await GraphShows("gamma-only.js", "beta-only.js"));

        // ── CP3: nothing but the chat on screen moves them ──
        await Open(chatB.Id);
        await GraphShows("beta-only.js", "alpha-only.js");
        moved = 0;
        var offScreen = Vm.Sessions.First(s => s.Id == chatA.Id);
        offScreen.ProjectPath = c;   // what a run in that chat does with open_project
        await Task.Delay(400);
        Check("CP3 a run in another chat changing ITS project moves nothing here", moved == 0 && StudioOn(b), $"{moved} {ui.ProjectPath}");
        offScreen.ProjectPath = a;
        Persist.SaveSession(offScreen);
        fs.WorkingDirectory = c;     // the app's folder moved by anything but a chat switch
        await Task.Delay(400);
        Check("CP3 the app's working folder moving moves nothing here: the pages read the chat, not the folder", moved == 0 && StudioOn(b), $"{moved} {ui.ProjectPath}");
        fs.WorkingDirectory = b;

        ScreenFree();
        Check("CP3 the graph on screen counts as the user looking", Vm.ProjectPageOnScreen);
        var gate = new SemaphoreSlim(0);
        Fake.Script.Clear();
        Fake.Script.Enqueue(FakeStep.Gated(["Working"], gate, [" — done quietly."]));
        var mine = Vm.CurrentSession;
        var bgRun = Task.Run(() => Vm.RunMcpTaskAsync("cp-bg", "Do it quietly.", null, null, a, CancellationToken.None, maxSteps: 3));
        await WaitUntil(() => Vm.Sessions.Any(s => s.Title == "cp-bg" && s.RunStatus is "working" or "waiting"), 20000);
        await Render();
        Check("CP3 a pipe task while the Studio or the graph is on screen (nobody typing): its own chat in the background, the pages do not move",
            ReferenceEquals(Vm.CurrentSession, mine) && moved == 0 && StudioOn(b), $"{Vm.CurrentSession?.Title} {moved} {ui.ProjectPath}");
        gate.Release();
        await Task.WhenAny(bgRun, Task.Delay(20000));
        await Render();
        Check("CP3 …and they still have not moved when it ends", ReferenceEquals(Vm.CurrentSession, mine) && moved == 0 && StudioOn(b) && SamePath(Vm.WorkingDirectory, b));

        graph.IsVisible = false;
        ScreenFree();
        Fake.Script.Clear();
        Fake.Script.Enqueue(FakeStep.Gated(["Working"], gate, [" — done on screen."]));
        var fgRun = Task.Run(() => Vm.RunMcpTaskAsync("cp-fg", "Do it where I can see.", null, null, a, CancellationToken.None, maxSteps: 3));
        await WaitUntil(() => Vm.CurrentSession?.Title == "cp-fg", 20000);
        await Render();
        Check("CP3 a task that took the screen IS a chat switch (nobody on those pages): they show that chat's project",
            Vm.CurrentSession?.Title == "cp-fg" && StudioOn(a), $"{Vm.CurrentSession?.Title} {ui.ProjectPath}");
        gate.Release();
        await Task.WhenAny(fgRun, Task.Delay(20000));
        await Render();
        graph.IsVisible = true;
        Fake.Script.Clear();

        await Open(chatB.Id);
        Vm.CurrentSession.ProjectPath = c;
        Check("CP3 the chat on screen getting another project (its own open_project): that is this chat's, they follow", StudioOn(c), ui.ProjectPath);
        Vm.CurrentSession.ProjectPath = b;
        Check("CP3 …and back", StudioOn(b));

        // ── CP4: a switch in the middle of work ──
        await GraphShows("beta-only.js", "alpha-only.js");
        await Open(chatA.Id);
        await WaitUntil(() => studio.IsServing);
        ui.NotePage("http://localhost:1/index.html");
        studio.NotePageLoaded();
        StudioElement Heading() => new() { NodeId = 7, Tag = "h1", Src = "index.html:2:1", SrcKind = "locator", Selector = "h1", Styles = new() { ["color"] = "black" } };
        var tree = new DomNode { NodeId = 1, Tag = "body" };
        studio.HandleOverlayMessage(new OverlayMessage { Type = "tree", Tree = tree });
        studio.HandleOverlayMessage(new OverlayMessage { Type = "select", Element = Heading() });
        var color = ui.StyleGroups.SelectMany(g => g.Fields).First(f => f.Prop == "color");
        Check("CP4 (control) the picked element can be edited on its own project", ui.HasSelection && ui.CanEditSource && ui.Layers.Count == 1);
        studio.Evaluate = _ => Task.FromResult("[]");
        color.Value = "red";
        ui.SaveCommand.Execute(null);
        string aHtml = Path.Combine(a, "index.html"), bHtml = Path.Combine(b, "index.html");
        Check("CP4 (control) a save with no switch lands in the project's own file", await WaitUntil(() => File.ReadAllText(aHtml).Contains("red"), 5000), File.ReadAllText(aHtml));

        string aText = File.ReadAllText(aHtml), bText = File.ReadAllText(bHtml);
        var answer = new TaskCompletionSource<string>();
        studio.Evaluate = _ => answer.Task;
        studio.HandleOverlayMessage(new OverlayMessage { Type = "select", Element = Heading() });
        color.Value = "blue";
        ui.SaveCommand.Execute(null);   // waiting for the browser to say the value is good
        await Open(chatB.Id);
        answer.SetResult("[]");
        await Render();
        Check("CP4 a save still waiting for the page when the chat switches writes nothing, to either project",
            File.ReadAllText(aHtml) == aText && File.ReadAllText(bHtml) == bText && !ui.HasSelection && !ui.IsDirty, File.ReadAllText(bHtml));
        studio.Evaluate = null;

        Check("CP4 the page in the browser counts as the last project's until a navigation lands", studio.PageIsOld);
        studio.HandleOverlayMessage(new OverlayMessage { Type = "select", Element = Heading() });
        studio.HandleOverlayMessage(new OverlayMessage { Type = "tree", Tree = tree });
        Check("CP4 a pick and a tree from that page are dropped: A's line numbers never point into B's files", !ui.HasSelection && ui.Layers.Count == 0 && studio.Tree == null);
        graph.Mode = "live";
        Check("CP4 …so the graph's live view does not show them either", graph.Graph == null, graph.Summary);
        graph.Mode = "project";
        studio.NotePageLoaded();
        studio.HandleOverlayMessage(new OverlayMessage { Type = "select", Element = new StudioElement { NodeId = 9, Tag = "h1", Src = "index.html:2:1", SrcKind = "locator" } });
        Check("CP4 once the new page has loaded, its picks count", ui.HasSelection);
        studio.HandleOverlayMessage(new OverlayMessage { Type = "deselect" });
        ui.NotePage(StudioService.BlankPage);
        Check("CP4 the blank page is no address", ui.Url == "" && studio.PageUrl == null, ui.Url);

        // the graph: a big project still being read when the chat moves
        string big = Path.Combine(root, "big");
        Directory.CreateDirectory(big);
        var imports = new System.Text.StringBuilder();
        for (int i = 0; i < 400; i++)
        {
            File.WriteAllText(Path.Combine(big, $"m{i}.js"), $"export const m{i} = {i};\n");
            imports.AppendLine($"import './m{i}.js';");
        }
        File.WriteAllText(Path.Combine(big, "big-only.js"), imports.ToString());
        File.WriteAllText(Path.Combine(big, "index.html"), "<script type=module src=big-only.js></script>");
        var chatBig = Chat("cp big", big);
        Vm.LoadSessionCommand.Execute(chatBig.Id);
        Vm.LoadSessionCommand.Execute(chatB.Id);   // the build of big has only just begun
        bool bigGone = await GraphShows("beta-only.js", "big-only.js");
        await Task.Delay(600);
        Check("CP4 a graph build of the project left never lands on the next", bigGone && !GraphHas("big-only.js") && !graph.IsBusy && SamePath(graph.RootName, "beta"), graph.Summary);

        // the graph: a drag whose save was still waiting
        await Open(chatA.Id);
        await GraphShows("alpha-only.js", "beta-only.js");
        var node = graph.Graph.ById["alpha-only.js"];
        node.X = 1234;
        node.Y = 567;
        graph.NodeMoved(node);
        await Open(chatB.Id);   // inside the second before it is saved
        await GraphShows("beta-only.js", "alpha-only.js");
        string Layout(string project) => (string)typeof(NodeGraphViewModel).GetMethod("LayoutFile", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(graph, [project]);
        await Task.Delay(1200);
        string aLayout = File.Exists(Layout(a)) ? File.ReadAllText(Layout(a)) : "", bLayout = File.Exists(Layout(b)) ? File.ReadAllText(Layout(b)) : "";
        Check("CP4 a dragged node's place goes to ITS project's layout, not the next one's", aLayout.Contains("alpha-only.js") && !bLayout.Contains("alpha-only.js"), $"{aLayout} / {bLayout}");
        await Open(chatA.Id);
        await GraphShows("alpha-only.js", "beta-only.js");
        Check("CP4 …and it is there on coming back", graph.Graph.ById["alpha-only.js"].X == 1234, graph.Graph.ById["alpha-only.js"].X.ToString());

        // a thumbnail that comes back after the chat moved
        await WaitUntil(() => studio.IsServing);
        var shot = new TaskCompletionSource<PageShot>();
        string shotUrl = null;
        ui.Shooter = (url, w, h, ct) => { shotUrl = url; return shot.Task; };
        ui.ShowPages = true;
        await WaitUntil(() => ui.PageRows.Count > 0);
        var cards = ui.PageRows.SelectMany(r => r.Cards).ToList();
        string pageId = cards.FirstOrDefault()?.Page.Id;
        ui.NoteVisible(cards);
        await WaitUntil(() => shotUrl != null, 5000);
        await Open(chatB.Id);
        Check("CP4 the page gallery of the project left is gone at once", !ui.ShowPages && ui.PageRows.Count == 0 && ui.ScreenCards.Count == 0);
        shot.SetResult(new PageShot([1, 2, 3], shotUrl ?? "http://localhost:1/", "late", 200, 1, 1));
        await Render();
        string data = Sp.GetRequiredService<SettingsService>().DataRoot;
        Check("CP4 a late thumbnail is saved to neither project", pageId != null && shotUrl != null
            && new PageShotStore(data, a).Get(pageId, "desktop") == null && new PageShotStore(data, b).Get(pageId, "desktop") == null, $"{pageId} {shotUrl}");
        ui.Shooter = null;

        // a preview still starting when the chat moves
        string vite = Path.Combine(root, "vite");
        Directory.CreateDirectory(Path.Combine(vite, "node_modules"));
        File.WriteAllText(Path.Combine(vite, "package.json"), "{\"name\":\"vite-app\",\"scripts\":{\"dev\":\"vite\"},\"devDependencies\":{\"vite\":\"5.0.0\"}}");
        var chatVite = Chat("cp vite", vite);
        await Open(chatVite.Id);
        ui.StartCommand.Execute(null);
        bool wasStarting = studio.IsStarting;
        await Open(chatB.Id);
        Check("CP4 a dev server still starting when the chat moves: the next project is not 'starting' (its own start used to be refused)", wasStarting && !studio.IsStarting, $"{wasStarting} {studio.IsStarting}");
        Check("CP4 …the next project's preview comes up", await WaitUntil(() => studio.IsServing, 5000), ui.StatusText);
        await Task.Delay(300);
        Check("CP4 …and nothing the old server said lands in its log or status", ui.ServerLog.Count == 0 && ui.LastLogLine == "" && !ui.ShowLog, string.Join(" | ", ui.ServerLog));

        // a review round left for another chat
        await Open(chatA.Id);
        string aJs = Path.Combine(a, "alpha-only.js"), jsBefore = File.ReadAllText(aJs);
        string htmlBefore = File.ReadAllText(aHtml);
        typeof(UiStudioViewModel).GetMethod("NoteToolEdit", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(ui, [aHtml, htmlBefore + "<!-- before -->", htmlBefore]);
        var round = Field<object>(ui, "_session");
        ((IDictionary)round.GetType().GetField("Before")!.GetValue(round))[aJs] = jsBefore;
        Check("CP5 (control) a round to review on A", ui.HasSession);
        await Open(chatB.Id);
        Check("CP5 a review round does not hold the Studio on A any more: the chat moved, so did the Studio, and A's round is not shown on B", StudioOn(b) && !ui.HasSession && !ui.ShowSessionBar, ui.ProjectPath);
        await Open(chatNone.Id);
        await Open(chatB.Id);
        Check("CP5 …nor after going through a chat with no project", StudioOn(b) && !ui.HasSession);
        File.WriteAllText(aJs, "console.log('the AI went on working');\n");
        await Open(chatA.Id);
        Check("CP5 back on A its round is there again", StudioOn(a) && ui.HasSession);
        var changed = (HashSet<string>)round.GetType().GetField("Changed")!.GetValue(round);
        Check("CP5 …with what the AI changed while the Studio was elsewhere", await WaitUntil(() => changed.Contains(aJs), 5000), string.Join(",", changed));
        ui.ToolConfirmOverride = true;
        ui.UndoAllCommand.Execute(null);
        ui.ToolConfirmOverride = null;
        Check("CP5 undo all still works: A's files come back, B's are untouched", File.ReadAllText(aJs) == jsBefore && File.ReadAllText(bHtml) == bText && !ui.HasSession, File.ReadAllText(aJs));

        // ── CP6: the Studio opening a folder ──
        await Open(chatA.Id);
        int chatsBefore = Vm.Sessions.Count;
        ui.OpenProject(b, autoStart: false);
        await Render();
        var newChat = Vm.CurrentSession;
        Check("CP6 opening another project from the Studio in a chat that holds work: the new project gets its own chat, the Studio is on it",
            newChat.Id != chatA.Id && SamePath(newChat.ProjectPath, b) && Vm.Sessions.Count == chatsBefore + 1 && StudioOn(b), $"{newChat.Title} {newChat.ProjectPath}");
        Check("CP6 …and the chat left keeps its own project", SamePath(Persist.LoadSession(chatA.Id).ProjectPath, a) && SamePath(Vm.WorkingDirectory, b));
        Vm.NewSession(keepProject: false);
        await Render();
        var unbound = Vm.CurrentSession;
        ui.OpenProject(c, autoStart: false);
        await Render();
        Check("CP6 in a chat with no project the folder becomes its project: no second chat, the Studio is on it",
            ReferenceEquals(Vm.CurrentSession, unbound) && SamePath(unbound.ProjectPath, c) && StudioOn(c), unbound.ProjectPath);

        await Open(chatA.Id);
        Fake.Script.Clear();
        Fake.Script.Enqueue(FakeStep.Gated(["Working"], gate, [" — done."]));
        await Send("keep working");
        await WaitUntil(() => Vm.IsGenerating);
        ui.OpenProject(b, autoStart: false);
        await Render();
        Check("CP6 opening another project while a reply runs: the reply keeps its folder, the new chat is tagged with the new project (it used to keep the old tag)",
            Vm.CurrentSession.Id != chatA.Id && SamePath(Vm.CurrentSession.ProjectPath, b) && StudioOn(b)
            && SamePath(Vm.Sessions.First(s => s.Id == chatA.Id).ProjectPath, a), $"{Vm.CurrentSession.ProjectPath} {ui.ProjectPath}");
        gate.Release();
        await WaitUntil(() => !Vm.Sessions.Any(s => s.Id == chatA.Id && s.RunStatus is "working" or "waiting"));
        Fake.Script.Clear();
        await Render();

        // ── CP7: pressed twice ──
        var sameA = Chat("cp alpha again", a);
        await Open(chatA.Id);
        await WaitUntil(() => studio.IsServing);
        string url = studio.CurrentBaseUrl;
        moved = 0;
        await Open(sameA.Id);
        await Open(chatA.Id);
        Vm.LoadSessionCommand.Execute(chatA.Id);
        await Render();
        Check("CP7 another chat of the same project, or the same chat clicked again: nothing is reset, the preview keeps running", moved == 0 && studio.CurrentBaseUrl == url, $"{moved} {url} {studio.CurrentBaseUrl}");
        chatsBefore = Vm.Sessions.Count;
        ui.OpenProject(a, autoStart: false);
        ui.OpenProject(a, autoStart: false);
        await Render();
        Check("CP7 opening the same folder twice makes no chat and leaves the Studio on it", Vm.Sessions.Count == chatsBefore && StudioOn(a));

        foreach (var id in new[] { chatB.Id, chatA.Id, chatB.Id, chatA.Id, chatB.Id }) Vm.LoadSessionCommand.Execute(id);
        Check("CP7 a burst of switches ends on the last chat, at once", StudioOn(b));
        await WaitUntil(() => studio.IsServing);
        await Task.Delay(400);
        using (var http = new HttpClient())
            Check("CP7 …and only the last project's preview starts", (await http.GetStringAsync(studio.CurrentBaseUrl + "index.html")).Contains("beta"));

        studio.ProjectChanged -= onMoved;
        studio.NavigateRequested -= onNav;
        graph.IsVisible = false;
        Vm.NewSession(keepProject: false);
        await Render();
        Vm.SetWorkingDirectory(folderBefore);
        await Render();
    }
}
