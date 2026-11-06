using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Helpers;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Attachments;
using CluadeX.ViewModels;

namespace ChatTest;

/// <summary>
/// Owner 2026-11-06: "ช่องแชท ลากไฟล์มาวางได้ทุกรูปแบบ … รูป งาน media งานไฟล์เอกสาร, กล่องโต้ตอบเมื่อต้องแสดงโค๊ด ควรแสดงเป็นโค๊ดแบบ
/// ก๊อปไปวางได้ต่างหากในกล่อง ทุกเจ้าทำให้ดี, การถามแบบหลายสเต็ป ย้อนกลับหรือเดินหน้าได้".
/// CF — every way a provider writes a code block gets the box with Copy (replies and questions).
/// AS — several questions on one card: steps, Back / Next, tick buttons, a review, one answer each.
/// XM — audio / video described, HEIC/AVIF/ICO converted, a picture dragged out of a web page.
/// </summary>
internal static partial class Program
{
    static async Task TestChatUx()
    {
        TestCodeFences();
        await TestCodeBoxOnScreen();
        TestAskStepsPure();
        await TestAskStepsCard();
        TestMediaFacts();
        await TestImageKinds();
        await TestWebImages();
    }

    // ═══════════════════════════════════════════ CF ═══════════════════════════════════════════

    static void TestCodeFences()
    {
        Log("\n## CF — code blocks however a model writes them");
        static MarkdownHelper.CodeFence? One(string text) => MarkdownHelper.FindCodeFences(text) is { Count: 1 } l ? l[0] : null;

        var cs = One("Here:\n```c#\nint x = 1;\n```\nDone.");
        Check("CF1 ```c# is a code block (it used to be plain text: the language tag was \\w only)", cs is { Language: "c#", Code: "int x = 1;", Open: false }, $"{cs}");
        var cpp = One("```c++\nint main() {}\n```");
        Check("CF1 ```c++ and ```objective-c too", cpp is { Language: "c++" } && One("```objective-c\n@end\n```") is { Language: "objective-c" }, $"{cpp}");
        var titled = One("```python title=\"app.py\"\nprint(1)\n```");
        Check("CF2 an info string with a title / a :file tail / {.lang} keeps the language", titled is { Language: "python", Code: "print(1)" }
            && One("```ts:src/app.ts\nlet a = 1\n```") is { Language: "ts" } && One("```{.rust}\nfn main(){}\n```") is { Language: "rust" }, $"{titled}");
        var tilde = One("~~~js\nconsole.log(1)\n~~~");
        Check("CF3 a ~~~ fence", tilde is { Language: "js", Code: "console.log(1)", Open: false }, $"{tilde}");
        var nested = One("````md\nUse:\n```js\nx()\n```\n````\nafter");
        Check("CF4 a 4-backtick fence holds ``` inside it", nested is { Language: "md", Open: false } && nested.Value.Code.Contains("```js") && nested.Value.Code.EndsWith("```"), $"{nested}");
        var listed = One("1. Install:\n   ```bash\n   npm install\n   npm test\n   ```\n2. Run it");
        Check("CF5 a fence indented inside a list item: the indent is taken off the code", listed is { Language: "bash", Code: "npm install\nnpm test", Open: false }, $"{listed}");
        string mid = "Run this: ```bash\nls -la\n```";
        var midF = One(mid);
        Check("CF6 a fence that opens at the end of a sentence (the old reader allowed it)", midF is { Language: "bash", Code: "ls -la", Open: false } && midF.Value.Start == mid.IndexOf("```"), $"{midF}");
        Check("CF7 …and one closed at the end of its last line", One("Run: ```bash\nls```") is { Code: "ls", Open: false });
        Check("CF8 streaming: an opening line still being written is not a fence yet", MarkdownHelper.FindCodeFences("text\n```pyt").Count == 0);
        Check("CF8 …once its newline arrives it is an open block (code so far, Copy works)", One("```python\nprint(") is { Language: "python", Code: "print(", Open: true });
        Check("CF9 ```inline``` on one line is not a fence", MarkdownHelper.FindCodeFences("use ```npm i``` here\nand more").Count == 0);
        var two = MarkdownHelper.FindCodeFences("a\n```js\n1\n```\nb\n~~~py\n2\n~~~\nc");
        Check("CF10 two blocks with text between", two.Count == 2 && two[0].Code == "1" && two[1].Code == "2" && two[1].Language == "py", string.Join(" | ", two));
        Check("CF10 a ``` line inside a ~~~ block does not close it", One("~~~\n```\nstill code\n~~~") is { Code: "```\nstill code", Open: false });
    }

    static async Task TestCodeBoxOnScreen()
    {
        await FreshChat();
        Fake.ScriptFor["[CF]"] = new Queue<FakeStep>([FakeStep.Reply("C#:\n```c#\nvar x = 1;\n```\nand a list:\n1. step\n   ~~~bash\n   dotnet build\n   ~~~")]);
        await Send("[CF] show code");
        await WaitIdle();
        await Render();
        var reply = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant && m.Content.Contains("var x"));
        var copies = reply == null || Container(reply) == null ? new List<Button>() : Find<Button>(Container(reply)).Where(b => (b.Content as string)?.Contains("Copy") == true).ToList();
        var codeCopies = copies.Where(b => b.Content as string == "\U0001F4CB Copy" && b.IsVisible).ToList();
        Check("CF12 a reply row's hidden question card renders nothing (it once rendered every reply a second time)",
            reply != null && reply.QuestionShown.Length == 0 && Vm.Messages.Where(m => m.Role != MessageRole.Question).All(m => m.QuestionShown.Length == 0));
        Check("CF11 on screen: a ```c# block and a ~~~ block in a list each get a box with Copy", codeCopies.Count == 2,
            $"copies={codeCopies.Count} all=" + string.Join(" | ", copies.Select(b => $"{b.Content}/{b.IsVisible}/{b.Parent?.GetType().Name}")));
        Fake.ScriptFor.Clear();
    }

    // ═══════════════════════════════════════════ AS ═══════════════════════════════════════════

    static void TestAskStepsPure()
    {
        Log("\n## AS — several questions on one card");
        var parsed = AgentToolService.ParseAskSteps(
            "[\"Name?\", {\"question\":\"DB?\",\"options\":[\"SQLite\",{\"label\":\"Postgres\",\"description\":\"server\"}]}, {\"q\":\"Extras?\",\"options\":\"auth, tests\",\"multiSelect\":true}, {\"options\":[\"x\"]}]");
        Check("AS1 questions[]: plain strings, {question, options as strings or {label, description}}, comma text, multiSelect; a step with no question is dropped",
            parsed.Count == 3 && parsed[0].Question == "Name?" && parsed[1].Options.SequenceEqual(new[] { "SQLite", "Postgres" }) && parsed[1].Notes.Contains("Postgres: server")
            && parsed[2].Multi && parsed[2].Options.SequenceEqual(new[] { "auth", "tests" }),
            string.Join(" | ", parsed.Select(p => p.Question + "[" + string.Join(",", p.Options) + "]" + (p.Multi ? "*" : ""))));
        Check("AS1 not an array → none (the single-question path)", AgentToolService.ParseAskSteps("Which?").Count == 0 && AgentToolService.ParseAskSteps("[broken").Count == 0);
        Check("AS1 at most 10 steps", AgentToolService.ParseAskSteps("[" + string.Join(",", Enumerable.Range(1, 15).Select(i => $"\"q{i}\"")) + "]").Count == 10);

        // One multi-select question: tick buttons, Send gives both picks at once.
        IReadOnlyList<string>? got = null;
        var m = new ChatMessage { Role = MessageRole.Question };
        m.SetSteps(new[] { new AskStep("Which?", new[] { "a", "b", "c" }, Array.Empty<string>(), true) }, a => got = a);
        m.StepPick(m.StepChoices[0]); m.StepPick(m.StepChoices[2]); m.StepPick(m.StepChoices[2]); m.StepPick(m.StepChoices[1]);
        Check("AS2 a multi-select step ticks and unticks, and does not send on a pick", got == null && m.StepChoices.Count(c => c.Picked) == 2 && m.StepChoices[0].Shown == "✓ a" && !m.ShowsStepBar && m.StepIsLast);
        m.AnswerDraft = "d";
        bool sent = m.AnswerQuestion(m.AnswerDraft);
        Check("AS2 …Send gives the picks plus what was typed, once; the card keeps question + answer", sent && got is { Count: 1 } && got[0] == "a, b, d" && m.QuestionAnswered
            && m.Content.Contains("❓ Which?") && m.Content.Contains("→ a, b, d") && !m.AnswerQuestion("again"), $"got={string.Join("|", got ?? new List<string>())} content={m.Content}");

        var e = new ChatMessage { Role = MessageRole.Question };
        IReadOnlyList<string>? got2 = null;
        e.SetSteps(new[] { new AskStep("One?", Array.Empty<string>()), new AskStep("Two?", Array.Empty<string>()) }, a => got2 = a);
        bool emptyNext = e.StepNext();
        Check("AS3 Next with no answer stays on the step", !emptyNext && e.StepIndex == 0 && got2 == null && !e.CanStepBack);
        e.CloseQuestion(L("chat.ask.cancelled"));
        Check("AS3 a stopped run closes a steps card: no buttons, the saved text says so", e.QuestionAnswered && !e.ShowsStepBar && !e.ShowsAnswerBox && e.QuestionShown.Contains(L("chat.ask.cancelled")) && got2 == null);
    }

    static async Task TestAskStepsCard()
    {
        Vm.SetWorkingDirectory(ProjA);
        Vm.AgenticMode = true;
        await FreshChat();
        string questions = "[{\"question\":\"Which database?\",\"options\":[\"SQLite\",\"Postgres\"]},"
            + "{\"question\":\"Which extras?\",\"options\":[\"auth\",\"tests\"],\"multiSelect\":true},"
            + "{\"question\":\"App name? It goes in:\\n```json\\n{\\\"name\\\": \\\"...\\\"}\\n```\"}]";
        Fake.ScriptFor["[AS]"] = new Queue<FakeStep>([FakeStep.Reply("A few things first.\n[ACTION: ask_user]\nquestions: " + questions + "\n[/ACTION]")]);
        Fake.Script.Clear();
        Fake.Script.Enqueue(FakeStep.Reply("Setting up SQLite with auth and tests — done."));
        await Send("[AS] set up the app");
        ChatMessage q = null;
        bool asked = await WaitUntil(() => (q = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Question && m.UsesSteps && !m.QuestionAnswered)) != null, 20000);
        await Task.Delay(700); await Render();
        Check("AS4 ask_user with questions opens ONE card with steps (not three cards)", asked && Vm.Messages.Count(m => m.Role == MessageRole.Question && !m.QuestionAnswered) == 1 && q.Steps.Count == 3,
            string.Join(" | ", Vm.Messages.Select(m => $"{m.Role}:{m.Content.Replace('\n', '⏎')}")));
        if (q == null) return;
        var card = Container(q);
        Button? B(string text) => card == null ? null : Find<Button>(card).FirstOrDefault(b => (b.Content as string) == text && b.IsVisible);
        Check("AS4 …the bar says 1 / 3, the question and its options are on screen, no Back on the first step",
            q.ShowsStepBar && q.StepLabel == "1 / 3" && q.QuestionShown.Contains("Which database?") && B("SQLite") != null && B(L("chat.ask.back")) == null && B(L("chat.ask.next")) != null,
            $"label={q.StepLabel} shown={q.QuestionShown}");
        SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-question-steps-1.png"));

        Click(B("SQLite")!);
        await Render();
        Check("AS5 a pick on a single-choice step moves on: 2 / 3, Back shows", q.StepIndex == 1 && q.StepLabel == "2 / 3" && B(L("chat.ask.back")) != null && B("auth") != null, $"step={q.StepIndex}");
        Click(B("auth")!); await Render(); Click(B("tests")!); await Render();
        Check("AS5 the multi-select step ticks both (✓) and stays", q.StepIndex == 1 && B("✓ auth") != null && B("✓ tests") != null);
        Click(B(L("chat.ask.next"))!);
        await Render();
        var codeCopy = card == null ? null : Find<Button>(card).FirstOrDefault(b => (b.Content as string)?.Contains("Copy") == true && b.IsVisible);
        Check("AS6 step 3: the code in the question is a box with Copy", q.StepIndex == 2 && codeCopy != null, $"step={q.StepIndex}");
        SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-question-steps-3.png"));
        q.AnswerDraft = "Shop";
        Click(B(L("chat.ask.next"))!);
        await Render();
        Check("AS7 after the last step: the review lists every answer, with Send and Back",
            q.OnReview && q.QuestionShown.Contains("→ SQLite") && q.QuestionShown.Contains("→ auth, tests") && q.QuestionShown.Contains("→ Shop")
            && B(L("chat.ask.send")) != null && B(L("chat.ask.back")) != null && !q.ShowsAnswerBox, q.QuestionShown);
        SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-question-steps-review.png"));

        Click(B(L("chat.ask.back"))!); await Render();
        bool draftKept = q.StepIndex == 2 && q.AnswerDraft == "Shop";
        Click(B(L("chat.ask.back"))!); await Render();
        bool picksKept = q.StepIndex == 1 && q.StepChoices.All(c => c.Picked);
        Click(B(L("chat.ask.back"))!); await Render();
        Click(B("Postgres")!); await Render();   // change the first answer
        Check("AS8 Back keeps each step's answer (typed text, ticks) and a changed answer replaces the old one",
            draftKept && picksKept && q.StepIndex == 1 && q.Steps[0].Answer == "Postgres", $"draft={draftKept} picks={picksKept} step={q.StepIndex} a0={q.Steps[0].Answer}");
        Click(B(L("chat.ask.next"))!); await Render();
        Click(B(L("chat.ask.next"))!); await Render();
        Click(B(L("chat.ask.send"))!);
        bool done = await WaitIdle(20000);
        string told = string.Join("\n", Fake.Calls.Select(c => c.Input).Concat(Fake.Calls.SelectMany(c => c.History.Select(h => h.Content))));
        Check("AS9 Send: the agent gets one numbered answer per question and carries on",
            done && told.Contains("1. Which database? → Postgres") && told.Contains("2. Which extras? → auth, tests") && told.Contains("→ Shop")
            && Vm.Messages.Any(m => m.Content.Contains("Setting up SQLite")), told.Length > 600 ? told[^600..] : told);
        Check("AS9 the card keeps every question with its answer (saved with the chat), no buttons left",
            q.QuestionAnswered && q.Content.Contains("❓ Which database?\n→ Postgres") && q.Content.Contains("→ Shop") && B(L("chat.ask.send")) == null && B(L("chat.ask.back")) == null, q.Content);
        Fake.ScriptFor.Clear();
    }

    // ═══════════════════════════════════════════ XM ═══════════════════════════════════════════

    static void TestMediaFacts()
    {
        Log("\n## XM — audio / video, pictures Windows must convert, pictures from a web page");
        const string probe = """
        {"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"disposition":{"attached_pic":0}},
                    {"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2}],
         "format":{"duration":"151.240000"}}
        """;
        var f = MediaInfo.ParseFfprobe(probe);
        Check("XM1 ffprobe's answer → length, picture and sound", f != null && f.Duration is { } d && MediaInfo.Length(d) == "2:31"
            && f.Video == "h264 1920×1080" && f.Audio == "aac 48 kHz stereo", $"{f}");
        var mp3 = MediaInfo.ParseFfprobe("""{"streams":[{"codec_type":"audio","codec_name":"mp3","sample_rate":"44100","channels":1},{"codec_type":"video","codec_name":"mjpeg","width":500,"height":500,"disposition":{"attached_pic":1}}],"format":{"duration":"3723"}}""");
        Check("XM1 an mp3's cover picture is not called video; an hour long reads 1:02:03", mp3 is { Video: null, Audio: "mp3 44.1 kHz mono" } && MediaInfo.Length(mp3.Duration!.Value) == "1:02:03", $"{mp3}");
        Check("XM1 junk from ffprobe → nothing, no exception", MediaInfo.ParseFfprobe("not json") == null && MediaInfo.ParseFfprobe("{}") == null);

        string dir = Path.Combine(PipeRoot, "media-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        string clip = Path.Combine(dir, "clip.mp4"), song = Path.Combine(dir, "song.mp3"), ts = Path.Combine(dir, "app.ts");
        File.WriteAllBytes(clip, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray());
        File.WriteAllBytes(song, new byte[] { 0x49, 0x44, 0x33, 3, 0, 0, 0, 0, 0, 0 });
        File.WriteAllText(ts, "export const a: number = 1;\n");
        var v = DocumentReader.Read(clip, 0, 4000);
        var a = DocumentReader.Read(song, 0, 4000);
        Check("XM2 an attached video / song is described as one (what is in it, how to work with it) — not 'use hex_read'",
            v.Kind == "video" && v.Note!.Contains("clip.mp4 is a video file (MP4)") && v.Note.Contains("ffmpeg") && !v.Note.Contains("hex_")
            && a.Kind == "audio" && a.Note!.Contains("audio file (MP3)") && !a.Note.Contains("frames"), v.Note + " || " + a.Note);
        Check("XM2 a .ts file is still read as TypeScript", DocumentReader.Read(ts, 0, 4000) is { Kind: "text" } t && t.Text.Contains("export const"));
        var digest = AttachmentDigest.Build(new List<string>(), new List<string> { clip }, AttachmentAudience.CliAgent, DigestBudget.Cli, null, CancellationToken.None);
        Check("XM2 the message names it a video file with its note", digest.Text.Contains("clip.mp4 — video file") && digest.Text.Contains("is a video file"), digest.Text);
    }

    static byte[] TinyPng(int w = 6, int h = 4)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 80; px[i + 2] = 20; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>An .ico whose one frame is a PNG (Vista icons are).</summary>
    static byte[] IcoOf(byte[] png, int size)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0); w.Write((short)1); w.Write((short)1);
        w.Write((byte)size); w.Write((byte)size); w.Write((byte)0); w.Write((byte)0);
        w.Write((short)1); w.Write((short)32); w.Write(png.Length); w.Write(22);
        w.Write(png);
        w.Flush();
        return ms.ToArray();
    }

    static async Task TestImageKinds()
    {
        await FreshChat();
        Vm.PendingImages.Clear(); Vm.PendingFiles.Clear();
        string dir = Path.Combine(PipeRoot, "pics-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        string ico = Path.Combine(dir, "logo.ico"), heic = Path.Combine(dir, "phone.heic"), jfif = Path.Combine(dir, "photo.jfif");
        File.WriteAllBytes(ico, IcoOf(TinyPng(16, 16), 16));
        File.WriteAllBytes(heic, Encoding.ASCII.GetBytes("not really a heic picture at all"));
        File.WriteAllBytes(jfif, ConvertPngToJpeg(TinyPng()));
        int added = await Vm.AttachFilesAsync([ico, heic, jfif]);
        await Render();
        Check("XM3 an icon is decoded by Windows and attached as a PNG picture; a .jfif is a JPEG picture",
            Vm.PendingImages.Count == 2 && Vm.PendingImages.Any(p => Path.GetFileName(p) == "logo.png") && Vm.PendingImages.Any(p => p.EndsWith("photo.jpg")),
            "images=" + string.Join(", ", Vm.PendingImages) + " files=" + string.Join(", ", Vm.PendingFiles));
        Check("XM3 a picture Windows cannot open (no codec) still goes in — as a file, and the status line says why, in the app's language",
            Vm.PendingFiles.Any(p => p.EndsWith("phone.heic")) && Vm.StatusText.Contains("phone.heic") && !Vm.StatusText.Contains("Exception"),
            $"{Vm.StatusText} (copied in: {added})");
        Vm.PendingImages.Clear(); Vm.PendingFiles.Clear();
    }

    static byte[] ConvertPngToJpeg(byte[] png)
    {
        var frame = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var enc = new JpegBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static async Task TestWebImages()
    {
        string html = "Version:0.9\r\nStartHTML:0000000105\r\nSourceURL:https://example.com/gallery/page.html\r\n<html><body><!--StartFragment-->"
            + "<a href=\"/x\"><img alt=\"cat\" src=\"/img/cat.png?w=200&amp;h=100\"></a><!--EndFragment--></body></html>";
        var u = ChatViewModel.WebImageSource(html, null);
        Check("XM4 a browser's drag HTML → its <img src>, made absolute against SourceURL, entities decoded",
            u?.AbsoluteUri == "https://example.com/img/cat.png?w=200&h=100", u?.AbsoluteUri ?? "(null)");
        Check("XM4 a link to an image file works without HTML; a link to a page does not take the drop",
            ChatViewModel.WebImageSource(null, "https://cdn.example.com/a/b/pic.webp\r\nPic")?.Host == "cdn.example.com"
            && ChatViewModel.WebImageSource(null, "https://example.com/article") == null && ChatViewModel.WebImageSource("<p>just text</p>", null) == null);
        Check("XM4 only http(s) and data:image — never file:, javascript: or a data: page",
            ChatViewModel.WebImageSource("<img src=\"file:///C:/Windows/win.ini\">", null) == null
            && ChatViewModel.WebImageSource("<img src='javascript:alert(1)'>", null) == null
            && ChatViewModel.WebImageSource("<img src=\"data:text/html;base64,PGgxPg==\">", null) == null);

        await FreshChat();
        Vm.PendingImages.Clear();
        byte[] png = TinyPng();
        var dataUri = ChatViewModel.WebImageSource("<img src=\"data:image/png;base64," + Convert.ToBase64String(png) + "\">", null);
        bool okData = dataUri != null && await Vm.AttachWebImageAsync(dataUri);
        Check("XM5 a data:image picture (a page's inline image) is attached", okData && Vm.PendingImages.Count == 1, Vm.StatusText);

        // A local web server stands in for the page's host (no network): 200 with a PNG, 404, and one too big to take.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(stop.Token); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        var s = c.GetStream();
                        var buf = new byte[4096];
                        int n = await s.ReadAsync(buf);
                        string req = Encoding.ASCII.GetString(buf, 0, n);
                        string path = req.Split(' ').ElementAtOrDefault(1) ?? "/";
                        byte[] body; string status = "200 OK"; string type = "image/png";
                        if (path.StartsWith("/cat.png")) body = png;
                        else if (path.StartsWith("/huge.png")) body = new byte[21 * 1024 * 1024];
                        else { body = Encoding.ASCII.GetBytes("nope"); status = "404 Not Found"; type = "text/plain"; }
                        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        try { await s.WriteAsync(head); await s.WriteAsync(body); } catch { }
                    }
                });
            }
        });
        try
        {
            bool okHttp = await Vm.AttachWebImageAsync(new Uri($"http://127.0.0.1:{port}/cat.png"));
            Check("XM6 a picture dragged from a page is downloaded and attached under its own name",
                okHttp && Vm.PendingImages.Count == 2 && Path.GetFileName(Vm.PendingImages[^1]) == "cat.png", Vm.StatusText + " | " + string.Join(", ", Vm.PendingImages));
            bool bad = await Vm.AttachWebImageAsync(new Uri($"http://127.0.0.1:{port}/gone.png"));
            Check("XM6 a picture the page cannot give (404) → the localized hint to save it first, nothing attached",
                !bad && Vm.PendingImages.Count == 2 && Vm.StatusText == L("chat.attach.webImageFailed"), Vm.StatusText);
            bool huge = await Vm.AttachWebImageAsync(new Uri($"http://127.0.0.1:{port}/huge.png"));
            Check("XM6 over 20 MB is refused, not downloaded to the end", !huge && Vm.PendingImages.Count == 2, Vm.StatusText);
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            Vm.PendingImages.Clear();
        }
    }
}
