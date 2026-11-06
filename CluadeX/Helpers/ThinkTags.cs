using System.Text;

namespace CluadeX.Helpers;

/// <summary>
/// Local reasoning models (Qwen 3 / 3.5, DeepSeek R1, …) served by llama-server with --reasoning-format none write
/// their thinking inline as &lt;think&gt;…&lt;/think&gt; before the answer. The chat routes thinking only when it arrives as
/// a whole "&lt;thinking&gt;…&lt;/thinking&gt;" chunk (<c>ChatViewModel.TryUnwrapThinking</c>), so the raw tags and a page of
/// "Thinking Process:" landed in the reply bubble — and were saved and sent back as the model's own answer
/// (2026-11-06, Qwen3.5-4B on llama.cpp b12144). This turns the stream into that convention. A tag split across
/// chunks ("&lt;thi" + "nk&gt;") is held back until it is whole.
/// </summary>
public sealed class ThinkTagStream
{
    private const string Open = "<think>", Close = "</think>";
    private bool _inThought;
    private bool _afterClose;
    private string _carry = "";

    /// <summary>The chunks to pass on for one streamed piece: plain reply text, or "&lt;thinking&gt;…&lt;/thinking&gt;".</summary>
    public List<string> Feed(string chunk)
    {
        var output = new List<string>();
        string text = _carry + chunk;
        _carry = "";
        while (text.Length > 0)
        {
            string tag = _inThought ? Close : Open;
            int at = text.IndexOf(tag, StringComparison.Ordinal);
            if (at >= 0)
            {
                Emit(output, text[..at]);
                _inThought = !_inThought;
                text = text[(at + tag.Length)..];
                if (!_inThought) _afterClose = true;   // the blank line after </think> may come in the next chunks
                continue;
            }
            // Hold back a tail that could be the start of the tag.
            int keep = PartialTagAtEnd(text, tag);
            Emit(output, text[..^keep]);
            _carry = text[^keep..];
            break;
        }
        return output;
    }

    /// <summary>Whatever is held back at the end of the stream.</summary>
    public List<string> Flush()
    {
        var output = new List<string>();
        Emit(output, _carry);
        _carry = "";
        return output;
    }

    private void Emit(List<string> output, string piece)
    {
        if (!_inThought && _afterClose)
        {
            piece = piece.TrimStart('\r', '\n');
            if (piece.Length > 0) _afterClose = false;
        }
        if (piece.Length == 0) return;
        output.Add(_inThought ? "<thinking>" + piece + "</thinking>" : piece);
    }

    private static int PartialTagAtEnd(string text, string tag)
    {
        for (int n = Math.Min(tag.Length - 1, text.Length); n > 0; n--)
            if (text.EndsWith(tag[..n], StringComparison.Ordinal)) return n;
        return 0;
    }

    /// <summary>
    /// A whole (non-streamed) reply split into answer and thinking. A reply that only closes the tag (the chat template
    /// opened it in the prompt) has everything before "&lt;/think&gt;" as thinking.
    /// </summary>
    public static (string Text, string Thinking) Split(string? reply)
    {
        if (string.IsNullOrEmpty(reply) || !reply.Contains(Close, StringComparison.Ordinal) && !reply.Contains(Open, StringComparison.Ordinal))
            return (reply ?? "", "");
        var text = new StringBuilder();
        var thought = new StringBuilder();
        string rest = reply;
        int firstOpen = rest.IndexOf(Open, StringComparison.Ordinal);
        int firstClose = rest.IndexOf(Close, StringComparison.Ordinal);
        if (firstClose >= 0 && (firstOpen < 0 || firstClose < firstOpen))
        {
            thought.Append(rest[..firstClose]);
            rest = rest[(firstClose + Close.Length)..];
        }
        var stream = new ThinkTagStream();
        foreach (var part in stream.Feed(rest).Concat(stream.Flush()))
        {
            if (part.StartsWith("<thinking>", StringComparison.Ordinal) && part.EndsWith("</thinking>", StringComparison.Ordinal))
                thought.Append(part["<thinking>".Length..^"</thinking>".Length]);
            else text.Append(part);
        }
        return (text.ToString().Trim(), thought.ToString().Trim());
    }
}
