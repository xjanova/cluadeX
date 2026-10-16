using System.IO;
using System.Diagnostics;
using CluadeX.Services;

namespace ToolTest;

/// <summary>
/// Restore points (CheckpointService): an agent tool that changes a file keeps the file as it was first, once per
/// step; going back puts every later change back and keeps the current state as a new point; branching writes the old
/// state into another copy of the project and leaves the project alone.
/// </summary>
internal static partial class Program
{
    static async Task Checkpoints()
    {
        string store = Path.Combine(Path.GetTempPath(), "cluadex-cp-" + Guid.NewGuid().ToString("N")[..8]);
        string proj = Path.Combine(Path.GetTempPath(), "cluadex-cpproj-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(proj);
        var cp = new CheckpointService(null, store);
        string savedWd = Fs.WorkingDirectory;
        try
        {
            Fs.WorkingDirectory = proj;
            string a = Path.Combine(proj, "a.txt");
            File.WriteAllText(a, "one\n");
            var scope = new AgentRunScope("chat-cp", proj);
            AgentRunScope.Enter(scope);

            scope.Step = 1;
            await T("read_file", ("path", "a.txt"));
            await T("edit_file", ("path", "a.txt"), ("find", "one"), ("replace", "two"));
            await T("edit_file", ("path", "a.txt"), ("find", "two"), ("replace", "three"));   // same step: kept once
            await T("write_file", ("path", "b.txt"), ("content", "new file\n"));
            scope.Step = 2;
            await T("edit_file", ("path", "a.txt"), ("find", "three"), ("replace", "four"));
            AgentRunScope.Enter(null);

            var pts = cp.PointsForSession(proj, "chat-cp");
            Check("CP1", "each step that changed files is one restore point", pts.Count == 2 && pts[0].Step == 1 && pts[1].Step == 2,
                string.Join(", ", pts.Select(p => $"#{p.Step}:{p.FileList}")));
            Check("CP2", "a file changed twice in a step is kept once, as it was before the step",
                pts.Count > 0 && pts[0].Files.Count(f => f.File.EndsWith("a.txt")) == 1);
            Check("CP3", "a file the step created is recorded as not existing before", pts.Count > 0 && pts[0].Files.Any(f => f.File.EndsWith("b.txt") && !f.Existed));
            Check("CP4", "the project shows up in the all-projects list", cp.Projects().Any(p => string.Equals(p.Project, proj, StringComparison.OrdinalIgnoreCase)));
            Check("CP5", "the day filter keeps today's points and drops other days",
                cp.Points(proj, DateTime.Today).Count == 2 && cp.Points(proj, DateTime.Today.AddDays(-3)).Count == 0);

            // Branch first (the project must stay as it is), then go back.
            string copy = Path.Combine(Path.GetTempPath(), "cluadex-cpcopy-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(copy);
            File.WriteAllText(Path.Combine(copy, "a.txt"), "four\n");
            File.WriteAllText(Path.Combine(copy, "b.txt"), "new file\n");
            var br = cp.RestoreInto(proj, pts[1].Id, copy);
            Check("CP6", "branching from step 2 writes the copy as it was before step 2, the project untouched",
                br.Ok && File.ReadAllText(Path.Combine(copy, "a.txt")) == "three\n" && File.ReadAllText(a) == "four\n",
                br.Message + " | copy=" + File.ReadAllText(Path.Combine(copy, "a.txt")));
            ForceDelete(copy);

            var back = cp.RestoreTo(proj, pts[0].Id);
            Check("CP7", "going back to step 1 restores the file and removes what the step created",
                back.Ok && File.ReadAllText(a) == "one\n" && !File.Exists(Path.Combine(proj, "b.txt")), back.Message);
            var undo = cp.Points(proj).FirstOrDefault();
            Check("CP8", "the state before going back is kept as the newest point", undo != null && undo.Id.StartsWith("restore-")
                && undo.Files.Any(f => f.File.EndsWith("a.txt") && f.Existed));
            if (undo != null)
            {
                var redo = cp.RestoreTo(proj, undo.Id);
                Check("CP9", "and going back to that point brings the work back", redo.Ok && File.ReadAllText(a) == "four\n"
                    && File.ReadAllText(Path.Combine(proj, "b.txt")) == "new file\n");
            }

            // Outside a run nothing is captured (a typed command, the editor's Save).
            int before = cp.Points(proj).Count;
            await T("edit_file", ("path", "a.txt"), ("find", "four"), ("replace", "five"));
            Check("CP10", "a tool used outside an agent run makes no restore point", cp.Points(proj).Count == before);
        }
        finally
        {
            AgentRunScope.Enter(null);
            Fs.WorkingDirectory = savedWd;
            ForceDelete(proj);
            ForceDelete(store);
        }
    }
}
