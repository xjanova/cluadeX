using CluadeX.Helpers;

namespace ArchTest;

/// <summary>ThinkTagStream: llama-server's inline &lt;think&gt; block → the chat's "&lt;thinking&gt;…&lt;/thinking&gt;" chunks.</summary>
internal static class ThinkTests
{
    public static void Run(Action<string, string, bool, string> check, Action<string, Action> run)
    {
        Console.WriteLine("── Thinking of local reasoning models (ThinkTagStream) ──");
        static (string Reply, string Thought) Drive(params string[] chunks)
        {
            var s = new ThinkTagStream();
            var reply = new System.Text.StringBuilder();
            var thought = new System.Text.StringBuilder();
            foreach (var part in chunks.SelectMany(s.Feed).Concat(s.Flush()))
            {
                if (part.StartsWith("<thinking>") && part.EndsWith("</thinking>")) thought.Append(part[10..^11]);
                else if (part.Contains("<think") || part.Contains("</think")) reply.Append("[RAW TAG]" + part);
                else reply.Append(part);
            }
            return (reply.ToString(), thought.ToString());
        }

        run("T1", () =>
        {
            var (r, t) = Drive("<think>\nThinking Process:\n1. 17 + 25 = 42\n</think>\n\nเท่ากับ 42");
            check("T1", "a whole <think> block: thinking apart, the reply is only the answer (no tags, no leading blank line)",
                t.Contains("17 + 25 = 42") && r == "เท่ากับ 42", $"reply=[{r}] thought=[{t}]");
        });
        run("T2", () =>
        {
            var (r, t) = Drive("<thi", "nk>คิด", "อยู่</th", "ink>", "\n", "ตอบ ", "42");
            check("T2", "tags split across stream chunks (\"<thi\" + \"nk>\", \"</th\" + \"ink>\") are still recognised",
                t == "คิดอยู่" && r == "ตอบ 42", $"reply=[{r}] thought=[{t}]");
        });
        run("T3", () =>
        {
            var (r, t) = Drive("a < b and x <th", "eta is fine");
            check("T3", "a reply with no think block passes through unchanged, even text that looks like the start of a tag",
                t == "" && r == "a < b and x <theta is fine", $"reply=[{r}]");
        });
        run("T4", () =>
        {
            var (r, t) = Drive("<think>still thinking when max_tokens hit");
            check("T4", "a reply cut off mid-thought: all of it is thinking, the reply stays empty (not raw tags)",
                t == "still thinking when max_tokens hit" && r == "", $"reply=[{r}] thought=[{t}]");
        });
        run("T5", () =>
        {
            var a = ThinkTagStream.Split("<think>plan</think>\n\nanswer");
            var b = ThinkTagStream.Split("plan opened by the template</think>answer");
            var c = ThinkTagStream.Split("just an answer");
            check("T5", "a whole tool-loop reply splits the same; a reply that only closes the tag (template opened it) too",
                a == ("answer", "plan") && b == ("answer", "plan opened by the template") && c == ("just an answer", ""),
                $"a={a} b={b} c={c}");
        });
    }
}
