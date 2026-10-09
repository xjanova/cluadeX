using System.Collections.Concurrent;
using System.IO;
using CluadeX.Models;
using CluadeX.Services.Providers;

namespace CluadeX.Services.Vision;

/// <summary>
/// The provider-neutral pieces of sending tool images to a model: the text markers that stand in for an image
/// wherever text goes (tool output, chat rows, codex.db, an omitted-image note), data URLs, the token estimate,
/// and the "keep only the newest N images" pass over a native conversation.
///
/// Model-facing notes are English on purpose (like every other tool output); they never contain base64.
/// </summary>
public static class ToolImageWire
{
    /// <summary>Most images one tool result may carry (DoS guard: a server returning hundreds of frames).</summary>
    public const int MaxImagesPerResult = 8;

    /// <summary>"data:image/png;base64,…" for OpenAI-style image_url / input_image.</summary>
    public static string DataUrl(ToolImage img) => $"data:{img.MediaType};base64,{img.Base64}";

    /// <summary>What a vision model is charged for one image: ceil(width × height / 750) tokens (Anthropic's
    /// published rule, close for the others). Unknown size counts as a full 1.15 MP image.</summary>
    public static int EstimateTokens(ToolImage img) =>
        img.Width > 0 && img.Height > 0 ? (int)Math.Ceiling(img.Width * (double)img.Height / 750.0) : 1534;

    public static int EstimateTokens(IEnumerable<ToolImage>? images) => images?.Sum(EstimateTokens) ?? 0;

    /// <summary>The text that stands in for image N in a tool's Output: "[image N: WxH attached, saved at …]".</summary>
    public static string AttachedMarker(int n, ToolImage img) => AttachedMarker(n, img, 0, 0, null);

    /// <summary>
    /// The marker for image N. When the image was shrunk from <paramref name="sourceWidth"/>×<paramref name="sourceHeight"/>
    /// it says so, with the factor that turns a point seen in the image into the original's pixels — a tool that
    /// clicks in screenshot pixels (Claude in Chrome's computer, Roblox user_mouse_input, Playwright's xy clicks)
    /// works in the ORIGINAL frame, so a model clicking where it sees would miss by up to the scale factor:
    /// "[image 1: 1429x804 attached — downscaled from 1920x1080: multiply x and y by 1.3436 for coordinates in the
    /// original, saved at …]". <paramref name="saveError"/> replaces the "saved at" part ("; not saved: …").
    /// The factor is written with '.' whatever the UI culture.
    /// </summary>
    public static string AttachedMarker(int n, ToolImage img, int sourceWidth, int sourceHeight, string? saveError)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"[image {n}: {img.Width}x{img.Height} attached");
        if (ScaleFactor(img.Width, img.Height, sourceWidth, sourceHeight) is double f)
            sb.Append($" — downscaled from {sourceWidth}x{sourceHeight}: multiply x and y by ")
              .Append(f.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
              .Append(" for coordinates in the original");
        if (!string.IsNullOrEmpty(saveError)) sb.Append("; ").Append(saveError);
        else if (img.FilePath != null) sb.Append(", saved at ").Append(img.FilePath);
        return sb.Append(']').ToString();
    }

    /// <summary>original ÷ sent, by the long edge (the more precise of the two after rounding), or null when the
    /// image was not shrunk.</summary>
    public static double? ScaleFactor(int width, int height, int sourceWidth, int sourceHeight)
    {
        if (width <= 0 || height <= 0 || sourceWidth <= 0 || sourceHeight <= 0) return null;
        if (sourceWidth == width && sourceHeight == height) return null;
        double f = Math.Max(sourceWidth, sourceHeight) / (double)Math.Max(width, height);
        return f > 1.0 ? f : null;
    }

    /// <summary>Image N could not be used at all (malformed, too large, unsupported).</summary>
    public static string FailedMarker(int n, string reason) => $"[image {n} could not be used: {reason}]";

    /// <summary>
    /// The note a model that cannot see images gets in place of them — it must know the image existed and that it
    /// did NOT see it, or it will claim it "looked". <paramref name="hint"/> names the text tools it has instead.
    /// <paramref name="noun"/>: "screenshot" for a capture, "image" for a file (read_file) or when unknown.
    /// </summary>
    public static string OmittedForBlindModel(IReadOnlyCollection<ToolImage> images, string? hint, string noun = "screenshot")
    {
        string what = images.Count == 1 ? $"{noun} omitted" : $"{images.Count} {noun}s omitted";
        string use = string.IsNullOrWhiteSpace(hint) ? "rely on the text output instead" : hint!;
        return $"[{what} — this model cannot see images; {use}{SavedAt(images)}]";
    }

    /// <summary>The note for images the API refused (a 400 that named the image) — the model did not see them,
    /// and the reason is the API's own words.</summary>
    public static string RefusedByApi(IReadOnlyCollection<ToolImage> images, string? why)
    {
        string what = images.Count == 1 ? "image omitted" : $"{images.Count} images omitted";
        string reason = string.IsNullOrWhiteSpace(why) ? "" : ": " + why!.Replace('\r', ' ').Replace('\n', ' ').Replace(']', ')');
        return $"[{what} — the API refused it{reason}{SavedAt(images)}]";
    }

    private static string SavedAt(IEnumerable<ToolImage> images)
    {
        var paths = images.Select(i => i.FilePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        return paths.Count == 0 ? "" : $" (saved at {string.Join(", ", paths)})";
    }

    /// <summary>The note an older image is replaced with when only the newest few are kept (the model saw it on an
    /// earlier step).</summary>
    public static string OmittedOld(ToolImage img) =>
        img.FilePath != null
            ? $"[screenshot omitted — saved at {img.FilePath}]"
            : "[screenshot omitted — an older image was dropped to save context]";

    /// <summary>An image of the newest step the model never saw: more than <see cref="MaxImagesPerStep"/> arrived at
    /// once, or the request had to shrink to fit the context window.</summary>
    public static string NotShown(ToolImage img, string why) =>
        $"[image not shown — {why}{(img.FilePath != null ? "; saved at " + img.FilePath : "")}]";

    /// <summary>Most images the newest step may carry into one request (several tools in parallel can each return
    /// up to <see cref="MaxImagesPerResult"/>; ~1,600 tokens apiece).</summary>
    public const int MaxImagesPerStep = 20;

    /// <summary>Which text tools to point a blind model at: the built-in browser's, else Claude in Chrome's or
    /// Playwright's (by leaf, so a bridge prefix like brainx-brain's chrome__ still matches). Null = none.</summary>
    public static string? BlindHint(IEnumerable<string>? offeredToolNames)
    {
        if (offeredToolNames == null) return null;
        var names = offeredToolNames.ToList();
        static string Leaf(string n) { int i = n.LastIndexOf("__", StringComparison.Ordinal); return i >= 0 ? n[(i + 2)..] : n; }
        var builtIn = new[] { "browser_read", "browser_console", "browser_eval" }.Where(n => names.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (builtIn.Count > 0) return "use " + string.Join(" / ", builtIn);
        var textLeaves = new[] { "get_page_text", "read_page", "read_console_messages", "browser_snapshot", "browser_console_messages" };
        var mcp = names.Where(n => n.StartsWith("mcp__", StringComparison.Ordinal)
                                   && textLeaves.Contains(Leaf(n), StringComparer.OrdinalIgnoreCase)).Take(3).ToList();
        return mcp.Count > 0 ? "use " + string.Join(" / ", mcp) : null;
    }

    // Every form AttachedMarker writes: plain, downscaled, "; not saved: …", ", saved at <path>". A marker ends its
    // line, so the path runs to the last ']' on the line (a folder name may itself contain ']').
    private static readonly System.Text.RegularExpressions.Regex AttachedMarkerRx = new(
        @"\[image (\d+): (\d+x\d+) attached(?: — downscaled from \d+x\d+[^;,\]\r\n]*)?(?:;[^\r\n]*?|(, saved at [^\r\n]+?))?\](?=[ \t\r]*$)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// Rewrite "[image N: WxH attached, saved at …]" in text from an EARLIER turn (chat history) to say the image
    /// is not attached any more — only the current turn's tool results carry pixels, so a later turn (or another
    /// provider after a switch) must not believe it still has them. The file path stays; the downscale note and a
    /// "not saved" reason go (they were about pixels this request doesn't have).
    /// </summary>
    public static string MarkEarlierTurn(string? text) =>
        string.IsNullOrEmpty(text) || !text.Contains("attached", StringComparison.Ordinal)
            ? text ?? ""
            : AttachedMarkerRx.Replace(text, "[image $1: $2 from an earlier turn — not attached any more$3]");

    // The lines PrepareAsync / read_file put into Output about images: attached, could not be used, skipped.
    private static readonly System.Text.RegularExpressions.Regex ImageLineRx = new(
        @"^[ \t]*\[(?:image \d+: \d+x\d+ attached|image \d+ could not be used: |\d+ more image\(s\) skipped)[^\r\n]*\][ \t\r]*$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>The image lines of a tool's Output, in order and exactly as written (their numbers are the item
    /// numbers the tool reported — "[image 2: …]" after a broken item 1).</summary>
    public static List<string> ImageLines(string? output) =>
        string.IsNullOrEmpty(output) ? new List<string>()
            : ImageLineRx.Matches(output).Select(m => m.Value.Trim()).ToList();

    /// <summary>
    /// After a text budget cut <paramref name="cut"/> out of <paramref name="original"/>: add back every image line
    /// of the original the cut removed, verbatim and in order (they sit at the END of Output, exactly what a cut
    /// takes first), so the model still learns which image is which and where each was saved.
    /// </summary>
    public static string RestoreImageLines(string cut, string? original)
    {
        var missing = ImageLines(original).Where(l => !cut.Contains(l, StringComparison.Ordinal)).ToList();
        return missing.Count == 0 ? cut : cut + "\n" + string.Join("\n", missing);
    }

    /// <summary>Content plus a note on its own line.</summary>
    public static string AppendNote(string? content, string note) =>
        string.IsNullOrEmpty(content) ? note : content + "\n" + note;

    /// <summary>Does this block carry images?</summary>
    public static bool HasImages(ContentBlock b) => b.Images is { Count: > 0 };

    public static int CountImages(IEnumerable<NativeMessage> messages) =>
        messages.Sum(m => m.Content.Sum(b => b.Images?.Count ?? 0));

    /// <summary>
    /// Bound the images a native conversation resends on every request. Mutates the blocks in place (the loop's own
    /// conversation) and returns how many images were removed:
    /// <list type="bullet">
    /// <item>keep ≥ 1: the NEWEST message that carries images (the step the model has not answered yet) keeps all of
    ///   them — the model must see what it just asked for, however many came at once — up to
    ///   <see cref="MaxImagesPerStep"/> (the rest: <see cref="NotShown"/>). Older images are kept, newest first, while
    ///   fewer than <paramref name="keep"/> are kept in total; the others become <see cref="OmittedOld"/> (the model
    ///   saw them on an earlier step).</item>
    /// <item>keep ≤ 0 (the context-overflow path): every image goes — the newest step's as "not shown — dropped to fit
    ///   the context window" (never seen), older ones as <see cref="OmittedOld"/>.</item>
    /// </list>
    /// </summary>
    public static int KeepNewestImages(List<NativeMessage> messages, int keep)
    {
        int newest = -1;
        for (int mi = messages.Count - 1; mi >= 0 && newest < 0; mi--)
            if (messages[mi].Content.Any(b => b.Images is { Count: > 0 })) newest = mi;
        if (newest < 0) return 0;

        int seen = 0, dropped = 0;
        for (int mi = newest; mi >= 0; mi--)
        {
            bool isNewest = mi == newest;
            // The newest step's images are counted in the order the tools returned them (first ones first);
            // older steps from the newest image backwards.
            var blocks = messages[mi].Content.Where(b => b.Images is { Count: > 0 }).ToList();
            if (!isNewest) blocks.Reverse();
            int stepKept = 0;
            foreach (var b in blocks)
            {
                var imgs = b.Images!;
                var keepFlags = new bool[imgs.Count];
                var notes = new string?[imgs.Count];
                var order = isNewest ? Enumerable.Range(0, imgs.Count) : Enumerable.Range(0, imgs.Count).Reverse();
                foreach (int ii in order)
                {
                    if (keep <= 0)
                        notes[ii] = isNewest ? NotShown(imgs[ii], "dropped to fit the context window") : OmittedOld(imgs[ii]);
                    else if (isNewest && stepKept >= MaxImagesPerStep)
                        notes[ii] = NotShown(imgs[ii], $"more than {MaxImagesPerStep} images in one step");
                    else if (isNewest || seen < keep)
                    {
                        keepFlags[ii] = true;
                        seen++;
                        if (isNewest) stepKept++;
                    }
                    else notes[ii] = OmittedOld(imgs[ii]);
                }
                var droppedHere = notes.Where(n => n != null).Cast<string>().ToList();
                if (droppedHere.Count == 0) continue;
                dropped += droppedHere.Count;
                var kept = imgs.Where((_, i) => keepFlags[i]).ToList();
                b.Images = kept.Count > 0 ? kept : null;
                string note = string.Join("\n", droppedHere);
                if (b.Type == "tool_result") b.Content = AppendNote(b.Content, note);
                else b.Text = AppendNote(b.Text, note);
            }
        }
        return dropped;
    }

    /// <summary>
    /// For a provider that cannot see images: strip every image from <paramref name="messages"/> (in place) and add
    /// <see cref="OmittedForBlindModel"/> to each block that had some. Returns how many were removed.
    /// </summary>
    public static int DropAllForBlindModel(List<NativeMessage> messages, string? hint)
        => DropAll(messages, imgs => OmittedForBlindModel(imgs, hint));

    /// <summary>
    /// After the API refused the images of a request (and the provider retried it as text): strip every image
    /// from the loop's own conversation (in place) with <see cref="RefusedByApi"/> — the same note the retried
    /// request carried — so later steps do not send the refused image again and hit the same 400.
    /// </summary>
    public static int DropRefused(List<NativeMessage> messages, string? why)
        => DropAll(messages, imgs => RefusedByApi(imgs, why));

    private static int DropAll(List<NativeMessage> messages, Func<List<ToolImage>, string> note)
    {
        int dropped = 0;
        foreach (var m in messages)
            foreach (var b in m.Content)
            {
                if (b.Images is not { Count: > 0 } imgs) continue;
                dropped += imgs.Count;
                string n = note(imgs);
                b.Images = null;
                if (b.Type == "tool_result") b.Content = AppendNote(b.Content, n);
                else b.Text = AppendNote(b.Text, n);
            }
        return dropped;
    }

    /// <summary>
    /// Delete every screenshot saved for one chat (when the chat itself is deleted). Goes through the store's own
    /// prune, so only files the store named are touched, a planted link is never followed, and the folder is
    /// removed only when nothing else is in it. Returns how many files were deleted. Never throws.
    /// </summary>
    public static int DeleteSessionImages(ToolImageStore store, string? sessionKey)
    {
        if (string.IsNullOrWhiteSpace(sessionKey)) return 0;
        // A "now" far in the future: every image is older than the store's age limit.
        try { return store.Prune(sessionKey, DateTime.MaxValue); }
        catch { return 0; }
    }

    // ── One store per data folder, shared by everything in this process that saves tool images ──
    private static readonly ConcurrentDictionary<string, ToolImageStore> Stores = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <see cref="ToolImageStore"/> for <paramref name="settings"/>' data folder (DataRoot\screens),
    /// one instance per folder so its lock covers every caller here.</summary>
    public static ToolImageStore StoreFor(SettingsService settings)
    {
        string root = Path.Combine(settings.DataRoot, "screens");
        return Stores.GetOrAdd(Path.GetFullPath(root), r => new ToolImageStore(r));
    }

    /// <summary>
    /// Decode, downscale and save each image, then return the images for <see cref="ToolResult.Images"/> plus the
    /// marker lines for <see cref="ToolResult.Output"/> (one per input, in order: attached / could not be used /
    /// skipped). Never throws; a failed save keeps the image (FilePath null) and says it was not saved.
    /// </summary>
    public static async Task<(List<ToolImage> Images, List<string> Markers)> PrepareAsync(
        IReadOnlyList<(string? Base64, string? MimeType)> items, ToolImageStore? store, string? sessionKey, int maxEdge,
        CancellationToken ct = default)
    {
        var images = new List<ToolImage>();
        var markers = new List<string>();
        for (int i = 0; i < items.Count; i++)
        {
            int n = i + 1;
            // Bounded work and bounded text: a server that returns hundreds of frames (or of broken ones) gets one
            // summary line, not hundreds of decodes and markers.
            if (images.Count >= MaxImagesPerResult || i >= MaxImagesPerResult * 2)
            {
                markers.Add($"[{items.Count - i} more image(s) skipped — at most {MaxImagesPerResult} images per tool result]");
                break;
            }
            ct.ThrowIfCancellationRequested();
            var decoded = await ImageCodec.FromBase64Async(items[i].Base64, items[i].MimeType, maxEdge).ConfigureAwait(false);
            if (decoded.Image is not { } img)
            {
                markers.Add(FailedMarker(n, decoded.Error ?? "unknown error"));
                continue;
            }
            string? path = null, saveError = null;
            if (store != null && decoded.JpegForStore is { } jpeg)
            {
                try { path = store.Save(jpeg, sessionKey); }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                { saveError = "not saved: the screenshot folder refused the file"; }
            }
            img = img with { FilePath = path };
            images.Add(img);
            // A shrunk image says from what size and by which factor — the tool's own text still names the original
            // size, and a coordinate tool clicks in the original's pixels.
            markers.Add(AttachedMarker(n, img, decoded.SourceWidth, decoded.SourceHeight, saveError));
        }
        return (images, markers);
    }
}
