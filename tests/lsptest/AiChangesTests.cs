using System.IO;
using CluadeX.Services;
using CluadeX.Services.Problems;

namespace LspTest;

/// <summary>
/// What the AI changed (AiChanges + CheckpointService.BeforeLatestTurn):
///   A*  hunks (added / changed / deleted, at the start, middle and end), putting one back (LF, CRLF, no final line
///       end), refusing when the lines changed since, stepping, every hunk put back = the file before; and the
///       baseline = the file before the LATEST turn that touched it (its first step), a file the turn created
/// </summary>
internal static partial class Program
{
    static string Show(IEnumerable<AiChangeHunk> hs) => string.Join(" | ", hs.Select(h => $"{h.Kind}@{h.StartLine}+{h.LineCount} -[{string.Join(",", h.OldLines)}] +[{string.Join(",", h.NewLines)}]"));

    static void AiChangeMarks()
    {
        Console.WriteLine("A  what the AI changed");
        string before = "a\nb\nc\nd\n", after = "a\nB\nc\nx\ny\nd\n";
        var hs = AiChanges.Hunks(before, after);
        Check("A1", "a changed line and two added lines → Modified@2 and Added@4..5, with the old and new text",
            hs.Count == 2 && hs[0].Kind == AiChangeKind.Modified && hs[0].StartLine == 2 && hs[0].OldLines.SequenceEqual(new[] { "b" })
            && hs[1].Kind == AiChangeKind.Added && hs[1].StartLine == 4 && hs[1].LineCount == 2, Show(hs));

        string? r0 = AiChanges.Revert(after, hs[0]), r1 = AiChanges.Revert(after, hs[1]);
        var del = AiChanges.Hunks("a\nb\nc", "a\nc");
        string? r2 = del.Count == 1 ? AiChanges.Revert("a\nc", del[0]) : null;
        Check("A2", "putting one back changes only its lines (changed → old, added → gone, deleted → restored)",
            r0 == "a\nb\nc\nx\ny\nd\n" && r1 == "a\nB\nc\nd\n" && del.Count == 1 && del[0].Kind == AiChangeKind.Removed && del[0].StartLine == 2 && r2 == "a\nb\nc",
            $"{r0?.Replace("\n", "⏎")} / {r1?.Replace("\n", "⏎")} / {Show(del)} / {r2?.Replace("\n", "⏎")}");

        var crlf = AiChanges.Hunks("a\r\nb\r\n", "a\r\nB\r\nz\r\n");
        string text = "a\r\nB\r\nz\r\n";
        foreach (var h in crlf.AsEnumerable().Reverse()) text = AiChanges.Revert(text, h) ?? "FAILED";
        Check("A3", "CRLF files stay CRLF when changes are put back", text == "a\r\nb\r\n", text.Replace("\r", "␍").Replace("\n", "⏎"));

        var addEnd = AiChanges.Hunks("a\nb", "a\nb\nc");
        var delEnd = AiChanges.Hunks("a\nb", "a");
        var addStart = AiChanges.Hunks("b\n", "a\nb\n");
        Check("A4", "at the ends of a file with no final line end: added last line, deleted last line, added first line",
            addEnd.Count == 1 && AiChanges.Revert("a\nb\nc", addEnd[0]) == "a\nb"
            && delEnd.Count == 1 && delEnd[0].StartLine == 2 && AiChanges.Revert("a", delEnd[0]) == "a\nb"
            && addStart.Count == 1 && AiChanges.Revert("a\nb\n", addStart[0]) == "b\n",
            $"{Show(addEnd)} / {Show(delEnd)} / {Show(addStart)}");

        Check("A5", "nothing is put back over lines that changed since the marks were made",
            AiChanges.Revert("a\nB2\nc\nx\ny\nd\n", hs[0]) == null && AiChanges.Revert("a", hs[1]) == null);

        var step = AiChanges.Hunks("1\n2\n3\n4\n5\n6\n", "1\nX\n3\n4\nY\n6\n");
        Check("A6", "next / previous change from the caret, wrapping around",
            AiChanges.Step(step, 1, true)?.StartLine == 2 && AiChanges.Step(step, 2, true)?.StartLine == 5 && AiChanges.Step(step, 5, true)?.StartLine == 2
            && AiChanges.Step(step, 3, false)?.StartLine == 2 && AiChanges.Step(step, 2, false)?.StartLine == 5);

        // Property: from any edit, putting every hunk back (last first) gives the file before, LF and CRLF.
        var rnd = new Random(7);
        int bad = 0;
        string firstBad = "";
        for (int round = 0; round < 60; round++)
        {
            string eol = round % 2 == 0 ? "\n" : "\r\n";
            var lines = Enumerable.Range(1, 30).Select(i => "line " + i).ToList();
            var edited = new List<string>(lines);
            for (int k = rnd.Next(1, 6); k > 0; k--)
            {
                int at = rnd.Next(0, edited.Count);
                switch (rnd.Next(3))
                {
                    case 0: edited.Insert(at, "new " + rnd.Next(1000)); break;
                    case 1: if (edited.Count > 1) edited.RemoveAt(at); break;
                    default: edited[at] = "changed " + rnd.Next(1000); break;
                }
            }
            bool finalEol = rnd.Next(2) == 0;
            string b = string.Join(eol, lines) + (finalEol ? eol : ""), a = string.Join(eol, edited) + (rnd.Next(2) == 0 ? eol : "");
            string t = a;
            foreach (var h in AiChanges.Hunks(b, a).AsEnumerable().Reverse()) t = AiChanges.Revert(t, h) ?? "FAILED";
            if (t != b) { bad++; if (firstBad.Length == 0) firstBad = $"round {round}: {Show(AiChanges.Hunks(b, a))}\nwant {b.Replace("\r", "␍").Replace("\n", "⏎")}\ngot  {t.Replace("\r", "␍").Replace("\n", "⏎")}"; }
        }
        Check("A7", "60 random edits (LF and CRLF, with and without a final line end): every hunk put back = the file before", bad == 0, firstBad);

        // The baseline: the restore points the agent keeps, through the real CheckpointService.
        string cpRoot = Path.Combine(Scratch, "checkpoints"), project = Path.Combine(Scratch, "โปรเจกต์ AI");
        Directory.CreateDirectory(project);
        string file = Path.Combine(project, "Program.cs"), created = Path.Combine(project, "New.cs");
        var cp = new CheckpointService(null, cpRoot);
        try
        {
            File.WriteAllText(file, "v1\n");
            var turn1 = new AgentRunScope("chat-a", project) { Step = 1 };
            AgentRunScope.Enter(turn1);
            cp.CaptureBeforeChange(file); File.WriteAllText(file, "v2\n");
            var turn2 = new AgentRunScope("chat-a", project) { Step = 1 };
            AgentRunScope.Enter(turn2);
            cp.CaptureBeforeChange(file); File.WriteAllText(file, "v3\n");
            turn2.Step = 2;
            cp.CaptureBeforeChange(file); File.WriteAllText(file, "v4 สวัสดี\n");
            cp.CaptureBeforeChange(created); File.WriteAllText(created, "new\n");
        }
        finally { AgentRunScope.Enter(null); }
        var baseline = cp.BeforeLatestTurn(project, file);
        var fresh = cp.BeforeLatestTurn(project, created);
        var none = cp.BeforeLatestTurn(project, Path.Combine(project, "Untouched.cs"));
        Check("A8", "baseline = the file before the latest turn's FIRST step (v2, not v1 or v3); a file the turn created = empty; untouched = none",
            baseline is { Existed: true, Text: "v2\n" } && fresh is { Existed: false, Text: null } && none == null,
            $"{baseline} / {fresh} / {none}");
        var marks = AiChanges.Hunks(baseline?.Text, File.ReadAllText(file));
        Check("A9", "…and the marks against it show the whole turn's change (v2 → v4)", marks.Count == 1 && marks[0].NewLines[0] == "v4 สวัสดี", Show(marks));
    }
}
