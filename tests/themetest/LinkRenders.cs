using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Helpers;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;
using CluadeX.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ThemeTest;

internal static partial class Program
{
    /// <summary>
    /// Links, pictures and copying in a reply (owner 2026-10-29: "ทำไมลิ้งค์ในแชทกดไม่ได้ เช่น ภาพที่เอไอส่งให้ และควร
    /// ก๊อปปี้คำพูดในแชทแบบลากคลุมได้"). The link forms are the ones Codex wrote in the naruay chat. Checked: how each
    /// target is read (ChatLinks.Resolve), that a local picture is drawn in the bubble, that every link carries its target,
    /// and that a reply's text selects and copies. renders\bubbles\links.png shows it.
    /// </summary>
    static async Task LinkRenders()
    {
        Log("\n## Links, pictures and copying in a reply");
        string dir = Path.Combine(Path.GetTempPath(), "cx-links-" + Environment.ProcessId, "my site");
        Directory.CreateDirectory(Path.Combine(dir, "previews"));
        string png = Path.Combine(dir, "previews", "desk top.png");
        string js = Path.Combine(dir, "app.js");
        File.WriteAllText(js, "alert(1)");
        SavePicture(png);
        string fwd = png.Replace('\\', '/');

        // 1. Reading a target
        void Reads(string name, string raw, ChatLinkKind kind, string? target = null)
        {
            var got = ChatLinks.Resolve(raw, dir);
            Check($"LK resolve: {name}", got.Kind == kind && (target == null || string.Equals(got.Target, target, StringComparison.OrdinalIgnoreCase)),
                $"{got.Kind} {got.Target}");
        }
        Reads("/D:/… (Codex's form) is the file", "/" + fwd, ChatLinkKind.ViewFile, png);
        Reads("<…> around a path with spaces", "</" + fwd + ">", ChatLinkKind.ViewFile, png);
        Reads("%20 for a space", "/" + fwd.Replace(" ", "%20"), ChatLinkKind.ViewFile, png);
        Reads("file:/// address", new Uri(png).AbsoluteUri, ChatLinkKind.ViewFile, png);
        Reads("relative to the project", "previews/desk top.png", ChatLinkKind.ViewFile, png);
        Reads("a line number after the file", js.Replace('\\', '/') + ":12", ChatLinkKind.RevealFile, js);
        Reads("#L12 after the file", js + "#L12", ChatLinkKind.RevealFile, js);
        Reads("a script is shown, never run", js, ChatLinkKind.RevealFile, js);
        Reads("a folder", dir, ChatLinkKind.Folder, dir);
        Reads("a missing file", Path.Combine(dir, "gone.png"), ChatLinkKind.Missing);
        Reads("https", "https://example.com/a?b=1", ChatLinkKind.Web, "https://example.com/a?b=1");
        Reads("a network share is refused", @"\\evil-host\share\a.png", ChatLinkKind.Refused);
        Reads("//host/share is refused", "//evil-host/share/a.png", ChatLinkKind.Refused);
        Reads("file://host/ is refused", "file://evil-host/share/a.png", ChatLinkKind.Refused);
        Reads("javascript: is refused", "javascript:alert(1)", ChatLinkKind.Refused);
        Reads("ms-settings: is refused", "ms-settings:privacy", ChatLinkKind.Refused);
        Reads("empty is refused", "  ", ChatLinkKind.Refused);

        // 2. Drawn in a reply
        var vm = Sp.GetRequiredService<ChatViewModel>();
        var themes = Sp.GetRequiredService<ThemeService>();
        var folderBefore = ChatLinks.ProjectFolder;
        try
        {
            themes.Apply(ThemeIds.Midnight, persist: false);
            MainVm.NavigateToCommand.Execute("Chat");
            vm.NewSession();
            ChatLinks.ProjectFolder = () => dir;
            var reply = new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = $"ทำ mockup ให้แล้วค่ะ\n\n![Mockup หน้าแรก](</{fwd}>)\n\n" +
                          $"ไฟล์: [desk top.png](/{fwd.Replace(" ", "%20")}) · [app.js](app.js:3) · ดูต่อที่ https://example.com/docs.\n\n" +
                          "- รายการที่ [หาย](gone.png) · ![จากเว็บ](https://example.com/x.png)\n\n```js\nconsole.log('copy me')\n```",
            };
            vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "ขอดูภาพหน้าเว็บ" });
            vm.Messages.Add(reply);
            await Idle(900);
            var view = Find<ChatView>(MainWin).FirstOrDefault(v => v.IsVisible);
            var list = view?.FindName("ChatList") as ListBox;
            list?.ScrollIntoView(reply);
            await Idle(900);   // the picture decodes off the UI thread
            var row = list?.ItemContainerGenerator.ContainerFromItem(reply) as DependencyObject;
            SavePng(Snapshot(RootVisual(MainWin), W, H), Path.Combine(OutRoot, "renders", "bubbles", "links.png"));

            var pictures = row == null ? new List<Image>() : Find<Image>(row).Where(i => i.Source is BitmapSource && i.IsVisible
                && (i.Parent as Border)?.Tag is ChatLinkTarget).ToList();
            Check("LK reply: the local picture is drawn in the bubble", pictures.Count == 1,
                $"{pictures.Count} picture(s){(pictures.Count > 0 ? $", {pictures[0].ActualWidth:0}×{pictures[0].ActualHeight:0}" : "")}");
            Check("LK reply: the picture is not stretched past its own size", pictures.Count == 1 && pictures[0].ActualWidth <= 200.5,
                pictures.Count == 1 ? $"{pictures[0].ActualWidth:0}" : "");

            var blocks = row == null ? new List<SelectableTextBlock>() : Find<SelectableTextBlock>(row).Where(b => b.IsVisible).ToList();
            var links = blocks.SelectMany(b => Walk(b.Inlines)).OfType<Hyperlink>().ToList();
            Check("LK reply: five links, each carrying its target",
                links.Count == 5 && links.All(l => l.Tag is ChatLinkTarget),
                string.Join(", ", links.Select(l => (l.Tag as ChatLinkTarget)?.Url ?? "-")));
            Check("LK reply: a web picture is only a link (never fetched)",
                links.Any(l => (l.Tag as ChatLinkTarget)?.Url == "https://example.com/x.png"));
            Check("LK reply: the bare address stops before its full stop",
                links.Any(l => (l.Tag as ChatLinkTarget)?.Url == "https://example.com/docs"));
            Check("LK reply: the picture's alt text is not left as a stray \"!\"",
                !blocks.Any(b => new TextRange(b.ContentStart, b.ContentEnd).Text.Contains("![")));

            // 3. Copying: the selection engine attaches, selects the paragraph and copies it
            Check("LK copy: the selection engine was found", SelectableTextBlock.IsAvailable);
            var para = blocks.FirstOrDefault(b => new TextRange(b.ContentStart, b.ContentEnd).Text.Contains("ไฟล์:"));
            string? before = TryClipboard(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
            string? copied = null;
            if (para != null && para.EnsureEditor())
            {
                para.Focus();
                ApplicationCommands.SelectAll.Execute(null, para);
                ApplicationCommands.Copy.Execute(null, para);
                await Idle(100);
                copied = TryClipboard(() => Clipboard.GetText());
            }
            Check("LK copy: Select all + Copy puts the paragraph's text on the clipboard",
                copied != null && copied.Contains("ไฟล์:") && copied.Contains("https://example.com/docs"), copied ?? "(nothing)");
            if (before != null) TryClipboard(() => { Clipboard.SetText(before); return ""; });

            var code = blocks.FirstOrDefault(b => new TextRange(b.ContentStart, b.ContentEnd).Text.Contains("copy me"));
            Check("LK copy: a code block selects too", code != null && code.EnsureEditor());
        }
        finally
        {
            ChatLinks.ProjectFolder = folderBefore;
            vm.NewSession();
            await Idle(200);
            try { Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static IEnumerable<Inline> Walk(InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            yield return i;
            if (i is Span span) foreach (var inner in Walk(span.Inlines)) yield return inner;
        }
    }

    static string? TryClipboard(Func<string?> act)
    {
        for (int i = 0; i < 5; i++)
        {
            try { return act(); }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(50); }
        }
        return null;
    }

    /// <summary>A small 200×120 picture to link to (drawn here: no test asset to ship).</summary>
    static void SavePicture(string path)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Colors.MediumPurple, Colors.Gold, 30), null, new Rect(0, 0, 200, 120));
            dc.DrawEllipse(Brushes.White, null, new Point(60, 60), 30, 30);
        }
        var bmp = new RenderTargetBitmap(200, 120, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
