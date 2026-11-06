using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace CluadeX.Models;

/// <summary>
/// One question of a question card that asks several at once (owner 2026-11-06: "การถามแบบหลายสเต็ป ย้อนกลับหรือเดินหน้าได้").
/// Every provider asks through the same card: Claude Code's AskUserQuestion, and ask_user with <c>questions</c> on the
/// native loop and on the CLIs' MCP tool. <paramref name="Notes"/>: what each option means, shown under the question.
/// </summary>
public sealed record AskStep(string Question, IReadOnlyList<string> Options, IReadOnlyList<string> Notes, bool Multi)
{
    public AskStep(string question, IReadOnlyList<string> options) : this(question, options, Array.Empty<string>(), false) { }

    /// <summary>The questions and their answers, numbered, for the model ("1. Which db? → SQLite"). Pure.</summary>
    public static string AnswersText(IReadOnlyList<AskStep> steps, IReadOnlyList<string> answers)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < steps.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(i + 1).Append(". ").Append(steps[i].Question.Trim()).Append(" → ")
              .Append(i < answers.Count && answers[i].Length > 0 ? answers[i] : "(no answer)");
        }
        return sb.ToString();
    }
}

/// <summary>An option button of a question step: picked or not (several can be picked on a multi-select step).</summary>
public sealed class AskChoice : INotifyPropertyChanged
{
    public AskChoice(string label) { Label = label; }
    public string Label { get; }
    private bool _picked;
    public bool Picked { get => _picked; set { if (_picked == value) return; _picked = value; Changed(); Changed(nameof(Shown)); } }
    /// <summary>The button's text: a tick in front once picked.</summary>
    public string Shown => _picked ? "✓ " + Label : Label;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A question step on the card: its question, its option buttons and what was typed for it.</summary>
public sealed class AskStepState
{
    public AskStepState(AskStep step)
    {
        Step = step;
        Choices = step.Options.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => new AskChoice(o.Trim())).ToList();
    }

    public AskStep Step { get; }
    public List<AskChoice> Choices { get; }
    /// <summary>The free answer typed for this step (kept while the user steps back and forth).</summary>
    public string Draft { get; set; } = "";

    /// <summary>The card text of this step: the question, then what each option means.</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder("❓ ").Append(Step.Question.Trim());
            foreach (var note in Step.Notes) sb.Append("\n- ").Append(note);
            return sb.ToString();
        }
    }

    /// <summary>The answer: the picked options, then anything typed ("" = not answered yet).</summary>
    public string Answer =>
        string.Join(", ", Choices.Where(c => c.Picked).Select(c => c.Label)
            .Concat(Draft.Trim() is { Length: > 0 } typed ? new[] { typed } : Array.Empty<string>()));
}
