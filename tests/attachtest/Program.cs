using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Models;
using CluadeX.Services.Attachments;
using CluadeX.Services.Tools;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace AttachTest;

/// <summary>
/// Files a user attaches to a chat, made usable at once (Services/Attachments + AttachmentToolModule), checked on the
/// shipping CluadeX.dll with real files written to a temp folder:
///   A  archives — zip / tar.gz / gz unpack; zip-slip ("../", absolute, drive, device names, ADS, links) never lands
///      outside; zip bombs, too many entries and too many bytes stop at the limit and leave nothing behind;
///   P  PDF — text per page, slicing with offset, a scanned PDF detected and its page pictures saved;
///   O  Office / text — docx, xlsx, pptx, Thai UTF-8 text, binary refused with a reason;
///   D  the digest a model gets — paths, document text, the zip unpacked next to it, folders for a CLI agent;
///   T  the agent tools — read_document, extract_archive (into the project), copy_file, and what they ask permission for.
///
///   dotnet build CluadeX/CluadeX.csproj   then   cd tests/attachtest && dotnet run
/// </summary>
internal static class Program
{
    static int Pass, Fail;
    static readonly List<string> Failures = new();
    static string Root = "";

    static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS  {name}"); }
        else
        {
            Fail++;
            Failures.Add(name);
            Console.WriteLine($"  FAIL  {name}" + (detail != null ? $"\n        {detail.Replace("\n", "\n        ")}" : ""));
        }
    }

    [STAThread]
    static int Main()
    {
        Root = Path.Combine(Path.GetTempPath(), "cx-attachtest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        try
        {
            Archives();
            Pdfs();
            Office();
            Digest();
            Tools().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Fail++;
            Failures.Add("crashed: " + ex.Message);
            Console.WriteLine(ex);
        }
        finally
        {
            try { Directory.Delete(Root, true); } catch { }
        }
        Console.WriteLine($"\nattachtest: {Pass} passed, {Fail} failed");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return Fail == 0 ? 0 : 1;
    }

    static string Dir(string name)
    {
        string d = Path.Combine(Root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    static void AddEntry(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    // ─── A: archives ───

    static void Archives()
    {
        Console.WriteLine("\n## A — archives: unpack, zip-slip, bombs");
        string work = Dir("a");

        // A normal site zip.
        string site = Path.Combine(work, "site.zip");
        using (var zip = ZipFile.Open(site, ZipArchiveMode.Create))
        {
            AddEntry(zip, "index.html", "<h1>สวัสดี</h1>");
            AddEntry(zip, "css/style.css", "body{color:red}");
            AddEntry(zip, "img/", "");
            AddEntry(zip, "js/app/main.js", "console.log(1)");
        }
        string dest = Path.Combine(work, "site");
        var r = ArchiveExtractor.Extract(site, dest);
        Check("A1 a zip unpacks every file with its folders", r.Success && r.Files.Count == 3
            && File.ReadAllText(Path.Combine(dest, "index.html")) == "<h1>สวัสดี</h1>"
            && File.Exists(Path.Combine(dest, "js", "app", "main.js")) && Directory.Exists(Path.Combine(dest, "img")),
            r.Error + " files=" + string.Join(",", r.Files));

        // Zip-slip: every escaping name is refused; the good one still lands.
        string evil = Path.Combine(work, "evil.zip");
        string outside = Path.Combine(work, "outside");
        Directory.CreateDirectory(outside);
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            AddEntry(zip, "../outside/evil1.txt", "x");
            AddEntry(zip, "..\\outside\\evil2.txt", "x");
            AddEntry(zip, "ok/../../outside/evil3.txt", "x");
            AddEntry(zip, "/evil4.txt", "x");
            AddEntry(zip, "C:/Windows/Temp/evil5.txt", "x");
            AddEntry(zip, "CON.txt", "x");
            AddEntry(zip, "file.txt:hidden", "x");
            AddEntry(zip, "trailingdot.", "x");
            AddEntry(zip, "good/readme.txt", "fine");
            var link = zip.CreateEntry("link-to-windows");
            link.ExternalAttributes = unchecked((int)(0xA1FFu << 16));   // a unix symlink
            using (var w = new StreamWriter(link.Open())) w.Write("C:/Windows");
        }
        string evilDest = Path.Combine(work, "evil");
        var er = ArchiveExtractor.Extract(evil, evilDest);
        var outsideFiles = Directory.GetFiles(outside, "*", SearchOption.AllDirectories);
        Check("A2 zip-slip: nothing is written outside the destination", er.Success && outsideFiles.Length == 0
            && !File.Exists(Path.Combine(work, "evil4.txt")) && !File.Exists(@"C:\Windows\Temp\evil5.txt"),
            string.Join(", ", outsideFiles));
        Check("A2 zip-slip: each bad entry is listed as skipped with a reason, the good one is unpacked",
            er.Files.SequenceEqual(new[] { "good/readme.txt" }) && er.Skipped.Count == 9
            && er.Skipped.Any(s => s.Contains("leaves the folder")) && er.Skipped.Any(s => s.Contains("absolute"))
            && er.Skipped.Any(s => s.Contains("device name")) && er.Skipped.Any(s => s.Contains("link")),
            "files=" + string.Join(",", er.Files) + "\nskipped=" + string.Join("\n", er.Skipped));
        Check("A2 SafeTarget refuses a sibling folder sharing the destination's prefix",
            ArchiveExtractor.SafeTarget(@"C:\x\dest", "..\\dest-evil\\a.txt", out _) == null
            && ArchiveExtractor.SafeTarget(@"C:\x\dest", "a/b.txt", out _) == @"C:\x\dest\a\b.txt");

        // Zip bomb: 24 MB of zeros packs to a few KB — the ratio stops it and nothing stays behind.
        string bomb = Path.Combine(work, "bomb.zip");
        using (var zip = ZipFile.Open(bomb, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("zeros.bin", CompressionLevel.SmallestSize);
            using var s = e.Open();
            var zeros = new byte[1 << 20];
            for (int i = 0; i < 24; i++) s.Write(zeros);
        }
        string bombDest = Path.Combine(work, "bomb");
        var limits = new ArchiveLimits { RatioCheckMinBytes = 1 << 20, MaxRatio = 100 };
        var br = ArchiveExtractor.Extract(bomb, bombDest, limits: limits);
        Check("A3 a zip bomb is refused as a bomb and its folder is removed", !br.Success && br.Failure == ArchiveFailure.Bomb && !Directory.Exists(bombDest),
            $"{br.Failure}: {br.Error}; exists={Directory.Exists(bombDest)}");

        // A bomb unpacked with overwrite into a folder that already holds that file: the user's file is not deleted.
        string keepDir = Path.Combine(work, "keep");
        Directory.CreateDirectory(keepDir);
        File.WriteAllText(Path.Combine(keepDir, "zeros.bin"), "user data");
        File.WriteAllText(Path.Combine(keepDir, "other.txt"), "untouched");
        var kb = ArchiveExtractor.Extract(bomb, keepDir, overwrite: true, limits: limits);
        Check("A3 an aborted unpack never deletes files that were already there, nor the folder",
            !kb.Success && File.Exists(Path.Combine(keepDir, "zeros.bin")) && File.ReadAllText(Path.Combine(keepDir, "other.txt")) == "untouched",
            kb.Error);

        // The byte cap counts what is really written, whatever the header says.
        string big = Path.Combine(work, "big.zip");
        var rnd = new Random(7);
        using (var zip = ZipFile.Open(big, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("noise.bin", CompressionLevel.NoCompression);
            using var s = e.Open();
            var buf = new byte[3 << 20];
            rnd.NextBytes(buf);
            s.Write(buf);
        }
        var tooBig = ArchiveExtractor.Extract(big, Path.Combine(work, "big"), limits: new ArchiveLimits { MaxTotalBytes = 1 << 20 });
        Check("A4 an archive over the byte limit is refused", !tooBig.Success && tooBig.Failure == ArchiveFailure.TooLarge, tooBig.Error);

        string many = Path.Combine(work, "many.zip");
        using (var zip = ZipFile.Open(many, ZipArchiveMode.Create))
            for (int i = 0; i < 12; i++) AddEntry(zip, $"f{i}.txt", "x");
        var tooMany = ArchiveExtractor.Extract(many, Path.Combine(work, "many"), limits: new ArchiveLimits { MaxEntries = 10 });
        Check("A5 an archive with too many entries is refused before writing anything",
            !tooMany.Success && tooMany.Failure == ArchiveFailure.TooManyEntries && !Directory.Exists(Path.Combine(work, "many")), tooMany.Error);

        // Existing files are kept unless overwrite.
        File.WriteAllText(Path.Combine(dest, "index.html"), "mine");
        var again = ArchiveExtractor.Extract(site, dest);
        Check("A6 an existing file is kept (and listed) without overwrite",
            again.Success && File.ReadAllText(Path.Combine(dest, "index.html")) == "mine" && again.Skipped.Any(s => s.StartsWith("index.html")));
        var over = ArchiveExtractor.Extract(site, dest, overwrite: true);
        Check("A6 overwrite=true replaces it", over.Success && File.ReadAllText(Path.Combine(dest, "index.html")) == "<h1>สวัสดี</h1>");

        // tar.gz, with an escaping entry.
        string tgz = Path.Combine(work, "pack.tar.gz");
        using (var fs = File.Create(tgz))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        using (var tw = new TarWriter(gz, TarEntryFormat.Pax))
        {
            foreach (var (n, t) in new[] { ("src/a.txt", "AAA"), ("../escape.txt", "BAD") })
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, n) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(t)) };
                tw.WriteEntry(entry);
            }
            tw.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "evil-link") { LinkName = "C:/Windows" });
        }
        var tr = ArchiveExtractor.Extract(tgz, Path.Combine(work, "pack"));
        Check("A7 a .tar.gz unpacks; its ../ entry and its link are skipped",
            tr.Success && tr.Files.SequenceEqual(new[] { "src/a.txt" }) && !File.Exists(Path.Combine(work, "escape.txt")) && tr.Skipped.Count == 2,
            tr.Error + " files=" + string.Join(",", tr.Files) + " skipped=" + string.Join(" | ", tr.Skipped));

        string gzOnly = Path.Combine(work, "notes.txt.gz");
        using (var fs = File.Create(gzOnly))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            gz.Write(Encoding.UTF8.GetBytes("hello gz"));
        var gr = ArchiveExtractor.Extract(gzOnly, Path.Combine(work, "gz"));
        Check("A8 a single .gz file unpacks to its name", gr.Success && File.ReadAllText(Path.Combine(work, "gz", "notes.txt")) == "hello gz", gr.Error);

        File.WriteAllText(Path.Combine(work, "fake.zip"), "this is not a zip");
        var bad = ArchiveExtractor.Extract(Path.Combine(work, "fake.zip"), Path.Combine(work, "fake"));
        Check("A9 a damaged zip fails with a reason, not an exception", !bad.Success && bad.Failure == ArchiveFailure.Corrupt && bad.Error!.Contains("damaged"), bad.Error);
        var seven = ArchiveExtractor.Extract(Path.Combine(work, "x.7z"), Path.Combine(work, "x"));
        Check("A9 a missing / unsupported archive fails cleanly", !seven.Success);
    }

    // ─── P: PDF ───

    static byte[] TextPdf(params string[] pages)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = b.AddPage(595, 842);
            double y = 780;
            foreach (var line in text.Split('\n'))
            {
                page.AddText(line, 12, new PdfPoint(50, y), font);
                y -= 16;
            }
        }
        return b.Build();
    }

    static byte[] Jpeg(int w, int h)
    {
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
        var px = new int[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (i / w) % 20 < 10 ? 0x202020 : 0xF0F0F0;   // stripes: a "scanned" page
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), px, w * 4, 0);
        var enc = new JpegBitmapEncoder { QualityLevel = 80 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static void Pdfs()
    {
        Console.WriteLine("\n## P — PDF: text, slices, scans");
        string work = Dir("p");
        string pdf = Path.Combine(work, "spec.pdf");
        File.WriteAllBytes(pdf, TextPdf("Project Spec\nThe hero section uses a big photo.", "Page two talks about pricing.", "Third page: contact form."));
        var doc = DocumentReader.Read(pdf);
        Check("P1 a PDF's text is read page by page", doc.Success && doc.Kind == "pdf" && doc.Parts == 3
            && doc.Text.Contains("--- page 1 ---") && doc.Text.Contains("hero section") && doc.Text.Contains("pricing") && doc.Text.Contains("contact form")
            && !doc.LooksScanned && !doc.Truncated,
            $"{doc.Kind} parts={doc.Parts} note={doc.Note}\n{doc.Text}");

        string manual = Path.Combine(work, "manual.pdf");
        File.WriteAllBytes(manual, TextPdf(Enumerable.Range(1, 6).Select(p => string.Join("\n", Enumerable.Range(1, 20).Select(l => $"Page {p} line {l} of the manual."))).ToArray()));
        var whole = DocumentReader.Read(manual, 0, 1_000_000);
        var first = DocumentReader.Read(manual, 0, 700);
        var rest = DocumentReader.Read(manual, first.NextOffset, 1_000_000);
        Check("P2 a slice says it is cut and where to go on; the next slice continues exactly",
            whole.Text.Length > 2000 && first.Truncated && first.Text.Length == 700 && (first.Text + rest.Text) == whole.Text && !rest.Truncated,
            $"first={first.Text.Length} trunc={first.Truncated} next={first.NextOffset} rest={rest.Text.Length}");

        string scan = Path.Combine(work, "scan.pdf");
        var sb = new PdfDocumentBuilder();
        var jpg = Jpeg(300, 420);
        for (int i = 0; i < 2; i++) sb.AddPage(595, 842).AddJpeg(jpg, new PdfRectangle(0, 0, 595, 842));
        File.WriteAllBytes(scan, sb.Build());
        var sdoc = DocumentReader.Read(scan);
        Check("P3 a scanned PDF (pictures, no text) is reported as scanned", sdoc.Success && sdoc.LooksScanned && sdoc.Parts == 2 && sdoc.Note!.Contains("scanned"),
            $"scanned={sdoc.LooksScanned} note={sdoc.Note}");
        var pages = DocumentReader.SavePdfPageImages(scan, Path.Combine(work, "scan_pages"), 4);
        Check("P4 its page pictures are saved as image files a vision model can be sent",
            pages.Count == 2 && pages.All(File.Exists) && pages.All(p => new FileInfo(p).Length > 100)
            && CluadeX.Services.Vision.ImageCodec.SniffMediaType(File.ReadAllBytes(pages[0])) is "image/jpeg" or "image/png",
            string.Join(", ", pages));

        File.WriteAllText(Path.Combine(work, "broken.pdf"), "%PDF-1.4 garbage");
        var broken = DocumentReader.Read(Path.Combine(work, "broken.pdf"));
        Check("P5 a broken PDF gives a reason, never an exception", !broken.Success && broken.Note != null, broken.Note);
    }

    // ─── O: Office / text ───

    static string OoXml(string path, params (string Name, string Xml)[] parts)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (n, x) in parts) AddEntry(zip, n, x);
        return path;
    }

    static void Office()
    {
        Console.WriteLine("\n## O — Word, Excel, PowerPoint, text");
        string work = Dir("o");
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        string docx = OoXml(Path.Combine(work, "brief.docx"), ("word/document.xml",
            $"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>ชื่อร้าน: ขนมไทย</w:t></w:r></w:p><w:p><w:r><w:t>Hero:</w:t><w:tab/><w:t>ภาพขนม</w:t></w:r></w:p></w:body></w:document>"));
        var d = DocumentReader.Read(docx);
        Check("O1 a .docx gives its paragraphs (Thai kept)", d.Success && d.Kind == "docx" && d.Text.Contains("ชื่อร้าน: ขนมไทย\n") && d.Text.Contains("Hero:\tภาพขนม"), d.Note + "\n" + d.Text);

        const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        string xlsx = OoXml(Path.Combine(work, "prices.xlsx"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"ราคา\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
            ("xl/sharedStrings.xml", $"<sst xmlns=\"{S}\"><si><t>สินค้า</t></si><si><t>ราคา</t></si><si><t>ข้าวเหนียว</t></si></sst>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{S}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>"
                + "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"C2\"><v>45.5</v></c></row></sheetData></worksheet>"));
        var x = DocumentReader.Read(xlsx);
        Check("O2 a .xlsx gives each sheet by name as tab-separated rows, gaps kept",
            x.Success && x.Parts == 1 && x.Text.Contains("--- sheet: ราคา ---") && x.Text.Contains("สินค้า\tราคา\n") && x.Text.Contains("ข้าวเหนียว\t\t45.5"),
            x.Note + "\n" + x.Text);

        const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        string pptx = OoXml(Path.Combine(work, "deck.pptx"),
            ("ppt/slides/slide2.xml", $"<p:sld xmlns:p=\"p\" xmlns:a=\"{A}\"><a:p><a:r><a:t>Second</a:t></a:r></a:p></p:sld>"),
            ("ppt/slides/slide1.xml", $"<p:sld xmlns:p=\"p\" xmlns:a=\"{A}\"><a:p><a:r><a:t>First slide</a:t></a:r></a:p></p:sld>"),
            ("ppt/slides/slide10.xml", $"<p:sld xmlns:p=\"p\" xmlns:a=\"{A}\"><a:p><a:r><a:t>Tenth</a:t></a:r></a:p></p:sld>"));
        var pp = DocumentReader.Read(pptx);
        int i1 = pp.Text.IndexOf("First slide"), i2 = pp.Text.IndexOf("Second"), i10 = pp.Text.IndexOf("Tenth");
        Check("O3 a .pptx gives its slides in number order (2 before 10)", pp.Success && pp.Parts == 3 && i1 >= 0 && i1 < i2 && i2 < i10, pp.Text);

        string thai = Path.Combine(work, "notes.md");
        File.WriteAllText(thai, "# บันทึก\nใช้ฟอนต์ Prompt", new UTF8Encoding(false));
        var t = DocumentReader.Read(thai);
        Check("O4 a UTF-8 text file is read as is", t.Success && t.Kind == "text" && t.Text == "# บันทึก\nใช้ฟอนต์ Prompt", t.Text);

        string bin = Path.Combine(work, "tool.exe");
        File.WriteAllBytes(bin, new byte[] { 0x4D, 0x5A, 0, 0, 3, 0, 0, 0, 4, 0 });
        var b = DocumentReader.Read(bin);
        Check("O5 a binary file is refused with a reason", !b.Success && b.Kind == "binary" && b.Note!.Contains("binary"), b.Note);

        string old = Path.Combine(work, "old.doc");
        File.WriteAllBytes(old, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0, 0, 0, 0 });
        var o = DocumentReader.Read(old);
        Check("O6 an old .doc says how to get a readable copy", !o.Success && o.Note!.Contains(".docx"), o.Note);
    }

    // ─── D: digest ───

    static void Digest()
    {
        Console.WriteLine("\n## D — what the model gets with the attachments");
        string att = Dir(Path.Combine("data", "attachments", "2026-10", "x1"));
        string zipPath = Path.Combine(att, "site.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddEntry(zip, "index.html", "<p>hi</p>");
            AddEntry(zip, "../escape.txt", "bad");
        }
        string att2 = Dir(Path.Combine("data", "attachments", "2026-10", "x2"));
        string pdf = Path.Combine(att2, "spec.pdf");
        File.WriteAllBytes(pdf, TextPdf("Use the blue theme everywhere."));
        string att3 = Dir(Path.Combine("data", "attachments", "2026-10", "x3"));
        string img = Path.Combine(att3, "hero.jpg");
        File.WriteAllBytes(img, Jpeg(64, 48));

        var dg = AttachmentDigest.Build(new[] { img }, new[] { zipPath, pdf }, AttachmentAudience.LoopSeesImages, DigestBudget.Api);
        string unpacked = Path.Combine(att, "site");
        Check("D1 every attachment is named with its real path", dg.Text.Contains(img) && dg.Text.Contains(zipPath) && dg.Text.Contains(pdf), dg.Text);
        Check("D1 the picture is said to be attached, and copy_file is named for putting it in the project",
            dg.Text.Contains("hero.jpg — image") && dg.Text.Contains("you can see it") && dg.Text.Contains("copy_file"), dg.Text);
        Check("D2 the zip is unpacked next to it (zip-slip entry skipped) and its files listed",
            File.Exists(Path.Combine(unpacked, "index.html")) && !File.Exists(Path.Combine(att, "escape.txt"))
            && dg.Text.Contains("Unpacked to " + unpacked) && dg.Text.Contains("index.html") && dg.Text.Contains("Skipped 1"), dg.Text);
        Check("D3 the PDF's text is in the message", dg.Text.Contains("<<<file 3: spec.pdf") && dg.Text.Contains("blue theme"), dg.Text);
        Check("D4 the folders to grant a CLI agent are the attachment folders",
            dg.Folders.Count == 3 && dg.Folders.Contains(att) && dg.Folders.Contains(att2) && dg.Folders.Contains(att3), string.Join(", ", dg.Folders));

        var cli = AttachmentDigest.Build(new[] { img }, new[] { pdf }, AttachmentAudience.CliAgent, DigestBudget.Cli);
        Check("D5 a CLI agent is told to open them with its own Read tool (no CluadeX tool names)",
            cli.Text.Contains("Read tool") && !cli.Text.Contains("copy_file") && cli.Text.Contains("open it with Read"), cli.Text);

        var plain = AttachmentDigest.Build(Array.Empty<string>(), new[] { pdf }, AttachmentAudience.NoTools, DigestBudget.Api);
        Check("D6 a plain chat (no tools) still gets the content and the path", plain.Text.Contains("blue theme") && plain.Text.Contains(pdf) && !plain.Text.Contains("read_document"), plain.Text);

        string longTxt = Path.Combine(att2, "long.txt");
        File.WriteAllText(longTxt, new string('ก', 9000));
        var small = AttachmentDigest.Build(Array.Empty<string>(), new[] { longTxt }, AttachmentAudience.LoopBlind, DigestBudget.Local);
        Check("D7 a long file is cut to the budget with how to read on", small.Text.Contains("cut here") && small.Text.Contains("offset=4000") && small.Text.Length < 6000,
            small.Text.Length + " chars");

        string scanDir = Dir(Path.Combine("data", "attachments", "2026-10", "x4"));
        string scan = Path.Combine(scanDir, "scan.pdf");
        var sb = new PdfDocumentBuilder();
        sb.AddPage(595, 842).AddJpeg(Jpeg(200, 280), new PdfRectangle(0, 0, 595, 842));
        File.WriteAllBytes(scan, sb.Build());
        var seeing = AttachmentDigest.Build(Array.Empty<string>(), new[] { scan }, AttachmentAudience.LoopSeesImages, DigestBudget.Api);
        var blind = AttachmentDigest.Build(Array.Empty<string>(), new[] { scan }, AttachmentAudience.LoopBlind, DigestBudget.Api);
        Check("D8 a scanned PDF: a seeing model gets its pages as pictures, a blind one is told OCR is needed",
            seeing.ExtraImages.Count == 1 && File.Exists(seeing.ExtraImages[0]) && seeing.Text.Contains("attached to this message as images")
            && blind.ExtraImages.Count == 0 && blind.Text.Contains("OCR"), seeing.Text + "\n---\n" + blind.Text);
    }

    // ─── T: agent tools ───

    static async Task Tools()
    {
        Console.WriteLine("\n## T — read_document / extract_archive / copy_file");
        string attRoot = Path.Combine(Root, "data", "attachments");
        string project = Dir("project");
        var module = new AttachmentToolModule(() => attRoot);
        var ctx = new ToolModuleContext { SessionKey = "t", WorkingDirectory = project, OutputBudgetChars = 30_000 };
        ToolCall Call(ToolType type, string name, params (string K, string V)[] args)
            => new() { Type = type, ToolName = name, Arguments = args.ToDictionary(a => a.K, a => a.V) };

        Check("T0 the module resolves its names and aliases", module.ResolveToolType("read_document") == ToolType.ReadDocument
            && module.ResolveToolType("UNZIP") == ToolType.ExtractArchive && module.ResolveToolType("copy_file") == ToolType.CopyFile
            && module.ResolveToolType("read_file") == null);
        Check("T0 every schema name resolves back to the module", module.GetToolSchemas().All(s => module.ResolveToolType(s.Name) != null));

        string img = Path.Combine(attRoot, "2026-10", "x3", "hero.jpg");
        var copy = Call(ToolType.CopyFile, "copy_file", ("source", img), ("destination", "public/assets/"));
        var perm = module.DescribePermission(copy, ctx);
        Check("T1 copying into the project asks (write), naming the target", !perm.IsFree && perm.Scope == "write"
            && perm.Resource == Path.Combine(project, "public", "assets", "hero.jpg"), perm.Resource);
        var cr = await module.ExecuteAsync(copy, ctx, CancellationToken.None);
        string copied = Path.Combine(project, "public", "assets", "hero.jpg");
        Check("T1 copy_file puts the attached picture into the project's assets", cr.Success && File.Exists(copied)
            && File.ReadAllBytes(copied).SequenceEqual(File.ReadAllBytes(img)), cr.Output);
        var again = await module.ExecuteAsync(Call(ToolType.CopyFile, "copy_file", ("source", img), ("destination", "public/assets/hero.jpg")), ctx, CancellationToken.None);
        Check("T2 it never replaces an existing file without overwrite=true", !again.Success && again.Output.Contains("overwrite=true"), again.Output);

        string zipPath = Path.Combine(attRoot, "2026-10", "x1", "site.zip");
        var ex = Call(ToolType.ExtractArchive, "extract_archive", ("path", zipPath), ("destination", "web"));
        var exPerm = module.DescribePermission(ex, ctx);
        var exr = await module.ExecuteAsync(ex, ctx, CancellationToken.None);
        Check("T3 extract_archive into the project asks, unpacks there and skips the escaping entry",
            !exPerm.IsFree && exr.Success && File.Exists(Path.Combine(project, "web", "index.html")) && !File.Exists(Path.Combine(project, "escape.txt"))
            && exr.Output.Contains("Skipped 1"), exr.Output);
        var inAtt = Call(ToolType.ExtractArchive, "extract_archive", ("path", zipPath));
        Check("T3 unpacking an attachment next to itself needs no permission", module.DescribePermission(inAtt, ctx).IsFree);

        string pdf = Path.Combine(attRoot, "2026-10", "x2", "spec.pdf");
        var rd = Call(ToolType.ReadDocument, "read_document", ("path", pdf));
        var rdr = await module.ExecuteAsync(rd, ctx, CancellationToken.None);
        Check("T4 read_document reads an attached PDF, free of permission", module.DescribePermission(rd, ctx).IsFree && rdr.Success && rdr.Output.Contains("blue theme"), rdr.Output);
        string elsewhere = Path.Combine(Root, "p", "spec.pdf");
        Check("T4 reading outside the project and the attachments asks", !module.DescribePermission(Call(ToolType.ReadDocument, "read_document", ("path", elsewhere)), ctx).IsFree);
        var missing = await module.ExecuteAsync(Call(ToolType.ReadDocument, "read_document", ("path", "nope.pdf")), ctx, CancellationToken.None);
        Check("T5 a missing file fails with a plain reason", !missing.Success && missing.Output.StartsWith("No file at"), missing.Output);
        var self = await module.ExecuteAsync(Call(ToolType.CopyFile, "copy_file", ("source", project), ("destination", "inner/")), ctx, CancellationToken.None);
        Check("T5 a folder is never copied into itself", !self.Success, self.Output);
    }
}
