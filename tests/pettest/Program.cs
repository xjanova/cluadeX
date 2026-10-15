// The pet harness. Renders every species × pose × face to a PNG sheet (look at it — the art is the feature), and
// asserts the pet's logic against the REAL CluadeX code: hatching from learned habits, the species decision,
// floors from window rectangles, the physics, the lines, and pet.json.
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Services;
using CluadeX.Services.Pet;

namespace PetTest;

internal static class Program
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name} {detail}");
    }

    [STAThread]
    static int Main(string[] args)
    {
        string outDir = AppContext.BaseDirectory;
        if (args.Contains("--live")) return Live(args.Contains("--egg"), args.Contains("--hatch"));
        RenderSheet(Path.Combine(outDir, "pets.png"));
        RenderHatchlings(Path.Combine(outDir, "hatchlings.png"));
        RenderDesign(Path.Combine(outDir, "designed.png"));
        if (args.Contains("--art")) return 0;

        Hatching();
        Conversation();
        Designs();
        AiHatch();
        Decisions();
        Floors();
        Physics();
        Lines();
        Persistence();

        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ─── hatching ────────────────────────────────────────────────────────

    static PetHabits Habits(int days, int minutes, int messages, int tools, Func<int, int> hourOf, bool thai = false)
    {
        var h = new PetHabits();
        var day0 = new DateTime(2026, 10, 1);
        for (int m = 0; m < minutes; m++)
        {
            var t = day0.AddDays(m % Math.Max(1, days)).AddHours(hourOf(m)).AddMinutes(m % 60);
            PetBrain.RecordMinute(h, t);
        }
        for (int i = 0; i < messages; i++) PetBrain.RecordMessage(h, thai ? "ช่วยแก้บั๊กนี้หน่อย" : "please fix this bug");
        for (int i = 0; i < tools; i++) PetBrain.RecordTool(h, i % 3 == 0 ? "run_build" : "edit_file", i % 9 != 0);
        return h;
    }

    static void Hatching()
    {
        Console.WriteLine("== hatching");
        var egg = PetBrain.NewEgg(new Random(1));
        Check("a new egg has a random seed and colour, and nothing learned", egg.Seed != 0 && egg.EggHue is >= 0 and < 360 && PetBrain.HatchProgress(egg.Habits) == 0, $"hue={egg.EggHue}");
        var a = PetBrain.NewEgg(new Random(1)); var b = PetBrain.NewEgg(new Random(2));
        Check("two installs get different eggs", a.Seed != b.Seed && (a.EggHue != b.EggHue || a.EggSpots != b.EggSpots), "");

        var twoDays = Habits(2, 2000, 300, 40, _ => 14);
        Check("2 days is not enough, however many hours", !PetBrain.ReadyToHatch(twoDays), $"progress={PetBrain.HatchProgress(twoDays):P0}");
        var shortUse = Habits(5, 1439, 300, 40, m => m % 24);
        Check("23 h 59 min of real use is not enough (24 hours of actual use, not of the clock)", !PetBrain.ReadyToHatch(shortUse),
            $"min={shortUse.ActiveMinutes} progress={PetBrain.HatchProgress(shortUse):P0}");
        var fewWords = Habits(5, 1500, 99, 40, m => m % 24);
        Check("99 messages is not enough to read their character", !PetBrain.ReadyToHatch(fewWords), "");
        var enough = Habits(3, 1440, 100, 45, m => m % 24);
        Check("24 h of use over 3 days with 100 messages → ready, progress 100%", PetBrain.ReadyToHatch(enough) && Math.Abs(PetBrain.HatchProgress(enough) - 1) < 1e-9,
            $"days={enough.ActiveDays.Count} min={enough.ActiveMinutes} msgs={enough.Messages}");

        var h = new PetHabits();
        var msgs = new List<(DateTime, string)>
        {
            (new DateTime(2026, 10, 1, 9, 1, 0), "สวัสดี"), (new DateTime(2026, 10, 1, 9, 4, 0), "fix it"),
            (new DateTime(2026, 10, 1, 23, 30, 0), "late"), (new DateTime(2026, 10, 4, 10, 0, 0), "ทดสอบ"),
        };
        PetBrain.Bootstrap(h, msgs);
        Check("history head start: 2 days, 3 ten-minute slots = 30 min, 4 messages, 2 Thai",
            h.ActiveDays.Count == 2 && h.ActiveMinutes == 30 && h.Messages == 4 && h.ThaiMessages == 2 && h.Hours[23] == 10 && h.Bootstrapped,
            $"days={h.ActiveDays.Count} min={h.ActiveMinutes} msgs={h.Messages} th={h.ThaiMessages}");
        PetBrain.Bootstrap(h, msgs);
        Check("the head start is taken once", h.Messages == 4, "");

        var tools = new PetHabits();
        PetBrain.RecordTool(tools, "mcp__brainx-brain__brain_search", true);
        PetBrain.RecordTool(tools, "run_build", false);
        PetBrain.RecordTool(tools, "git_commit", true);
        PetBrain.RecordTool(tools, "browser_open", true);
        Check("tool families: brain, failed build, git, browser counted", tools.Brain == 1 && tools.Builds == 1 && tools.BuildFailures == 1 && tools.Git == 1 && tools.Browser == 1 && tools.ToolErrors == 1, "");
    }

    // ─── conversation: tone and samples ──────────────────────────────────

    static void Conversation()
    {
        Console.WriteLine("== reading how they talk");
        var h = new PetHabits();
        var rng = new Random(3);
        for (int i = 0; i < 1000; i++) PetBrain.RecordMessage(h, $"message number {i} about the build", rng);
        var idx = h.Samples.Select(x => int.Parse(x.Split(' ')[2])).ToList();
        Check("at most 80 samples kept, spread over the whole history (early and late messages both present)",
            h.Samples.Count == PetBrain.MaxSamples && idx.Any(i => i < 300) && idx.Any(i => i > 700), $"min={idx.Min()} max={idx.Max()}");

        string secret = PetBrain.SampleOf("my key is sk-abcdefghijklmnop1234 and ghp_ABCDEFGHIJKLMNOPQRSTuvwx, mail me at me@example.com, see https://example.com/x?token=1") ?? "";
        Check("samples never carry keys, tokens, e-mails or links", !secret.Contains("sk-") && !secret.Contains("ghp_") && !secret.Contains('@') && !secret.Contains("http") && secret.Contains("[…]"), secret);
        string code = PetBrain.SampleOf("fix this please\n```csharp\nvar password = \"hunter2\";\n```") ?? "";
        Check("pasted code is cut off (only their own words are kept)", code == "fix this please", code);
        Check("a message with nothing personal in it is not sampled", PetBrain.SampleOf("```\nonly code\n```") == null && PetBrain.SampleOf("ok") == null, "");
        string longText = PetBrain.SampleOf(new string('ก', 500)) ?? "";
        Check("a sample is at most 200 characters", longText.Length <= 201, $"{longText.Length}");

        var t = new PetHabits();
        PetBrain.RecordMessage(t, "ทำไมมันพังอีกแล้ว?!");
        PetBrain.RecordMessage(t, "ขอบคุณครับ 555");
        PetBrain.RecordMessage(t, "ด่วนเลยนะ!!!");
        PetBrain.RecordMessage(t, "please check this");
        var tone = PetBrain.ToneOf(t);
        Check("tone: questions, exclamations, polite words, laughter, urgency all counted",
            t.Questions == 1 && t.Exclaims == 2 && t.Polite == 2 && t.Playful == 1 && t.Urgent == 1 && tone.Polite == 0.5,
            $"q={t.Questions} !={t.Exclaims} polite={t.Polite} play={t.Playful} urgent={t.Urgent}");

        var p = new PetHabits();
        p.ProviderTurns["Anthropic"] = 40; p.ProviderTurns["Local"] = 12;
        Check("the AI used most is the one that will design the pet", PetBrain.MostUsedProvider(p) == "Anthropic" && PetBrain.MostUsedProvider(new PetHabits()) == null, "");
    }

    // ─── the AI's design ─────────────────────────────────────────────────

    /// <summary>A pet as an AI might answer: prose, a fenced JSON object, a pudding with a cherry on top.</summary>
    const string PuddingReply = """
        What a lovely thing to be asked! Here is my gift:
        ```json
        {
          "species": "Caramel pudding", "speciesTh": "พุดดิ้งคาราเมล", "name": "Purin",
          "personality": "Caring", "personalityText": "Soft, sweet and always there after a hard build.",
          "reason": "They push through long nights and failing builds; they deserve something sweet that wobbles but never breaks.",
          "palette": { "b": "#F6D58E", "s": "#E2B866", "h": "#FFF3C4", "a": "#F9E4B0", "c": "#9C5A2E", "r": "#E5485D" },
          "feet": true, "floats": false, "eyeX": 7, "eyeY": 11, "mouthY": 14, "blush": true,
          "body": [
            "........................",
            "............k...........",
            "..........rrrr..........",
            "..........rrrr..........",
            ".......cccccccccc.......",
            "......cccccccccccc......",
            ".....cccccccccccccc.....",
            ".....bcccbccccbcccb.....",
            ".....bbcbbbbcbbbbcbb....",
            ".....bhbbbbbbbbbbbbb....",
            ".....bhbbbbbbbbbbbbb....",
            ".....bbbbbbbbbbbbbbb....",
            ".....bbbbbbbbbbbbbbb....",
            ".....bbbbbbbbbbbbbbb....",
            "....bbbbbbbbbbbbbbbbb...",
            "....bbbbbaaaaaabbbbbb...",
            "....bbbbbaaaaaabbbbbb...",
            "....bbbbbbbbbbbbbbbbb...",
            "....bbbbbbbbbbbbbbbbs...",
            ".....bbbbbbbbbbbbbbs....",
            "....bbbb........bbbb....",
            "........................",
            "........................",
            "........................"
          ],
          "catchphrasesTh": ["สั่นได้แต่ไม่พังนะ", "พักกินขนมก่อนไหม", "build ผ่านแล้วค่อยฉลอง"],
          "catchphrasesEn": ["I wobble, but I don't break", "Snack break?", "Celebrate when it builds"]
        }
        ```
        I hope they love it.
        """;

    static PetHabits ReadyHabits() => Habits(3, 1440, 120, 60, m => m % 24, thai: true);

    static void Designs()
    {
        Console.WriteLine("== the AI's design");
        var s = new PetState { Seed = 99, Habits = ReadyHabits() };
        string req1 = PetDesigner.BuildRequest(s, new Random(1)), req2 = PetDesigner.BuildRequest(s, new Random(2));
        Check("the request asks the AI, as the one who worked with them, what pet it would give them as a friend",
            req1.Contains("You have worked with this person for 24 hours") && req1.Contains("what pixel pet would you give them, to be their friend")
            && PetDesigner.SystemPrompt.Contains("give") && PetDesigner.SystemPrompt.Contains("friend"), "");
        Check("it carries the measured profile and a sample of their own words",
            req1.Contains("works late at night") && req1.Contains("A FEW OF THEIR OWN MESSAGES") && req1.Contains("ช่วยแก้บั๊กนี้หน่อย"), "");
        string Insp(string r) => r.Split('\n').First(l => l.StartsWith("RANDOM INSPIRATION"));
        Check("each request carries a different random inspiration (nobody can predict it)", Insp(req1) != Insp(req2), $"{Insp(req1)} | {Insp(req2)}");

        var (d, err) = PetDesigner.ParseDesign(PuddingReply);
        Check("an answer in prose + ```json fences is read", d != null && err == null, err ?? "");
        Check("…name, species (both languages), personality, reason, colours, catchphrases",
            d is { Name: "Purin", Species: "Caramel pudding", SpeciesTh: "พุดดิ้งคาราเมล", Personality: "Caring" } && d.Palette["b"] == "#F6D58E"
            && d.CatchphrasesTh.Count == 3 && d.Reason.Length > 0, "");
        Check("…the body is 24 rows of 24, and a walking body's rows 20–23 are cleared for the app's feet",
            d!.Body.Length == 24 && d.Body.All(r => r.Length == 24) && d.Body.Skip(20).All(r => r.All(c => c == '.')), d.Body[20]);

        string Without(string field) => PuddingReply.Replace(field, "\"x\": 1");
        Check("no JSON at all → refused with a reason", PetDesigner.ParseDesign("I'd give them a cat.").error != null, "");
        Check("no name → refused", PetDesigner.ParseDesign(PuddingReply.Replace("\"name\": \"Purin\",", "")).error is { } e1 && e1.Contains("name"), "");
        Check("no body colour → refused", PetDesigner.ParseDesign(PuddingReply.Replace("\"b\": \"#F6D58E\", ", "")).error is { } e2 && e2.Contains("palette"), "");
        var tiny = PuddingReply[..PuddingReply.IndexOf("\"body\"", StringComparison.Ordinal)] + "\"body\": [\"bb\",\"bb\",\".\",\".\",\".\",\".\",\".\",\".\",\".\",\".\",\".\",\".\"] }";
        Check("a speck instead of a creature → refused (too small)", PetDesigner.ParseDesign(tiny).error is { } e3 && e3.Contains("small"), (PetDesigner.ParseDesign(tiny).error ?? "") + " … " + tiny[^200..].Replace('\n', ' '));
        var (moved, _) = PetDesigner.ParseDesign(PuddingReply.Replace("\"eyeY\": 11", "\"eyeY\": 2"));
        Check("a face placed off the body (on the cherry stem) is moved down onto the body", moved != null && moved.EyeY >= 4 && moved.MouthY >= moved.EyeY + 2, $"eyeY={moved?.EyeY}");
        var tpl = PetDesigner.ParseTemplate("{\"template\":\"Owl\",\"hue\":400,\"accentHue\":20,\"name\":\"Hoot\",\"species\":\"Night owl\",\"personality\":\"Sleepy\"}");
        Check("the fallback answer (a built-in body in the AI's colours) is read; hue wrapped into 0–359", tpl is { template: PetSpecies.Owl, hue: 40 } && tpl.Value.words.Name == "Hoot", "");
    }

    /// <summary>The hatch itself, with a stand-in for the owner's AI.</summary>
    static void AiHatch()
    {
        Console.WriteLine("== hatching through the owner's AI");
        var settings = new SettingsService();
        string path = Path.Combine(settings.DataRoot, "pet.json");

        PetService Fresh()
        {
            File.Delete(path);
            var svc = new PetService(settings, new LocalizationService());
            svc.State.Habits = ReadyHabits();
            svc.State.Habits.Bootstrapped = true;
            return svc;
        }

        var none = Fresh();
        none.TryHatch();
        Check("no AI ready → the egg waits (no hatch, no attempt counted, no formula)", !none.IsHatched && none.State.HatchAttempts == 0 && !none.HatchPending, "");

        var calls = new List<string>();
        var svc = Fresh();
        svc.DesignerOverride = (sys, req, ct) =>
        {
            calls.Add(req);
            return Task.FromResult((calls.Count == 1 ? "Sure! A cat. {\"name\": \"Mew\"}" : PuddingReply, "TestAI · model-x"));
        };
        svc.NoteTurnStarted();
        svc.TryHatch();
        Check("never in the middle of a reply the AI is writing", calls.Count == 0 && !svc.IsHatched, "");
        svc.NoteTurnDone("Anthropic", false, true);
        svc.TryHatch();
        Check("…nor in the 30 s right after one", calls.Count == 0, "");

        var svc2 = Fresh();
        calls.Clear();
        svc2.DesignerOverride = svc.DesignerOverride;
        svc2.TryHatch();
        for (int i = 0; i < 50 && svc2.HatchPending; i++) Thread.Sleep(20);
        Check("an unusable first answer is sent back with the reason, the corrected one hatches the AI's own creature",
            svc2.IsHatched && calls.Count == 2 && calls[1].Contains("could not be used") && svc2.State.Design?.Name == "Purin"
            && svc2.State.Name == "Purin" && svc2.State.Personality == PetPersonality.Caring && svc2.State.Design!.DesignedBy == "TestAI · model-x",
            $"calls={calls.Count} name={svc2.State.Name}");
        Check("its title, what it is (in the app's language) and who designed it are shown",
            svc2.Title == "Purin" && svc2.Subtitle.StartsWith("Caramel pudding") && svc2.Details.Contains("TestAI · model-x"), svc2.Subtitle);
        bool usedOwn = Enumerable.Range(0, 6).Select(i => svc2.Line(PetMoment.Idle, i)).Any(l => l is "I wobble, but I don't break" or "Snack break?" or "Celebrate when it builds");
        Check("its small talk includes the catchphrases the AI wrote for it", usedOwn, "");
        var reread = new PetService(settings, new LocalizationService());
        Check("the AI's creature is still itself after a restart (design, colours, voice)", reread.State.Design?.Body.Length == 24 && reread.State.Design.Palette["c"] == "#9C5A2E" && reread.Title == "Purin", "");

        var svc3 = Fresh();
        int n3 = 0;
        svc3.DesignerOverride = (sys, req, ct) =>
        {
            n3++;
            return Task.FromResult((req.Contains("Drawing is not needed")
                ? "{\"template\":\"Fox\",\"hue\":25,\"accentHue\":200,\"name\":\"Kitsu\",\"species\":\"Ember fox\",\"speciesTh\":\"จิ้งจอกถ่านไฟ\",\"personality\":\"Sassy\",\"catchphrasesEn\":[\"Hot take incoming\"]}"
                : "I can't draw today.", "TestAI"));
        };
        svc3.TryHatch();
        for (int i = 0; i < 50 && svc3.HatchPending; i++) Thread.Sleep(20);
        Check("two unusable drawings → it asks for a built-in body in its own colours, name and voice",
            svc3.IsHatched && n3 == 3 && svc3.State.Design == null && svc3.State.Species == PetSpecies.Fox && svc3.State.Name == "Kitsu"
            && svc3.State.Personality == PetPersonality.Sassy && svc3.SpeciesText == "Ember fox", $"calls={n3} {svc3.SpeciesText}");

        var svc4 = Fresh();
        int n4 = 0;
        svc4.DesignerOverride = (sys, req, ct) => { n4++; return Task.FromResult(("no", "TestAI")); };
        svc4.TryHatch();
        for (int i = 0; i < 50 && svc4.HatchPending; i++) Thread.Sleep(20);
        Check("nothing usable at all → still an egg, one attempt counted, asked again only after an hour",
            !svc4.IsHatched && svc4.State.HatchAttempts == 1 && n4 == 3, $"attempts={svc4.State.HatchAttempts} calls={n4}");
        svc4.TryHatch();
        Check("…(no new request before then)", n4 == 3, "");
        svc4.State.HatchAttempts = PetService.MaxAiAttempts - 1;
        svc4.State.LastHatchAttemptUtc = DateTime.UtcNow.AddHours(-2);
        svc4.TryHatch();
        for (int i = 0; i < 50 && svc4.HatchPending; i++) Thread.Sleep(20);
        Check("after 5 failed attempts the formula decides, so no egg is stuck forever", svc4.IsHatched && svc4.State.Design == null && svc4.State.Name.Length > 0, svc4.State.Name);
        File.Delete(path);
    }

    static void Decisions()
    {
        Console.WriteLine("== what hatches");
        var night = Habits(5, 600, 80, 60, m => 23 + (m % 4) - (m % 4 >= 1 ? 24 : 0));   // 23:00–02:59
        var morning = Habits(5, 600, 80, 60, m => 6 + m % 3);
        int owlNight = 0, owlMorning = 0, bunnyNight = 0, bunnyMorning = 0;
        var seen = new HashSet<PetSpecies>();
        for (int i = 0; i < 1500; i++)
        {
            var sn = new PetState { Seed = 1000 + i, Habits = night }; PetBrain.Hatch(sn, false, DateTime.UtcNow);
            var sm = new PetState { Seed = 1000 + i, Habits = morning }; PetBrain.Hatch(sm, false, DateTime.UtcNow);
            if (sn.Species == PetSpecies.Owl) owlNight++;
            if (sm.Species == PetSpecies.Owl) owlMorning++;
            if (sn.Species == PetSpecies.Bunny) bunnyNight++;
            if (sm.Species == PetSpecies.Bunny) bunnyMorning++;
            seen.Add(sn.Species); seen.Add(sm.Species);
        }
        Check("night workers hatch owls far more often than morning people", owlNight > owlMorning * 2, $"owls: night {owlNight} / morning {owlMorning}");
        Check("morning people hatch bunnies more often", bunnyMorning > bunnyNight * 2, $"bunnies: morning {bunnyMorning} / night {bunnyNight}");
        Check("every species can still hatch (habits tilt the odds, they don't decide)", seen.Count == Enum.GetValues<PetSpecies>().Length, $"{seen.Count} species seen");

        var s1 = new PetState { Seed = 42, Habits = night }; PetBrain.Hatch(s1, true, DateTime.UtcNow);
        var s2 = new PetState { Seed = 42, Habits = night }; PetBrain.Hatch(s2, true, DateTime.UtcNow);
        Check("the same person and habits always hatch the same pet", s1.Species == s2.Species && s1.Personality == s2.Personality && s1.Name == s2.Name && s1.Hue == s2.Hue, $"{s1.Species}/{s1.Personality}/{s1.Name}");
        Check("a Thai-speaking owner gets a Thai name", s1.Name.Any(c => c >= '฀' && c <= '๿'), s1.Name);
        var fox = Enumerable.Range(0, 3000).Select(i => { var s = new PetState { Seed = 7 + i, Habits = morning }; PetBrain.Hatch(s, false, DateTime.UtcNow); return s; })
            .Where(s => s.Species == PetSpecies.Fox).ToList();
        Check("foxes come out orange (hue 14–38)", fox.Count > 0 && fox.All(s => s.Hue is >= 14 and <= 38), $"{fox.Count} foxes");

        var sleepyNight = 0; var sleepyMorning = 0;
        for (int i = 0; i < 1500; i++)
        {
            var sn = new PetState { Seed = 5000 + i, Habits = night }; PetBrain.Hatch(sn, false, DateTime.UtcNow);
            var sm = new PetState { Seed = 5000 + i, Habits = morning }; PetBrain.Hatch(sm, false, DateTime.UtcNow);
            if (sn.Personality == PetPersonality.Sleepy) sleepyNight++;
            if (sm.Personality == PetPersonality.Sleepy) sleepyMorning++;
        }
        Check("night owners get sleepy pets more often", sleepyNight > sleepyMorning * 2, $"sleepy: night {sleepyNight} / morning {sleepyMorning}");
    }

    // ─── the world ───────────────────────────────────────────────────────

    static readonly PetMonitor Screen1 = new(new PetRect(0, 0, 1920, 1080), new PetRect(0, 0, 1920, 1032));

    static void Floors()
    {
        Console.WriteLine("== floors");
        var a = new PetWindow(1, new PetRect(100, 300, 900, 800));
        var b = new PetWindow(2, new PetRect(500, 200, 1200, 600));
        var max = new PetWindow(3, new PetRect(0, 0, 1920, 1032));
        var floors = PetWorld.Floors([Screen1], [b, a, max], 90);
        var task = floors.Where(f => f.Kind == PetFloorKind.Taskbar).ToList();
        Check("the taskbar's top edge is a floor (work area bottom 1032)", task.Count == 1 && task[0].Y == 1032 && task[0].X1 == 0, $"{task.Count}");
        var fa = floors.Where(f => f.Owner == 1).ToList();
        Check("window A's top edge is a floor only where window B (above it) does not cover it",
            fa.Count == 1 && fa[0].Y == 300 && fa[0].X1 == 100 && fa[0].X2 == 499, string.Join(" ", fa.Select(f => $"{f.X1}-{f.X2}@{f.Y}")));
        var fb = floors.Where(f => f.Owner == 2).ToList();
        Check("window B (on top) is a floor along its whole top", fb.Count == 1 && fb[0].Y == 200 && fb[0].X1 == 500 && fb[0].X2 == 1199, "");
        Check("a maximized window (top at the screen top) is no floor — the pet would stand off-screen", floors.All(f => f.Owner != 3), "");
        var covered = PetWorld.Floors([Screen1], [new PetWindow(9, new PetRect(0, 100, 1920, 900)), a], 90);
        Check("a window entirely under another has no floor", covered.All(f => f.Owner != 1), "");
        var narrow = PetWorld.Floors([Screen1], [new PetWindow(5, new PetRect(10, 500, 100, 600))], 90);
        Check("a tiny window (90 px) is no floor", narrow.All(f => f.Owner != 5), "");
    }

    static void Physics()
    {
        Console.WriteLine("== physics");
        var a = new PetWindow(1, new PetRect(100, 300, 900, 800));
        var b = new PetWindow(2, new PetRect(500, 200, 1200, 600));
        var floors = PetWorld.Floors([Screen1], [b, a], 90);
        var screen = Screen1.Bounds;
        var body = new PetBody { X = 300, Y = 50 };
        bool landed = false;
        for (int i = 0; i < 200 && !landed; i++) landed = PetWorld.Step(body, floors, 1 / 30.0, screen);
        Check("dropped above window A, it falls and lands on A's top edge", landed && body.Grounded && body.Y == 300 && body.Floor?.Owner == 1, $"y={body.Y} owner={body.Floor?.Owner}");

        body.Vx = 200;
        bool fell = false;
        for (int i = 0; i < 120 && body.Grounded; i++) { PetWorld.Step(body, floors, 1 / 30.0, screen); fell = !body.Grounded; }
        Check("walking past the covered part, it steps off the edge", fell && body.X > 499, $"x={body.X:F0}");
        landed = false;
        for (int i = 0; i < 200 && !landed; i++) landed = PetWorld.Step(body, floors, 1 / 30.0, screen);
        Check("…and lands on the taskbar (falls in front of windows, not onto their middles)", landed && body.Y == 1032 && body.Floor?.Kind == PetFloorKind.Taskbar, $"y={body.Y}");

        var (vx, vy) = PetWorld.JumpTo(body, 800, 200);
        body.Vx = vx; body.Vy = vy; body.Grounded = false; body.Floor = null;
        landed = false;
        for (int i = 0; i < 300 && !landed; i++) landed = PetWorld.Step(body, floors, 1 / 60.0, screen);
        Check("a jump from the taskbar lands on window B's top edge", landed && body.Floor?.Owner == 2 && body.Y == 200, $"landed on {body.Floor?.Owner} at y={body.Y} x={body.X:F0}");

        var moved = PetWorld.Floors([Screen1], [new PetWindow(2, new PetRect(560, 250, 1260, 650)), a], 90);
        double x0 = body.X;
        PetWorld.Follow(body, moved);
        Check("window B moved +60/+50: the pet rides along", body.Grounded && body.Y == 250 && Math.Abs(body.X - (x0 + 60)) < 0.01, $"x {x0:F0}→{body.X:F0} y={body.Y}");
        PetWorld.Follow(body, PetWorld.Floors([Screen1], [a], 90));
        Check("window B closed: the pet falls", !body.Grounded && body.Floor == null, "");

        var lost = new PetBody { X = 50, Y = 2000, Vy = 500 };
        PetWorld.Step(lost, floors, 1 / 30.0, screen);
        Check("below every floor (off-screen): caught on the ground under it", lost.Grounded && lost.Y == 1032, $"y={lost.Y}");
    }

    static void Lines()
    {
        Console.WriteLine("== lines");
        bool all = true; string missing = "";
        foreach (var m in Enum.GetValues<PetMoment>())
            foreach (var thai in new[] { true, false })
            {
                if (PetLines.Say(m, null, thai, "Mochi", 0).Length == 0) { all = false; missing += $"{m}/{(thai ? "th" : "en")} "; }
                foreach (var p in Enum.GetValues<PetPersonality>())
                    if (PetLines.Say(m, p, thai, "Mochi", 3).Length == 0) { all = false; missing += $"{m}/{p} "; }
            }
        Check("every moment has a line in Thai and English, for every personality", all, missing);
        Check("{name} is filled in", PetLines.Say(PetMoment.Hatched, PetPersonality.Shy, true, "โมจิ", 0).Contains("โมจิ"), "");
        Check("a grumpy pet sounds grumpy when the build breaks", PetLines.Say(PetMoment.BuildFailed, PetPersonality.Grumpy, false, "x", 0) != PetLines.Say(PetMoment.BuildFailed, null, false, "x", 0), "");
        Check("species and personality have Thai names", PetLines.SpeciesName(PetSpecies.Fairy, true) == "นางฟ้า" && PetLines.PersonalityName(PetPersonality.Sleepy, true) == "ขี้เซา", "");
    }

    static void Persistence()
    {
        Console.WriteLine("== pet.json");
        var settings = new SettingsService();
        string path = Path.Combine(settings.DataRoot, "pet.json");
        string realHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex");
        Check("the harness's pet.json is in its portable data folder, never the real ~/.cluadex",
            !Path.GetFullPath(path).StartsWith(Path.GetFullPath(realHome), StringComparison.OrdinalIgnoreCase), path);
        File.Delete(path);
        var svc = new PetService(settings, new LocalizationService());
        long seed = svc.State.Seed;
        svc.Save();
        var again = new PetService(settings, new LocalizationService());
        Check("the egg survives a restart (same seed and colour)", again.State.Seed == seed && again.State.EggHue == svc.State.EggHue && !again.IsHatched, "");

        var h = ReadyHabits();
        again.State.Habits = h;
        again.State.Habits.Bootstrapped = true;
        int changed = 0;
        again.Changed += () => changed++;
        again.DesignerOverride = (sys, req, ct) => Task.FromResult((PuddingReply, "TestAI"));
        again.TryHatch();   // no window → hatches as soon as the AI answers
        for (int i = 0; i < 50 && again.HatchPending; i++) Thread.Sleep(20);
        Check("ready → TryHatch hatches it (the AI's creature, personality, name) and tells the UI", again.IsHatched && again.State.Name.Length > 0 && changed > 0,
            $"'{again.State.Name}' {again.SpeciesText}");
        var third = new PetService(settings, new LocalizationService());
        Check("…and it is still that pet after a restart", third.IsHatched && third.State.Name == again.State.Name && third.State.Design != null, "");
        Check("the title shows the name; the subtitle what it is", third.Title == third.State.Name && third.Subtitle.Contains("·"), third.Subtitle);
        File.WriteAllText(path, "{ not json");
        var broken = new PetService(settings, new LocalizationService());
        Check("a damaged pet.json is kept aside and a new egg starts (no crash)", !broken.IsHatched && File.Exists(path + ".unreadable"), "");
        File.Delete(path); File.Delete(path + ".unreadable");
    }

    // ─── live: the real PetWindow on the real desktop for ~20 s ───────────

    /// <summary>Shows the pet (portable data folder, never the user's own) and prints where it is twice a second:
    /// its window rectangle, the floor it stands on, and its state — the screenshots are taken from outside.</summary>
    static int Live(bool egg, bool hatchLive)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settings = new SettingsService();
        File.Delete(Path.Combine(settings.DataRoot, "pet.json"));
        var svc = new PetService(settings, new LocalizationService());
        if (!egg)
        {
            svc.State.Habits = ReadyHabits();
            svc.DesignerOverride = async (sys, req, ct) => { await Task.Delay(1500, ct); return (PuddingReply, "TestAI"); };
            if (!hatchLive) { svc.TryHatch(); for (int i = 0; i < 200 && svc.HatchPending; i++) Thread.Sleep(20); }
        }
        svc.State.Habits.Bootstrapped = true;   // no chat history in the harness
        var started = DateTime.Now;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            var w = app.Windows.OfType<CluadeX.Views.Pet.PetWindow>().FirstOrDefault();
            if (w != null)
            {
                var r = PetDesktop.WindowRect(new System.Windows.Interop.WindowInteropHelper(w).Handle);
                Console.WriteLine($"t={(DateTime.Now - started).TotalSeconds:F1} rect={r.Left},{r.Top},{r.Right},{r.Bottom} {w.DebugState}");
            }
            if (hatchLive && !svc.IsHatched && (DateTime.Now - started).TotalSeconds > 4) svc.TryHatch();
            if ((DateTime.Now - started).TotalSeconds > 20) { svc.Shutdown(); app.Shutdown(); }
        };
        app.Startup += (_, _) => { app.MainWindow = new Window(); svc.Start(); timer.Start(); Console.WriteLine($"pet: {svc.Title} · {svc.Subtitle}"); };
        app.Run();
        return 0;
    }

    // ─── art ─────────────────────────────────────────────────────────────

    const int Scale = 5;

    static void RenderSheet(string path)
    {
        var species = Enum.GetValues<PetSpecies>();
        var columns = new List<(PetPose pose, PetFace face)>
        {
            (PetPose.Stand, PetFace.Normal), (PetPose.Walk1, PetFace.Normal), (PetPose.Walk2, PetFace.Normal),
            (PetPose.Sit, PetFace.Happy), (PetPose.Sleep, PetFace.Sleepy), (PetPose.Dangle, PetFace.Surprised),
            (PetPose.Jump, PetFace.Love), (PetPose.Stand, PetFace.Angry), (PetPose.Stand, PetFace.Sad),
            (PetPose.Stand, PetFace.Smug), (PetPose.Stand, PetFace.Blink),
        };
        Save(path, columns.Count, species.Length + 1, (dc, cell) =>
        {
            for (int r = 0; r < species.Length; r++)
                for (int c = 0; c < columns.Count; c++)
                    Draw(dc, PetArt.Render(new PetLook(species[r], 30 * r + 10, 30 * r + 200, false), columns[c].pose, columns[c].face), 8 + c * cell, 8 + r * cell);
            double[] prog = [0, 0.4, 0.7, 0.95];
            for (int i = 0; i < 8; i++) Draw(dc, PetArt.RenderEgg(45 * i, prog[i % 4], 1000 + i), 8 + i * cell, 8 + species.Length * cell);
        });
    }

    /// <summary>Real hatchlings: random seeds through PetBrain.Hatch, to see the colours people actually get.</summary>
    static void RenderHatchlings(string path)
    {
        var h = Habits(4, 300, 60, 60, m => 10 + m % 12);
        Save(path, 8, 4, (dc, cell) =>
        {
            for (int i = 0; i < 32; i++)
            {
                var s = new PetState { Seed = 900_000 + i * 7919, Habits = h };
                PetBrain.Hatch(s, false, DateTime.UtcNow);
                Draw(dc, PetArt.Render(s.Look, PetPose.Stand, i % 3 == 0 ? PetFace.Happy : PetFace.Normal), 8 + (i % 8) * cell, 8 + (i / 8) * cell);
            }
        });
    }

    static void RenderDesign(string path)
    {
        var (d, err) = PetDesigner.ParseDesign(PuddingReply);
        if (d == null) { Console.WriteLine("design did not parse: " + err); return; }
        var look = new PetLook(PetSpecies.Cat, 0, 0, false, d);
        var cols = new (PetPose, PetFace)[]
        {
            (PetPose.Stand, PetFace.Normal), (PetPose.Walk1, PetFace.Normal), (PetPose.Walk2, PetFace.Happy), (PetPose.Sit, PetFace.Happy),
            (PetPose.Sleep, PetFace.Sleepy), (PetPose.Dangle, PetFace.Surprised), (PetPose.Jump, PetFace.Love), (PetPose.Stand, PetFace.Angry),
        };
        Save(path, cols.Length, 1, (dc, cell) =>
        {
            for (int i = 0; i < cols.Length; i++) Draw(dc, PetArt.Render(look, cols[i].Item1, cols[i].Item2), 8 + i * cell, 8);
        });
    }

    static void Save(string path, int cols, int rows, Action<DrawingContext, int> paint)
    {
        int cell = PetArt.Size * Scale + 8;
        int w = cell * cols + 8, hgt = cell * rows + 8;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x3A)), null, new Rect(0, 0, w, hgt));
            paint(dc, cell);
        }
        var rtb = new RenderTargetBitmap(w, hgt, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
        Console.WriteLine("sheet: " + path);
    }

    static void Draw(DrawingContext dc, uint[] px, double x, double y)
    {
        var bmp = BitmapSource.Create(PetArt.Size, PetArt.Size, 96, 96, PixelFormats.Pbgra32, null, px, PetArt.Size * 4);
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        group.Children.Add(new ImageDrawing(bmp, new Rect(x, y, PetArt.Size * Scale, PetArt.Size * Scale)));
        dc.DrawDrawing(group);
    }
}
