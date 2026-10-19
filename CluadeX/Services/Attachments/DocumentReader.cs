using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace CluadeX.Services.Attachments;

/// <summary>What <see cref="DocumentReader.Read"/> got out of one file.</summary>
public sealed class DocumentText
{
    public bool Success { get; init; }
    /// <summary>"pdf", "docx", "xlsx", "pptx", "text", "archive", "image", "binary".</summary>
    public string Kind { get; init; } = "";
    /// <summary>The slice asked for (from offset, at most maxChars).</summary>
    public string Text { get; init; } = "";
    /// <summary>Characters of text in the whole document (as far as it was read).</summary>
    public int TotalChars { get; init; }
    /// <summary>The slice ends before the text does: read on from <see cref="NextOffset"/>.</summary>
    public bool Truncated { get; init; }
    public int NextOffset { get; init; }
    /// <summary>PDF pages / slides / sheets.</summary>
    public int? Parts { get; init; }
    /// <summary>A PDF whose pages are pictures (a scan): little or no text, images on the pages.</summary>
    public bool LooksScanned { get; init; }
    /// <summary>Why it failed, or a remark about what was read — English, for the model.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// The text inside an attached file — PDF (PdfPig), Word / Excel / PowerPoint (Office Open XML, read straight from the
/// zip), and any text or source file — so a model gets the content rather than a file name. Read-only, bounded (it
/// never builds more text than it needs for the slice), and it never throws: a failure comes back as a Note.
/// </summary>
public static class DocumentReader
{
    /// <summary>PDF pages read at most — a 2,000-page manual is still answered from its start.</summary>
    public const int MaxPdfPages = 400;
    private const long MaxTextFileBytes = 64L * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".log", ".env", ".html", ".htm", ".css", ".scss", ".less", ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".vue", ".svelte",
        ".cs", ".xaml", ".csproj", ".sln", ".props", ".targets", ".vb", ".fs", ".java", ".kt", ".kts", ".gradle", ".swift", ".m",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".rs", ".go", ".py", ".rb", ".php", ".blade.php", ".pl", ".lua", ".dart", ".sql",
        ".sh", ".bash", ".ps1", ".psm1", ".bat", ".cmd", ".r", ".tex", ".rtf", ".srt", ".vtt", ".svg", ".gitignore", ".dockerfile",
        ".arb", ".properties", ".lock",
    };

    public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public static DocumentText Read(string path, int offset = 0, int maxChars = 20_000)
    {
        offset = Math.Max(0, offset);
        maxChars = Math.Clamp(maxChars, 200, 2_000_000);
        try
        {
            if (!File.Exists(path)) return new() { Kind = "", Note = $"No file at {path}" };
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Path.GetFileName(path).EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || ArchiveExtractor.IsSupported(path))
                return Archive(path, offset, maxChars);
            switch (ext)
            {
                case ".pdf": return Pdf(path, offset, maxChars);
                case ".docx": case ".docm": case ".dotx": return Office(path, "docx", offset, maxChars, Docx);
                case ".xlsx": case ".xlsm": return Office(path, "xlsx", offset, maxChars, Xlsx);
                case ".pptx": case ".pptm": return Office(path, "pptx", offset, maxChars, Pptx);
                case ".doc": case ".xls": case ".ppt":
                    return new() { Kind = "binary", Note = $"{Path.GetFileName(path)} is an old binary Office file (97-2003); only .docx/.xlsx/.pptx can be read. Ask the user to save it in the new format, or convert it with LibreOffice (soffice --headless --convert-to docx)." };
                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".webp": case ".bmp": case ".tif": case ".tiff": case ".ico":
                    return new() { Kind = "image", Note = $"{Path.GetFileName(path)} is an image, not a document." };
            }
            if (TextExtensions.Contains(ext) || LooksLikeText(path)) return TextFile(path, offset, maxChars);
            if (ArchiveExtractor.IsOtherArchive(path))
                return new() { Kind = "archive", Note = $"{Path.GetFileName(path)} is a {ext.TrimStart('.')} archive; only zip/tar/gz can be opened here — use 7-Zip through the shell if it is installed." };
            return new() { Kind = "binary", Note = $"{Path.GetFileName(path)} is a binary file ({ext}); there is no text to read. Use hex_info / hex_read to look inside." };
        }
        catch (Exception ex)
        {
            return new() { Kind = "", Note = $"Could not read {Path.GetFileName(path)}: {ex.Message}" };
        }
    }

    // ─── Slicing ───

    /// <summary>Builds text a part at a time and stops as soon as the slice [offset, offset+max) is complete.</summary>
    private sealed class Collector(int offset, int max)
    {
        private readonly StringBuilder _all = new();
        public int Parts;
        public bool Full => _all.Length > offset + max;
        public void Append(string s) { if (!Full) _all.Append(s); }
        public DocumentText Result(string kind, string? note = null, bool scanned = false, bool complete = true)
        {
            int total = _all.Length;
            string slice = offset >= total ? "" : _all.ToString(offset, Math.Min(max, total - offset));
            // More text exists past the slice: either already collected, or never read because the slice was full.
            bool truncated = offset + slice.Length < total || !complete;
            return new()
            {
                Success = true, Kind = kind, Text = slice, TotalChars = total, Truncated = truncated,
                NextOffset = offset + slice.Length, Parts = Parts > 0 ? Parts : null, LooksScanned = scanned, Note = note,
            };
        }
    }

    // ─── Text files ───

    private static bool LooksLikeText(string path)
    {
        var buf = new byte[8192];
        int n;
        using (var fs = File.OpenRead(path)) n = fs.Read(buf, 0, buf.Length);
        if (n == 0) return true;
        // UTF-16 with a BOM has NULs but is text.
        if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return true;
        int control = 0;
        for (int i = 0; i < n; i++)
        {
            byte b = buf[i];
            if (b == 0) return false;
            if (b < 8 || (b > 13 && b < 32 && b != 27)) control++;
        }
        return control < n / 50 + 1;
    }

    private static DocumentText TextFile(string path, int offset, int maxChars)
    {
        long size = new FileInfo(path).Length;
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var c = new Collector(offset, maxChars);
        var buf = new char[16384];
        long read = 0;
        int n;
        while (!c.Full && (n = reader.Read(buf, 0, buf.Length)) > 0)
        {
            c.Append(new string(buf, 0, n));
            read += n;
            if (read > MaxTextFileBytes) break;
        }
        bool complete = reader.EndOfStream;
        var r = c.Result("text", complete: complete);
        return complete ? r : new DocumentText
        {
            Success = true, Kind = "text", Text = r.Text, TotalChars = r.TotalChars, Truncated = true, NextOffset = r.NextOffset,
            Note = $"The file is {size / 1024:N0} KB; read on with offset={r.NextOffset}.",
        };
    }

    // ─── PDF ───

    private static DocumentText Pdf(string path, int offset, int maxChars)
    {
        PdfDocument doc;
        try { doc = PdfDocument.Open(path); }
        catch (Exception ex) when (ex.GetType().Name.Contains("Encrypt", StringComparison.Ordinal))
        {
            return new() { Kind = "pdf", Note = $"{Path.GetFileName(path)} is password-protected; ask the user for an unlocked copy." };
        }
        catch (Exception ex)
        {
            return new() { Kind = "pdf", Note = $"{Path.GetFileName(path)} could not be opened as a PDF ({ex.GetType().Name}: {ex.Message})." };
        }
        using (doc)
        {
            int pages = doc.NumberOfPages;
            var c = new Collector(offset, maxChars);
            int examined = 0, textChars = 0, pagesWithImages = 0;
            bool complete = true;
            for (int i = 1; i <= pages; i++)
            {
                if (c.Full || i > MaxPdfPages) { complete = false; break; }
                string text;
                bool hasImages;
                try
                {
                    var page = doc.GetPage(i);
                    text = ContentOrderTextExtractor.GetText(page) ?? "";
                    hasImages = page.GetImages().Any();
                }
                catch (Exception ex) { text = $"[page {i} could not be read: {ex.Message}]"; hasImages = false; }
                examined++;
                textChars += text.Count(ch => !char.IsWhiteSpace(ch));
                if (hasImages) pagesWithImages++;
                c.Append($"--- page {i} ---\n{text.Trim()}\n\n");
            }
            c.Parts = pages;
            // A scan: almost no characters per page, and pictures on the pages.
            bool scanned = examined > 0 && textChars < 25 * examined && pagesWithImages > 0;
            string? note = scanned
                ? $"This PDF looks scanned: {pages} page(s) of pictures with little or no text layer."
                : !complete && pages > MaxPdfPages ? $"Only the first {MaxPdfPages} of {pages} pages are read." : null;
            return c.Result("pdf", note, scanned, complete);
        }
    }

    /// <summary>
    /// The pictures of a scanned PDF's first pages, saved as files a vision model can be sent: for each page the largest
    /// embedded image (a scanner puts one per page), as PNG when PdfPig can decode it or as the JPEG it already is.
    /// Pages whose image is in another format (JPEG 2000, CCITT) are left out.
    /// </summary>
    public static List<string> SavePdfPageImages(string pdfPath, string outDir, int maxPages = 4)
    {
        var saved = new List<string>();
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            Directory.CreateDirectory(outDir);
            for (int i = 1; i <= doc.NumberOfPages && saved.Count < maxPages; i++)
            {
                try
                {
                    var page = doc.GetPage(i);
                    var img = page.GetImages().OrderByDescending(m => m.Bounds.Width * m.Bounds.Height).FirstOrDefault();
                    if (img == null) continue;
                    string file;
                    if (img.TryGetPng(out var png) && png is { Length: > 0 })
                        File.WriteAllBytes(file = Path.Combine(outDir, $"page-{i}.png"), png);
                    else if (img.RawMemory.Length > 3 && img.RawMemory.Span[0] == 0xFF && img.RawMemory.Span[1] == 0xD8)
                        File.WriteAllBytes(file = Path.Combine(outDir, $"page-{i}.jpg"), img.RawMemory.ToArray());
                    else continue;
                    saved.Add(file);
                }
                catch { /* one bad page: the others still go */ }
            }
        }
        catch { }
        return saved;
    }

    // ─── Office Open XML ───

    private static DocumentText Office(string path, string kind, int offset, int maxChars, Action<ZipArchive, Collector> read)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException)
        {
            return new() { Kind = kind, Note = $"{Path.GetFileName(path)} is not a valid {kind} file (it may be password-protected or damaged)." };
        }
        using (zip)
        {
            var c = new Collector(offset, maxChars);
            read(zip, c);
            return c.Result(kind, complete: !c.Full);
        }
    }

    private static XmlReader Xml(ZipArchiveEntry e) =>
        XmlReader.Create(e.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });

    /// <summary>Visits every node once. <see cref="XmlReader.ReadElementContentAsString()"/> and Skip already move to
    /// the next node, so <paramref name="onNode"/> returns true when it moved the reader itself and false to have the
    /// walker step on (calling Read after them would silently skip the node that follows).</summary>
    private static void Walk(XmlReader x, Func<XmlReader, bool> onNode, Func<bool>? stop = null)
    {
        x.Read();
        while (!x.EOF && (stop == null || !stop()))
            if (!onNode(x)) x.Read();
    }

    private static void Docx(ZipArchive zip, Collector c)
    {
        var body = zip.GetEntry("word/document.xml");
        if (body == null) { c.Append("[no word/document.xml in this file]"); return; }
        using var x = Xml(body);
        var line = new StringBuilder();
        void EndParagraph() { c.Append(line.Append('\n').ToString()); line.Clear(); }
        Walk(x, r =>
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                switch (r.LocalName)
                {
                    case "t": line.Append(r.ReadElementContentAsString()); return true;
                    case "tab": line.Append('\t'); break;
                    case "br": case "cr": line.Append('\n'); break;
                    case "p" when r.IsEmptyElement: EndParagraph(); break;
                }
            }
            else if (r.NodeType == XmlNodeType.EndElement)
            {
                if (r.LocalName == "p") EndParagraph();
                else if (r.LocalName == "tc") line.Append(" | ");
            }
            return false;
        }, () => c.Full);
        if (line.Length > 0) c.Append(line.ToString());
    }

    private static void Pptx(ZipArchive zip, Collector c)
    {
        var slides = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => NumberIn(e.FullName)).ToList();
        foreach (var s in slides)
        {
            if (c.Full) break;
            c.Parts++;
            c.Append($"--- slide {NumberIn(s.FullName)} ---\n");
            using var x = Xml(s);
            var line = new StringBuilder();
            Walk(x, r =>
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "t") { line.Append(r.ReadElementContentAsString()); return true; }
                if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "p" && line.Length > 0) { c.Append(line.Append('\n').ToString()); line.Clear(); }
                return false;
            });
            if (line.Length > 0) c.Append(line.Append('\n').ToString());
            c.Append("\n");
        }
    }

    private static void Xlsx(ZipArchive zip, Collector c)
    {
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } ss)
        {
            using var x = Xml(ss);
            StringBuilder? cur = null;
            Walk(x, r =>
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    switch (r.LocalName)
                    {
                        case "si" when r.IsEmptyElement: shared.Add(""); break;
                        case "si": cur = new StringBuilder(); break;
                        case "rPh": r.Skip(); return true;                 // phonetic hints, not cell text
                        case "t" when cur != null: cur.Append(r.ReadElementContentAsString()); return true;
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "si" && cur != null) { shared.Add(cur.ToString()); cur = null; }
                return false;
            });
        }
        foreach (var (name, entry) in Sheets(zip))
        {
            if (c.Full) break;
            c.Parts++;
            c.Append($"--- sheet: {name} ---\n");
            using var x = Xml(entry);
            var row = new List<string>();
            string? type = null; int col = -1; string? value = null;
            void EndCell()
            {
                string text = type switch
                {
                    "s" when int.TryParse(value, out var si) && si >= 0 && si < shared.Count => shared[si],
                    "b" => value == "1" ? "TRUE" : "FALSE",
                    _ => value ?? "",
                };
                Put(row, col, text);
            }
            void EndRow()
            {
                if (row.Any(v => v.Length > 0))
                    c.Append(string.Join('\t', row.Select(v => v.Replace('\t', ' ').Replace('\n', ' '))) + "\n");
                row.Clear();
            }
            Walk(x, r =>
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    switch (r.LocalName)
                    {
                        case "row": row.Clear(); if (r.IsEmptyElement) EndRow(); break;
                        case "c":
                            type = r.GetAttribute("t");
                            col = ColumnIndex(r.GetAttribute("r"), row.Count);
                            value = null;
                            if (r.IsEmptyElement) Put(row, col, "");
                            break;
                        case "v": value = r.ReadElementContentAsString(); return true;
                        case "t": value = (value ?? "") + r.ReadElementContentAsString(); return true;   // inline string
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (r.LocalName == "c") EndCell();
                    else if (r.LocalName == "row") EndRow();
                }
                return false;
            }, () => c.Full);
            c.Append("\n");
        }
    }

    private static void Put(List<string> row, int col, string text)
    {
        if (col < 0 || col > 16384) col = row.Count;
        while (row.Count < col) row.Add("");
        if (col < row.Count) row[col] = text; else row.Add(text);
    }

    /// <summary>"C7" → 2 (zero-based). No reference = the next column.</summary>
    private static int ColumnIndex(string? cellRef, int next)
    {
        if (string.IsNullOrEmpty(cellRef)) return next;
        int col = 0, i = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i])) { col = col * 26 + (char.ToUpperInvariant(cellRef[i]) - 'A' + 1); i++; }
        return i == 0 ? next : col - 1;
    }

    /// <summary>The workbook's sheets in tab order, with their names (workbook.xml + its rels); falls back to the
    /// sheet parts in number order.</summary>
    private static List<(string Name, ZipArchiveEntry Entry)> Sheets(ZipArchive zip)
    {
        var list = new List<(string, ZipArchiveEntry)>();
        try
        {
            var rels = new Dictionary<string, string>();
            if (zip.GetEntry("xl/_rels/workbook.xml.rels") is { } re)
            {
                using var x = Xml(re);
                while (x.Read())
                    if (x.NodeType == XmlNodeType.Element && x.LocalName == "Relationship" && x.GetAttribute("Id") is { } id && x.GetAttribute("Target") is { } t)
                        rels[id] = t.StartsWith('/') ? t.TrimStart('/') : "xl/" + t;
            }
            if (zip.GetEntry("xl/workbook.xml") is { } wb)
            {
                using var x = Xml(wb);
                while (x.Read())
                {
                    if (x.NodeType != XmlNodeType.Element || x.LocalName != "sheet") continue;
                    string name = x.GetAttribute("name") ?? "Sheet";
                    string? rid = x.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    if (rid != null && rels.TryGetValue(rid, out var target) && zip.GetEntry(target) is { } se) list.Add((name, se));
                }
            }
        }
        catch { list.Clear(); }
        if (list.Count == 0)
            list = zip.Entries
                .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => NumberIn(e.FullName)).Select(e => (Path.GetFileNameWithoutExtension(e.Name), e)).ToList();
        return list;
    }

    private static int NumberIn(string s)
    {
        var digits = new string(Path.GetFileNameWithoutExtension(s).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    // ─── Archives (listing only — unpacking is ArchiveExtractor.Extract) ───

    private static DocumentText Archive(string path, int offset, int maxChars)
    {
        var list = ArchiveExtractor.List(path, maxListed: 2000);
        if (!list.Success) return new() { Kind = "archive", Note = list.Error };
        var c = new Collector(offset, maxChars);
        c.Append($"{list.TotalEntries} entries, {list.TotalBytes / 1024:N0} KB unpacked:\n");
        foreach (var e in list.Entries)
            c.Append(e.IsDirectory ? $"  {e.Name}\n" : $"  {e.Name}  ({(e.Size < 0 ? "?" : (e.Size / 1024.0).ToString("0.#") + " KB")})\n");
        if (list.TotalEntries > list.Entries.Count) c.Append($"  … and {list.TotalEntries - list.Entries.Count} more\n");
        return c.Result("archive", "This is the archive's listing; extract_archive unpacks it.", complete: !c.Full);
    }
}
