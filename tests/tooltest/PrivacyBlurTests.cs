using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Media;
using CluadeX.Services.Tools;

namespace ToolTest;

/// <summary>
/// media_blur and the media editor's "Blur faces &amp; plates" (Services/Media/Privacy*, Services/Tools/MediaBlurToolModule):
/// the models' raw outputs decoded to boxes on hand-made arrays, NMS, the frame → tensor sampling, the tracker that keeps
/// a lost box covered, the mosaic + blur actually destroying detail inside the box and leaving the rest alone, ffprobe's
/// JSON (a rotated phone clip), the ffmpeg argument lists, the model download refusing a wrong file, the module's
/// paths and permission — then REAL ffmpeg: a generated clip with sound goes through the decode → cover → encode pipe with
/// a stand-in detector, and the box is covered while the rest and the sound stay. With PRIVACY_MODELS (a folder holding
/// the two .onnx files) and PRIVACY_SAMPLE (a picture or clip with faces / plates), the real detectors run on it too.
/// </summary>
internal static partial class Program
{
    static async Task PrivacyBlurTests(LocalizationService loc)
    {
        // ── the models' outputs ──
        await Case("PB1", async () =>
        {
            int padW = 64, padH = 64;
            var cls = new List<float[]>(); var obj = new List<float[]>(); var bbox = new List<float[]>();
            foreach (int s in PrivacyDecode.YuNetStrides)
            {
                int cells = padW / s * (padH / s);
                cls.Add(new float[cells]); obj.Add(new float[cells]); bbox.Add(new float[cells * 4]);
            }
            int idx = 3 * 8 + 2;   // stride 8: row 3, col 2
            cls[0][idx] = 0.9f; obj[0][idx] = 0.9f;
            bbox[0][idx * 4] = 0.5f; bbox[0][idx * 4 + 1] = 0.5f; bbox[0][idx * 4 + 2] = MathF.Log(4); bbox[0][idx * 4 + 3] = MathF.Log(2);
            cls[1][0] = 0.3f; obj[1][0] = 0.3f;   // √0.09 = 0.3: under the threshold
            cls[2][0] = 2f; obj[2][0] = 0.25f;    // clamped to 1 → √0.25 = 0.5: still under 0.6
            var faces = PrivacyDecode.YuNet(cls, obj, bbox, padW, padH, 0.6f);
            var f = faces.FirstOrDefault();
            Check("PB1", "YuNet: centre (cell + d)·stride, size e^d·stride, score √(cls·obj); weak cells dropped",
                faces.Count == 1 && Math.Abs(f.X1 - 4) < 1e-3 && Math.Abs(f.Y1 - 20) < 1e-3 && Math.Abs(f.X2 - 36) < 1e-3 && Math.Abs(f.Y2 - 36) < 1e-3
                && Math.Abs(f.Score - 0.9f) < 1e-4 && f.Kind == PrivacyKind.Face,
                string.Join(" | ", faces.Select(b => $"{b.X1},{b.Y1},{b.X2},{b.Y2} s={b.Score}")));

            var rows = new float[]
            {
                0, 10, 20, 110, 60, 0, 0.9f,
                0, 5, 5, 50, 30, 0, 0.2f,    // weak
                0, 80, 80, 70, 90, 0, 0.95f, // x2 < x1: broken
            };
            var plates = PrivacyDecode.YoloEnd2End(rows, 7, 0.35f);
            Check("PB1b", "plate model end-to-end rows [batch,x1,y1,x2,y2,class,score]: a weak and a broken row skipped",
                plates.Count == 1 && plates[0].X1 == 10 && plates[0].Y2 == 60 && plates[0].Kind == PrivacyKind.Plate,
                string.Join(" | ", plates.Select(b => $"{b.X1},{b.Y1},{b.X2},{b.Y2} s={b.Score}")));

            var nms = PrivacyDecode.Nms(new[]
            {
                new PrivacyBox(0, 0, 100, 100, 0.8f, PrivacyKind.Face),
                new PrivacyBox(5, 5, 100, 100, 0.9f, PrivacyKind.Face),
                new PrivacyBox(5, 5, 100, 100, 0.5f, PrivacyKind.Plate),
                new PrivacyBox(300, 300, 340, 340, 0.7f, PrivacyKind.Face),
            }, 0.4f);
            Check("PB1c", "NMS: the stronger of two overlapping faces stays; a plate on the same spot and a far face stay too",
                nms.Count == 3 && nms[0].Score == 0.9f && nms.Any(b => b.Kind == PrivacyKind.Plate) && nms.Any(b => b.X1 == 300),
                string.Join(" | ", nms.Select(b => $"{b.Kind} {b.Score}")));
            await Task.CompletedTask;
        });

        // ── frame → tensor ──
        await Case("PB2", async () =>
        {
            // 2×1 BGR frame: pixel 0 = (B10,G20,R30), pixel 1 = (B40,G50,R60)
            var frame = new byte[] { 10, 20, 30, 40, 50, 60 };
            var bgr = PrivacyDecode.ToTensor(frame, 2, 1, 0, 0, 2, 1, 4, 2, 0, 0, 2, 1, rgb: false, scale: 1, fill: 0);
            var rgb = PrivacyDecode.ToTensor(frame, 2, 1, 0, 0, 2, 1, 4, 2, 1, 1, 2, 1, rgb: true, scale: 0.5f, fill: 7);
            int plane = 8;
            Check("PB2", "BGR order kept for YuNet (plane 0 = B), padding stays 0",
                bgr[0] == 10 && bgr[1] == 40 && bgr[plane] == 20 && bgr[2 * plane + 1] == 60 && bgr[2] == 0 && bgr[4] == 0,
                string.Join(",", bgr));
            Check("PB2b", "RGB swap for the plate model, scaled, placed at its offset, the rest filled (letterbox grey)",
                rgb[5] == 15 && rgb[6] == 30 && rgb[2 * plane + 5] == 5 && rgb[0] == 7 && rgb[plane + 5] == 10,
                string.Join(",", rgb));
            var views = PrivacyDecode.PlateViews(1920, 1080, tiles: true);
            Check("PB2c", "plates: a 1080p frame is looked at whole and in four overlapping quarters; 640p only whole",
                views.Count == 5 && views[0] == (0, 0, 1920, 1080) && views.All(v => v.X + v.W <= 1920 && v.Y + v.H <= 1080)
                && PrivacyDecode.PlateViews(640, 360, true).Count == 1, string.Join(" ", views));
            var scales = PrivacyDecode.FaceScales(1920, 1080);
            Check("PB2d", "faces: 1080p at 640 and 1280 wide; a small frame once, never enlarged",
                scales.Count == 2 && Math.Abs(scales[0] - 1 / 3f) < 1e-4 && Math.Abs(scales[1] - 2 / 3f) < 1e-4
                && PrivacyDecode.FaceScales(320, 240).SequenceEqual(new[] { 1f }), string.Join(",", scales));
            await Task.CompletedTask;
        });

        // ── the tracker ──
        await Case("PB3", async () =>
        {
            var t = new PrivacyTracker { Hold = 2 };
            var a = new PrivacyBox(10, 10, 50, 50, 0.9f, PrivacyKind.Face);
            var s1 = t.Step(new[] { a });
            var s2 = t.Step(Array.Empty<PrivacyBox>());
            var s3 = t.Step(Array.Empty<PrivacyBox>());
            var s4 = t.Step(Array.Empty<PrivacyBox>());
            Check("PB3", "a lost face stays covered for Hold steps, then goes", s1.Count == 1 && s2.Count == 1 && s3.Count == 1 && s4.Count == 0,
                $"{s1.Count} {s2.Count} {s3.Count} {s4.Count}");
            t.Step(new[] { a });
            var moved = a with { X1 = 20, X2 = 60 };
            var s5 = t.Step(new[] { moved });
            var plate = new PrivacyBox(20, 20, 60, 40, 0.8f, PrivacyKind.Plate);
            var s6 = t.Step(new[] { moved, plate });
            Check("PB3b", "a moved face follows its track (one box, the new place); a plate on it is its own track",
                s5.Count == 1 && s5[0].X1 == 20 && s6.Count == 2, $"{s5.Count} {s6.Count}");
            await Task.CompletedTask;
        });

        // ── covering a box ──
        await Case("PB4", async () =>
        {
            int w = 120, h = 80;
            byte[] Checker()
            {
                var f = new byte[w * h * 3];
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte v = (byte)(((x / 2 + y / 2) % 2) * 255);   // a 2-px checkerboard: the finest detail there is
                    int i = (y * w + x) * 3; f[i] = v; f[i + 1] = v; f[i + 2] = v;
                }
                return f;
            }
            double Spread(byte[] f, int x1, int y1, int x2, int y2)
            {
                var vals = new List<double>();
                for (int y = y1; y < y2; y++) for (int x = x1; x < x2; x++) vals.Add(f[(y * w + x) * 3]);
                double m = vals.Average();
                return Math.Sqrt(vals.Average(v => (v - m) * (v - m)));
            }
            var orig = Checker();
            var plate = Checker();
            var box = new PrivacyBox(40, 30, 80, 50, 0.9f, PrivacyKind.Plate);
            PrivacyBlur.Apply(plate, w, h, box, PrivacyStyle.Blur);
            double before = Spread(orig, 42, 32, 78, 48), after = Spread(plate, 42, 32, 78, 48);
            bool outsideSame = true;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                if ((x < 40 || x >= 80 || y < 30 || y >= 50) && plate[(y * w + x) * 3] != orig[(y * w + x) * 3]) outsideSame = false;
            Check("PB4", "blur: the detail inside the box is gone (spread falls by 90 %+), every pixel outside is untouched",
                after < before * 0.1 && outsideSame, $"spread {before:0.0} → {after:0.0}, outside same={outsideSame}");

            var face = Checker();
            PrivacyBlur.Apply(face, w, h, new PrivacyBox(20, 10, 100, 70, 0.9f, PrivacyKind.Face), PrivacyStyle.Pixelate);
            bool corner = face[(10 * w + 20) * 3] == orig[(10 * w + 20) * 3] && face[(69 * w + 99) * 3] == orig[(69 * w + 99) * 3];
            Check("PB4b", "a face is covered in an ellipse: the box's corners keep the picture, the middle is a mosaic",
                corner && Spread(face, 50, 30, 70, 50) < Spread(orig, 50, 30, 70, 50) * 0.2, $"corner kept={corner}");

            var black = Checker();
            PrivacyBlur.Apply(black, w, h, box, PrivacyStyle.Black);
            Check("PB4c", "black: the plate is a black box", black[(40 * w + 60) * 3] == 0 && black[(40 * w + 61) * 3 + 2] == 0);

            var grown = PrivacyBlur.Cover(new PrivacyBox(100, 0, 119, 20, 1, PrivacyKind.Face), w, h);
            Check("PB4d", "a face's cover grows to take in hair and chin, clamped to the frame",
                grown.Y1 == 0 && grown.X2 == w && grown.X1 < 100 && grown.Y2 > 20, $"{grown.X1},{grown.Y1},{grown.X2},{grown.Y2}");
            PrivacyBlur.Apply(Checker(), w, h, new PrivacyBox(-50, -50, 1, 1, 1, PrivacyKind.Plate), PrivacyStyle.Blur);   // must not throw
            PrivacyBlur.Apply(Checker(), w, h, new PrivacyBox(119.5f, 79.5f, 400, 400, 1, PrivacyKind.Face), PrivacyStyle.Blur);
            Check("PB4e", "boxes off the frame or a sliver at its edge do not throw", true);
            await Task.CompletedTask;
        });

        // ── ffprobe and the ffmpeg arguments ──
        await Case("PB5", async () =>
        {
            const string phone = "{\"streams\":[{\"codec_type\":\"video\",\"width\":1920,\"height\":1080,\"avg_frame_rate\":\"30000/1001\",\"r_frame_rate\":\"30/1\","
                + "\"disposition\":{\"attached_pic\":0},\"side_data_list\":[{\"rotation\":-90}]},{\"codec_type\":\"audio\"}],\"format\":{\"duration\":\"12.5\"}}";
            var p = PrivacyBlur.ParseProbe(phone, @"C:\v\clip.mp4");
            Check("PB5", "a phone clip rotated -90°: decoded portrait 1080×1920, 29.97 fps kept as 30000/1001, sound, 12.5 s",
                p != null && p.Width == 1080 && p.Height == 1920 && p.Rate == "30000/1001" && Math.Abs(p.Fps - 29.97) < 0.01 && p.HasAudio && p.Duration == 12.5 && !p.IsStill,
                p == null ? "null" : p.ToString());
            const string mp3Cover = "{\"streams\":[{\"codec_type\":\"video\",\"width\":500,\"height\":500,\"avg_frame_rate\":\"0/0\",\"disposition\":{\"attached_pic\":1}},{\"codec_type\":\"audio\"}],\"format\":{}}";
            Check("PB5b", "cover art (attached_pic) is not a video", PrivacyBlur.ParseProbe(mp3Cover, @"C:\a.mkv") == null);
            var still = PrivacyBlur.ParseProbe("{\"streams\":[{\"codec_type\":\"video\",\"width\":801,\"height\":601,\"avg_frame_rate\":\"0/0\",\"r_frame_rate\":\"25/1\"}],\"format\":{\"duration\":\"0.04\"}}", @"C:\p\car.JPG");
            Check("PB5c", "a picture: a still, its odd size kept as it is (no 4:2:0 encoder)", still != null && still.IsStill && PrivacyBlur.PipeSize(still) == (801, 601) && !still.HasAudio);
            Check("PB5d", "frame rate text: 30000/1001, 25, 0/0 and garbage", Math.Abs(PrivacyBlur.RateValue("30000/1001") - 29.97) < 0.01 && PrivacyBlur.RateValue("25") == 25
                && PrivacyBlur.RateValue("0/0") == 0 && PrivacyBlur.RateValue("x") == 0);

            var video = new PrivacyProbe(1281, 721, "30000/1001", 29.97, 10, true, false);
            var dec = PrivacyBlur.DecodeArguments(@"C:\v\a b.mp4", video);
            var enc = PrivacyBlur.EncodeArguments(@"C:\v\a b.mp4", @"C:\v\a b-blurred.mp4", video, 0);
            string e = string.Join(" ", enc);
            Check("PB5e", "decode: even crop 1280×720, constant 30000/1001 fps, raw BGR on stdout; the path is one argument",
                dec.Contains("format=bgr24,crop=1280:720:0:0") && dec.Contains("30000/1001") && dec.Contains("bgr24") && dec.Contains(@"C:\v\a b.mp4") && dec[^1] == "pipe:1",
                string.Join(" ", dec));
            Check("PB5f", "encode: raw frames in at the same rate, the original's sound copied (same container), x264, never overwrites, output last",
                e.Contains("-s 1280x720") && e.Contains("-framerate 30000/1001") && e.Contains("-map 1:a?") && e.Contains("-c:a copy") && e.Contains("libx264")
                && e.Contains("+faststart") && enc[^2] == "-n" && enc[^1] == @"C:\v\a b-blurred.mp4", e);
            var avi = string.Join(" ", PrivacyBlur.EncodeArguments(@"C:\v\old.avi", @"C:\v\old-blurred.mp4", video, 1));
            Check("PB5g", "an .avi becomes .mp4 with AAC sound; the fallback encoder (an LGPL ffmpeg) is mpeg4",
                PrivacyBlur.OutputExt(@"C:\v\old.avi") == "mp4" && avi.Contains("-c:a aac") && avi.Contains("mpeg4")
                && PrivacyBlur.OutputExt(@"C:\v\x.mov") == "mov" && PrivacyBlur.OutputExt(@"C:\p\car.png") == "png",
                avi);
            var pic = PrivacyBlur.EncodeArguments(@"C:\p\car.jpg", @"C:\p\car-blurred.jpg", still!, 0);
            Check("PB5h", "a picture: one frame, JPEG quality 2, no sound mapping", pic.Contains("-frames:v") && string.Join(" ", pic).Contains("-q:v 2") && !pic.Contains("1:a?"),
                string.Join(" ", pic));
            Check("PB5i", "detection about 5 times a second: every 6th frame at 30 fps, every frame at 4 fps",
                PrivacyBlur.DetectEvery(29.97) == 6 && PrivacyBlur.DetectEvery(4) == 1 && PrivacyBlur.DetectEvery(60) == 12);
            await Task.CompletedTask;
        });

        // ── the model download ──
        string root = Path.Combine(Path.GetTempPath(), "cluadex-pb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var keepFolder = PrivacyModels.FolderOverride;
        var keepHttp = PrivacyModels.HttpOverride;
        try
        {
            await Case("PB6", async () =>
            {
                PrivacyModels.FolderOverride = Path.Combine(root, "models");
                byte[] good = new byte[1000]; new Random(4).NextBytes(good);
                var model = new PrivacyModel("t", "t.onnx", "https://example.invalid/t.onnx", good.Length, Convert.ToHexString(SHA256.HashData(good)), "test");
                byte[] served = good.ToArray(); served[10] ^= 1;   // first: one bit off
                int calls = 0;
                PrivacyModels.HttpOverride = new HttpClient(new Stub(() => { calls++; return served; }));
                string err = await PrivacyModels.EnsureAsync(new[] { model }, null, CancellationToken.None);
                string target = PrivacyModels.PathOf(model, null);
                Check("PB6", "a download with one byte changed is refused by its SHA-256, and nothing is left on disk",
                    err != null && err.Contains("SHA-256") && !File.Exists(target) && Directory.GetFiles(PrivacyModels.Folder(null)).Length == 0, err ?? "null");
                served = good;
                err = await PrivacyModels.EnsureAsync(new[] { model }, null, CancellationToken.None);
                var again = await PrivacyModels.EnsureAsync(new[] { model }, null, CancellationToken.None);
                Check("PB6b", "the right file is kept; asked again it is not fetched a second time",
                    err == null && again == null && File.Exists(target) && calls == 2 && PrivacyModels.Missing(new[] { model }, null).Count == 0, $"err={err} calls={calls}");
                served = new byte[5000];
                File.WriteAllBytes(target, new byte[good.Length]);   // right size, wrong bytes
                err = await PrivacyModels.EnsureAsync(new[] { model }, null, CancellationToken.None);
                Check("PB6c", "a file on disk of the right size but the wrong bytes is fetched again; a download longer than pinned is cut off",
                    err != null && err.Contains("larger") && calls == 3, err ?? "null");
                Check("PB6d", "the pinned models: YuNet 232,589 bytes and its LFS SHA-256; the plate model's hash filled in (no placeholder)",
                    PrivacyModels.Face.Size == 232_589 && PrivacyModels.Face.Sha256.StartsWith("8f2383e4") && PrivacyModels.Plate.Sha256.Length == 64
                    && PrivacyModels.Plate.Url.StartsWith("https://github.com/ankandrew/open-image-models/"), PrivacyModels.Plate.Sha256);
            });

            // ── the module ──
            await Case("PB7", async () =>
            {
                PrivacyModels.FolderOverride = Path.Combine(root, "nomodels");
                var module = new MediaBlurToolModule(() => root);
                var ctx = new ToolModuleContext { SessionKey = "pb", WorkingDirectory = root };
                ToolCall C(params (string k, string v)[] a) => new() { Type = ToolType.MediaBlur, ToolName = "media_blur", Arguments = a.ToDictionary(x => x.k, x => x.v) };
                string clip = Path.Combine(root, "ถนน คลิป.mp4");
                File.WriteAllBytes(clip, new byte[16]);
                string outPath = MediaBlurToolModule.OutputPath(C(("source", clip)), ctx, clip, out _);
                Check("PB7", "the result goes beside the source as <name>-blurred.mp4", outPath == Path.Combine(root, "ถนน คลิป-blurred.mp4"), outPath);
                File.WriteAllBytes(Path.Combine(root, "taken.mp4"), new byte[1]);
                var taken = MediaBlurToolModule.OutputPath(C(("source", clip), ("output", "taken.mp4")), ctx, clip, out string why);
                var wrongKind = MediaBlurToolModule.OutputPath(C(("source", clip), ("output", "x.jpg")), ctx, clip, out _);
                Check("PB7b", "an existing output is refused (never written over); a picture name for a video is refused",
                    taken == null && why.Contains("taken.mp4") && wrongKind == null, why);
                var perm = module.DescribePermission(C(("source", "ถนน คลิป.mp4")), ctx);
                Check("PB7c", "the permission: write to the new file; the first time it names the ≈ 8 MB download",
                    !perm.IsFree && perm.Scope == "write" && perm.Resource == outPath && perm.Detail.Contains(outPath) && perm.Detail.Contains("MB") && perm.Detail.Contains("SHA-256"),
                    perm.Detail);
                var facesOnly = module.DescribePermission(C(("source", clip), ("plates", "false")), ctx);
                Check("PB7d", "faces only: the prompt says faces, and the download is only the face model (KB, not MB)",
                    facesOnly.Detail.Contains(loc.T("media.blur.whatFaces")) && facesOnly.Detail.Contains("KB"), facesOnly.Detail);
                var opt = MediaBlurToolModule.Options(C(("style", "mosaic"), ("faces", "no")));
                Check("PB7e", "options: 'mosaic' is pixelate, faces=no is off, plates on by default", opt.Style == PrivacyStyle.Pixelate && !opt.Faces && opt.Plates);
                var notMedia = await module.ExecuteAsync(C(("source", "readme.txt")), ctx, CancellationToken.None);
                File.WriteAllText(Path.Combine(root, "readme.txt"), "x");
                var txt = await module.ExecuteAsync(C(("source", "readme.txt")), ctx, CancellationToken.None);
                var none = await module.ExecuteAsync(C(("source", clip), ("faces", "false"), ("plates", "false")), ctx, CancellationToken.None);
                Check("PB7f", "worded refusals: a missing file, a text file, nothing asked for",
                    !notMedia.Success && !txt.Success && Seen(txt).Contains("readme.txt") && !none.Success && Seen(none).Contains(loc.T("media.blur.nothingAsked")),
                    Seen(notMedia) + " / " + Seen(txt) + " / " + Seen(none));
                Check("PB7g", "the names a model writes resolve through the real tool service (media_blur, blur_faces, anonymize_video)",
                    Svc.ResolveToolTypePublic("media_blur") == ToolType.MediaBlur && Svc.ResolveToolTypePublic("blur_faces") == ToolType.MediaBlur
                    && Svc.ResolveToolTypePublic("anonymize_video") == ToolType.MediaBlur);
            });

            // ── real ffmpeg, stand-in detector ──
            await Case("PB8", async () =>
            {
                string ffmpeg = Ffmpeg.Locate();
                if (ffmpeg == null || PrivacyBlur.LocateFfprobe(ffmpeg) == null) { GapIf("PB8", "the decode → cover → encode pipe (ffmpeg/ffprobe not installed here)", false); return; }
                string src = Path.Combine(root, "src clip.mp4");
                var made = await Ffmpeg.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-loglevel", "error",
                    "-f", "lavfi", "-i", "testsrc2=size=322x182:rate=25:duration=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", src }, src, TimeSpan.FromSeconds(60), CancellationToken.None);
                if (made.Outcome != FfmpegOutcome.Done) { Check("PB8", "made a 2 s test clip with sound", false, made.Detail); return; }
                string dst = Path.Combine(root, "src clip-blurred.mp4");
                var box = new PrivacyBox(100, 50, 220, 130, 0.9f, PrivacyKind.Plate);
                int detections = 0;
                var progress = new List<double>();
                var r = await PrivacyBlur.RunAsync(ffmpeg, src, dst, new PrivacyOptions(Faces: false), (f, w, h) => { detections++; return new List<PrivacyBox> { box }; },
                    f => progress.Add(f), CancellationToken.None);
                var probe = r.Outcome == FfmpegOutcome.Done ? await PrivacyBlur.ProbeAsync(ffmpeg, dst, CancellationToken.None) : null;
                Check("PB8", "a 2 s 25 fps clip: 50 frames through the pipe, detection every 5th, all covered, sound kept, even size 322×182",
                    r.Outcome == FfmpegOutcome.Done && r.Frames == 50 && detections == 10 && r.FramesCovered == 50 && r.MostPlates == 1
                    && probe != null && probe.HasAudio && probe.Width == 322 && probe.Height == 182 && Math.Abs(probe.Duration - 2) < 0.2 && progress.LastOrDefault() == 1,
                    $"{r} probe={probe}");

                // the box really is covered and the rest is not: one frame of each, compared
                async Task<byte[]> Frame(string file)
                {
                    string png = Path.Combine(root, Guid.NewGuid().ToString("N") + ".bmp");
                    await Ffmpeg.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-ss", "1", "-i", file, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", png }, png, TimeSpan.FromSeconds(30), CancellationToken.None);
                    return File.Exists(png) ? File.ReadAllBytes(png) : Array.Empty<byte>();
                }
                var a = await Frame(src); var b = await Frame(dst);
                double Diff(int x1, int y1, int x2, int y2)
                {
                    double s = 0; int n = 0;
                    for (int y = y1; y < y2; y++) for (int x = x1; x < x2; x++) { s += Math.Abs(a[y * 322 + x] - b[y * 322 + x]); n++; }
                    return n == 0 ? 0 : s / n;
                }
                bool sized = a.Length == 322 * 182 && b.Length == 322 * 182;
                double inside = sized ? Diff(120, 70, 200, 110) : 0, outside = sized ? Diff(0, 0, 90, 40) : 99;
                Check("PB8b", "inside the box the picture changed a lot; outside it only by the re-encode",
                    sized && inside > 15 && outside < 4, $"inside Δ={inside:0.0} outside Δ={outside:0.0} sizes {a.Length}/{b.Length}");

                var dup = await PrivacyBlur.RunAsync(ffmpeg, src, dst, new PrivacyOptions(), (f, w, h) => new List<PrivacyBox>(), null, CancellationToken.None);
                Check("PB8c", "an existing output is refused before anything runs (its bytes untouched)",
                    dup.Outcome == FfmpegOutcome.Failed && dup.Detail.Contains("already exists") && File.Exists(dst));

                using var cts = new CancellationTokenSource();
                string dst2 = Path.Combine(root, "stopped.mp4");
                var stopped = await PrivacyBlur.RunAsync(ffmpeg, src, dst2, new PrivacyOptions(), (f, w, h) => { cts.Cancel(); return new List<PrivacyBox>(); }, null, cts.Token);
                Check("PB8d", "stopped mid-run: Cancelled, and no half-written file is left", stopped.Outcome == FfmpegOutcome.Cancelled && !File.Exists(dst2), stopped.ToString());

                string jpg = Path.Combine(root, "car.png");   // PNG keeps an odd size (testsrc, RGB: no 4:2:0 rounding)
                await Ffmpeg.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=301x201", "-frames:v", "1", jpg }, jpg, TimeSpan.FromSeconds(30), CancellationToken.None);
                string jpgOut = Path.Combine(root, "car-blurred.png");
                var pic = await PrivacyBlur.RunAsync(ffmpeg, jpg, jpgOut, new PrivacyOptions(), (f, w, h) => new List<PrivacyBox> { box with { Kind = PrivacyKind.Face } }, null, CancellationToken.None);
                var pp = pic.Outcome == FfmpegOutcome.Done ? await PrivacyBlur.ProbeAsync(ffmpeg, jpgOut, CancellationToken.None) : null;
                Check("PB8e", "a picture (odd size 301×201): one frame covered, saved as a PNG of the same size",
                    pic.Outcome == FfmpegOutcome.Done && pic.Frames == 1 && pic.FramesCovered == 1 && pp != null && pp.Width == 301 && pp.Height == 201, $"{pic} {pp}");
            });

            // ── the real detectors, when the models and a sample are given ──
            await Case("PB9", async () =>
            {
                string models = Environment.GetEnvironmentVariable("PRIVACY_MODELS");
                string sample = Environment.GetEnvironmentVariable("PRIVACY_SAMPLE");
                string ffmpeg = Ffmpeg.Locate();
                if (string.IsNullOrEmpty(models) || string.IsNullOrEmpty(sample) || ffmpeg == null)
                { GapIf("PB9", "the real face and plate models on a real picture (set PRIVACY_MODELS and PRIVACY_SAMPLE)", false); return; }
                PrivacyModels.FolderOverride = models;
                bool verified = PrivacyModels.All.All(m => PrivacyModels.Verify(PrivacyModels.PathOf(m, null), m));
                Check("PB9", "both model files in PRIVACY_MODELS match their pinned SHA-256", verified);
                if (!verified) return;
                using var det = new PrivacyDetector(PrivacyModels.PathOf(PrivacyModels.Face, null), PrivacyModels.PathOf(PrivacyModels.Plate, null));
                string outFile = Path.Combine(root, "sample-blurred" + Path.GetExtension(sample));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = await PrivacyBlur.RunAsync(ffmpeg, sample, outFile, new PrivacyOptions(), det.Detect, null, CancellationToken.None);
                sw.Stop();
                int wantFaces = int.TryParse(Environment.GetEnvironmentVariable("PRIVACY_FACES"), out int wf) ? wf : 1;
                int wantPlates = int.TryParse(Environment.GetEnvironmentVariable("PRIVACY_PLATES"), out int wp) ? wp : 1;
                Check("PB9b", $"the real models find at least {wantFaces} face(s) and {wantPlates} plate(s) in {Path.GetFileName(sample)}",
                    r.Outcome == FfmpegOutcome.Done && r.MostFaces >= wantFaces && r.MostPlates >= wantPlates,
                    $"{r} in {sw.Elapsed.TotalSeconds:0.0} s");
                string keep = Environment.GetEnvironmentVariable("PRIVACY_KEEP");
                if (!string.IsNullOrEmpty(keep) && File.Exists(outFile)) File.Copy(outFile, Path.Combine(keep, Path.GetFileName(outFile)), overwrite: true);
            });
        }
        finally
        {
            PrivacyModels.FolderOverride = keepFolder;
            PrivacyModels.HttpOverride = keepHttp;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class Stub : HttpMessageHandler
    {
        private readonly Func<byte[]> _body;
        public Stub(Func<byte[]> body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body()) });
    }
}
