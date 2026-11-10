using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ThemeTest;

internal static partial class Program
{
    static bool SettingsOnly;

    static readonly string[] SettingsCategories = { "provider", "model", "agent", "autonomous", "memory", "voice", "local", "general" };

    /// <summary>
    /// The Settings page after the clean-up (owner 2026-11-10: "หน้า settings ดูรก … ตั้งค่า provider ซ้ำซ้อน"): every
    /// category in light (Classic) and dark (Midnight), in Thai, as the window shows it and as the whole scrolled page,
    /// plus the connection card for one provider of each kind. Saved to renders\settings\ for the eye; the checks are that
    /// the card hides what a provider does not read.
    /// </summary>
    static async Task SettingsShots()
    {
        Log("\n## Settings page (rendered for the eye)");
        string dir = Path.Combine(OutRoot, "renders", "settings");
        var themes = ThemeServiceOrNull();
        var loc = Sp.GetRequiredService<LocalizationService>();
        var svm = MainVm.SettingsVM;
        var wasProvider = svm.SelectedProvider;
        MainWin.Width = W; MainWin.Height = H;
        loc.SetLanguage("th");
        MainVm.NavigateToCommand.Execute("Settings");
        await Idle(300);

        var view = FindAll<SettingsView>(MainWin).FirstOrDefault();
        Check("settings: the page is on the window", view != null);
        if (view == null) return;
        var picker = view.FindName("ProviderPicker") as ComboBox;
        Check("settings: the provider picker is grouped (cloud / CLI / compatible / this PC / other)",
            picker?.ItemsSource is System.Windows.Data.ListCollectionView lcv && lcv.Groups?.Count == 5 && picker.GroupStyle.Count == 1,
            $"{(picker?.ItemsSource as System.Windows.Data.ListCollectionView)?.Groups?.Count} groups");

        var providers = new[]
        {
            (AiProviderType.Anthropic, "anthropic"), (AiProviderType.LMStudio, "lmstudio"), (AiProviderType.ClaudeDev, "claude-cli"),
            (AiProviderType.LlamaServer, "llama-server"), (AiProviderType.AzureOpenAI, "azure"), (AiProviderType.BrainX, "brainx"),
        };
        var sets = new[] { ("Classic", "light"), ("Midnight", "dark") };
        foreach (var (id, label) in sets)
        {
            if (themes != null) { Invoke(themes, "Apply", id, false); await Idle(200); }
            svm.SelectedProvider = AiProviderType.Anthropic;
            foreach (string cat in SettingsCategories)
            {
                svm.SelectedCategory = cat;
                await SaveSettingsShot(view, Path.Combine(dir, $"{label}-{cat}"), $"{label}/{cat}");
            }
            svm.SelectedCategory = "provider";
            foreach (var (t, name) in providers)
            {
                svm.SelectedProvider = t;
                await SaveSettingsShot(view, Path.Combine(dir, $"{label}-provider-{name}"), $"{label}/provider/{name}");
                if (label == "light") CardChecks(view, t, name);
            }
        }

        // the English labels once, so the translated keys can be eyed against the Thai
        loc.SetLanguage("en");
        svm.SelectedProvider = AiProviderType.Anthropic;
        foreach (string cat in new[] { "provider", "model", "local" })
        {
            svm.SelectedCategory = cat;
            await SaveSettingsShot(view, Path.Combine(dir, $"light-en-{cat}"), $"light-en/{cat}");
        }

        svm.SelectedProvider = wasProvider;
        svm.SelectedCategory = "general";
        MainVm.NavigateToCommand.Execute("Chat");
        await Idle();
        Log($"rendered the settings page → {dir}");
    }

    /// <summary>What the connection card shows for one provider, read off the live page (not the view model).</summary>
    static void CardChecks(SettingsView view, AiProviderType t, string name)
    {
        bool Shown(string bindingPath) => FindAll<TextBox>(view).Any(e => e.IsVisible
            && e.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == bindingPath);
        var svm = MainVm.SettingsVM;
        bool model = FindAll<ComboBox>(view).Any(e => e.IsVisible
            && e.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedItemProperty)?.ParentBinding.Path.Path == "ProviderModel");
        Check($"settings card [{name}]: the model box shows only where a model is picked here", model == svm.ShowModelFields,
            $"visible={model}, expected={svm.ShowModelFields}");
        Check($"settings card [{name}]: the address box follows ShowBaseUrlBox", Shown("ProviderBaseUrl") == svm.ShowBaseUrlBox,
            $"visible={Shown("ProviderBaseUrl")}, expected={svm.ShowBaseUrlBox}");
        Check($"settings card [{name}]: the key box shows only where a key is used", Shown("ProviderApiKey") == svm.NeedsApiKey,
            $"visible={Shown("ProviderApiKey")}, expected={svm.NeedsApiKey}");
        Check($"settings card [{name}]: the workspace box is Anthropic's only", Shown("ProviderWorkspaceId") == (t == AiProviderType.Anthropic),
            $"visible={Shown("ProviderWorkspaceId")}");
    }

    /// <summary>The window as it is, and the whole scrolled settings column at its full height.</summary>
    static async Task SaveSettingsShot(SettingsView view, string pathNoExt, string label)
    {
        FreezeVolatile();
        var bmp = await Stable(() => (RootVisual(MainWin), W, H), label);
        SavePng(bmp, pathNoExt + ".png");

        var scroll = FindAll<ScrollViewer>(view).Where(s => s.IsVisible).OrderByDescending(s => s.ExtentHeight).FirstOrDefault();
        if (scroll?.Content is not FrameworkElement content || content.ActualHeight < 1) return;
        int w = (int)Math.Ceiling(content.ActualWidth), h = (int)Math.Ceiling(content.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            Brush bg = view.Background ?? (Brush)MainWin.TryFindResource("Bg.Primary") ?? MainWin.Background ?? Brushes.White;
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, Math.Min(h, 12000), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        SavePng(rtb, pathNoExt + "-full.png");
    }
}
