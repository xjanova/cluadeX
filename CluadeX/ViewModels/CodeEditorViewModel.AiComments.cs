using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// Code Editor toolbar ▸ AI comments: how much the AI comments the code it writes, in which language, and whether this
/// project keeps its own choice. The setting lives in AppSettings; CodeCommentPolicy turns it into the instruction every
/// code-writing path sends (system prompt, Claude Dev / Gemini Dev, sub-agents) — this file is only the editor's view of it.
/// </summary>
public partial class CodeEditorViewModel
{
    private ICommand? _setAiCommentLevel, _setAiCommentLanguage, _toggleAiCommentProject;

    public ICommand SetAiCommentLevelCommand => _setAiCommentLevel ??= new RelayCommand<string>(v => ApplyAiComment(level: v));
    public ICommand SetAiCommentLanguageCommand => _setAiCommentLanguage ??= new RelayCommand<string>(v => ApplyAiComment(language: v));
    public ICommand ToggleAiCommentProjectCommand => _toggleAiCommentProject ??= new RelayCommand(ToggleAiCommentProject);

    /// <summary>The project the editor shows — the same folder the agent's prompt resolves the setting for.</summary>
    private string? AiCommentProjectDir => _workspace.HasWorkingDirectory ? _workspace.WorkingDirectory : null;

    private (string Level, string Language, bool PerProject) AiComment => CodeCommentPolicy.Resolve(_settings.Settings, AiCommentProjectDir);

    public bool AiCommentIsNone => AiComment.Level == CodeCommentPolicy.None;
    public bool AiCommentIsKey => AiComment.Level == CodeCommentPolicy.Key;
    public bool AiCommentIsDetailed => AiComment.Level == CodeCommentPolicy.Detailed;
    public bool AiCommentIsThai => AiComment.Language == CodeCommentPolicy.Thai;
    public bool AiCommentIsEnglish => AiComment.Language == CodeCommentPolicy.English;
    public bool AiCommentIsMatch => AiComment.Language == CodeCommentPolicy.MatchFile;
    public bool AiCommentPerProject => AiComment.PerProject;
    public bool AiCommentCanPerProject => AiCommentProjectDir != null;
    public bool AiCommentIsOn => !AiCommentIsNone;

    /// <summary>The toolbar button's text: "AI comments: key points · Thai" (· this project when it has its own).</summary>
    public string AiCommentLabel
    {
        get
        {
            var (level, language, perProject) = AiComment;
            string state = T("edt.aic.short." + level);
            if (level != CodeCommentPolicy.None) state += " · " + T("edt.aic.lang." + language);
            if (perProject) state += " · " + T("edt.aic.short.project");
            return T("edt.aic.button", state);
        }
    }

    public string AiCommentProjectHeader => AiCommentProjectDir is { } dir
        ? T("edt.aic.project", Path.GetFileName(dir.TrimEnd('\\', '/')))
        : T("edt.aic.noProject");

    /// <summary>What the choice costs: nothing when off, else the instruction's own size (it rides in the cached system
    /// prompt) — the bigger cost, the longer answers, is on the level items themselves.</summary>
    public string AiCommentCostNote
    {
        get
        {
            var (level, language, _) = AiComment;
            string text = CodeCommentPolicy.Build(level, language);
            return text.Length == 0 ? T("edt.aic.costOff") : T("edt.aic.cost", Math.Max(1, (text.Length + 3) / 4));
        }
    }

    private void ApplyAiComment(string? level = null, string? language = null)
    {
        string? dir = AiCommentProjectDir;
        bool perProject = AiComment.PerProject;
        _settings.UpdateSettings(s =>
        {
            if (perProject && dir != null)
            {
                // Copy-on-write: the agent reads this dictionary off the UI thread while it builds a prompt.
                string key = CodeCommentPolicy.ProjectKey(dir);
                var map = new Dictionary<string, CodeCommentSetting>(s.CodeCommentProjects ?? new());
                map.TryGetValue(key, out var own);
                map[key] = new CodeCommentSetting
                {
                    Level = CodeCommentPolicy.NormalizeLevel(level ?? own?.Level),
                    Language = CodeCommentPolicy.NormalizeLanguage(language ?? own?.Language),
                };
                s.CodeCommentProjects = map;
            }
            else
            {
                if (level != null) s.CodeCommentLevel = CodeCommentPolicy.NormalizeLevel(level);
                if (language != null) s.CodeCommentLanguage = CodeCommentPolicy.NormalizeLanguage(language);
            }
        });
        RaiseAiComment();
    }

    /// <summary>On: this project starts from the choice in force now and keeps its own from then on. Off: it follows the
    /// global choice again (its own is dropped — nothing else is lost, the global pair never changed).</summary>
    private void ToggleAiCommentProject()
    {
        string? dir = AiCommentProjectDir;
        if (dir == null) return;
        var (level, language, perProject) = AiComment;
        string key = CodeCommentPolicy.ProjectKey(dir);
        _settings.UpdateSettings(s =>
        {
            var map = new Dictionary<string, CodeCommentSetting>(s.CodeCommentProjects ?? new());
            if (perProject) map.Remove(key);
            else map[key] = new CodeCommentSetting { Level = level, Language = language };
            s.CodeCommentProjects = map;
        });
        RaiseAiComment();
    }

    /// <summary>Re-read every AI-comment property: after a change, a language switch, or another project opening.</summary>
    private void RaiseAiComment()
    {
        foreach (var name in new[]
                 {
                     nameof(AiCommentIsNone), nameof(AiCommentIsKey), nameof(AiCommentIsDetailed), nameof(AiCommentIsThai),
                     nameof(AiCommentIsEnglish), nameof(AiCommentIsMatch), nameof(AiCommentPerProject),
                     nameof(AiCommentCanPerProject), nameof(AiCommentIsOn), nameof(AiCommentLabel),
                     nameof(AiCommentProjectHeader), nameof(AiCommentCostNote),
                 })
            OnPropertyChanged(name);
    }
}
