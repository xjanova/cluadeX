using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CluadeX.Services.Media;

/// <summary>What a box covers.</summary>
public enum PrivacyKind { Face, Plate }

/// <summary>A box in frame pixels (x1, y1) – (x2, y2), with the detector's score.</summary>
public readonly record struct PrivacyBox(float X1, float Y1, float X2, float Y2, float Score, PrivacyKind Kind)
{
    public float Width => X2 - X1;
    public float Height => Y2 - Y1;
    public float Area => Math.Max(0, Width) * Math.Max(0, Height);

    public static float IoU(PrivacyBox a, PrivacyBox b)
    {
        float ix = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1), iy = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);
        if (ix <= 0 || iy <= 0) return 0;
        float inter = ix * iy, union = a.Area + b.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Grown by the given fractions of its own size on each side, clamped to the frame.</summary>
    public PrivacyBox Grow(float left, float top, float right, float bottom, int frameW, int frameH)
    {
        float w = Width, h = Height;
        return this with
        {
            X1 = Math.Clamp(X1 - w * left, 0, frameW),
            Y1 = Math.Clamp(Y1 - h * top, 0, frameH),
            X2 = Math.Clamp(X2 + w * right, 0, frameW),
            Y2 = Math.Clamp(Y2 + h * bottom, 0, frameH),
        };
    }
}

/// <summary>
/// The pure parts of detection — picture to tensor, the models' raw outputs to boxes, non-maximum suppression — kept
/// apart from ONNX Runtime so the harness checks them on hand-made arrays.
/// </summary>
public static class PrivacyDecode
{
    public static readonly int[] YuNetStrides = { 8, 16, 32 };

    /// <summary>
    /// YuNet's outputs (cls_8/16/32, obj_*, bbox_* per stride; each a flat row-major grid over the padded input) to face
    /// boxes in INPUT pixels, as OpenCV's FaceDetectorYN decodes them: score = √(cls·obj), centre = (cell + d)·stride,
    /// size = e^d·stride.
    /// </summary>
    public static List<PrivacyBox> YuNet(IReadOnlyList<float[]> cls, IReadOnlyList<float[]> obj, IReadOnlyList<float[]> bbox,
        int padW, int padH, float threshold)
    {
        var faces = new List<PrivacyBox>();
        for (int i = 0; i < YuNetStrides.Length && i < cls.Count; i++)
        {
            int s = YuNetStrides[i], cols = padW / s, rows = padH / s;
            float[] c = cls[i], o = obj[i], b = bbox[i];
            int cells = Math.Min(rows * cols, Math.Min(c.Length, Math.Min(o.Length, b.Length / 4)));
            for (int idx = 0; idx < cells; idx++)
            {
                float score = MathF.Sqrt(Math.Clamp(c[idx], 0f, 1f) * Math.Clamp(o[idx], 0f, 1f));
                if (score < threshold) continue;
                int r = idx / cols, col = idx % cols;
                float cx = (col + b[idx * 4]) * s, cy = (r + b[idx * 4 + 1]) * s;
                float w = MathF.Exp(b[idx * 4 + 2]) * s, h = MathF.Exp(b[idx * 4 + 3]) * s;
                if (!float.IsFinite(w) || !float.IsFinite(h) || w <= 0 || h <= 0) continue;
                faces.Add(new PrivacyBox(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2, score, PrivacyKind.Face));
            }
        }
        return faces;
    }

    /// <summary>
    /// The plate model's end-to-end output (rows of [batch, x1, y1, x2, y2, class, score], NMS already done inside) to
    /// boxes in LETTERBOXED input pixels. Rows shorter than 7 are skipped.
    /// </summary>
    public static List<PrivacyBox> YoloEnd2End(float[] rows, int rowLength, float threshold)
    {
        var plates = new List<PrivacyBox>();
        if (rowLength < 7) return plates;
        for (int i = 0; i + rowLength <= rows.Length; i += rowLength)
        {
            float score = rows[i + 6];
            if (!(score >= threshold)) continue;
            float x1 = rows[i + 1], y1 = rows[i + 2], x2 = rows[i + 3], y2 = rows[i + 4];
            if (!(x2 > x1) || !(y2 > y1)) continue;
            plates.Add(new PrivacyBox(x1, y1, x2, y2, score, PrivacyKind.Plate));
        }
        return plates;
    }

    /// <summary>
    /// Highest score first; a box overlapping a kept one of the same kind by more than <paramref name="iou"/> goes. A box
    /// mostly (60 %+) inside or around a kept one — the same plate seen whole and in a quarter view, cut at the edge —
    /// is merged into it (the union), so one plate gets one cover, not two overlapping ones.
    /// </summary>
    public static List<PrivacyBox> Nms(IEnumerable<PrivacyBox> boxes, float iou)
    {
        var keep = new List<PrivacyBox>();
        foreach (var b in boxes.OrderByDescending(b => b.Score))
        {
            int hit = keep.FindIndex(k => k.Kind == b.Kind && (PrivacyBox.IoU(k, b) > iou || Contained(k, b) > 0.6f));
            if (hit < 0) { keep.Add(b); continue; }
            var k = keep[hit];
            if (Contained(k, b) > 0.6f)
                keep[hit] = k with { X1 = Math.Min(k.X1, b.X1), Y1 = Math.Min(k.Y1, b.Y1), X2 = Math.Max(k.X2, b.X2), Y2 = Math.Max(k.Y2, b.Y2) };
        }
        return keep;
    }

    /// <summary>How much of the smaller box lies inside the other (0..1).</summary>
    public static float Contained(PrivacyBox a, PrivacyBox b)
    {
        float ix = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1), iy = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);
        if (ix <= 0 || iy <= 0) return 0;
        float small = Math.Min(a.Area, b.Area);
        return small <= 0 ? 0 : ix * iy / small;
    }

    /// <summary>
    /// Sample the BGR24 frame's rectangle (<paramref name="rx"/>, <paramref name="ry"/>, <paramref name="rw"/>×<paramref name="rh"/>)
    /// bilinearly into a planar float tensor of <paramref name="tensorW"/>×<paramref name="tensorH"/>, at offset
    /// (<paramref name="ox"/>, <paramref name="oy"/>) and size <paramref name="dw"/>×<paramref name="dh"/>; the rest keeps
    /// <paramref name="fill"/>. <paramref name="rgb"/> swaps the channel order; each value is multiplied by <paramref name="scale"/>.
    /// </summary>
    public static float[] ToTensor(byte[] frame, int frameW, int frameH, int rx, int ry, int rw, int rh,
        int tensorW, int tensorH, int ox, int oy, int dw, int dh, bool rgb, float scale, float fill)
    {
        int plane = tensorW * tensorH;
        var t = new float[plane * 3];
        if (fill != 0) Array.Fill(t, fill);
        if (dw <= 0 || dh <= 0 || rw <= 0 || rh <= 0) return t;
        int stride = frameW * 3;
        var x0 = new int[dw];
        var x1 = new int[dw];
        var fx = new float[dw];
        for (int x = 0; x < dw; x++)
        {
            float sx = rx + (x + 0.5f) * rw / dw - 0.5f;
            sx = Math.Clamp(sx, 0, frameW - 1);
            int a = (int)sx;
            x0[x] = a * 3;
            x1[x] = Math.Min(a + 1, frameW - 1) * 3;
            fx[x] = sx - a;
        }
        int c0 = rgb ? 2 : 0, c2 = rgb ? 0 : 2;   // tensor channel 0 takes R when rgb, else B
        for (int y = 0; y < dh; y++)
        {
            int ty = oy + y;
            if (ty < 0 || ty >= tensorH) continue;
            float sy = Math.Clamp(ry + (y + 0.5f) * rh / dh - 0.5f, 0, frameH - 1);
            int a = (int)sy, b = Math.Min(a + 1, frameH - 1);
            float fy = sy - a;
            int rowA = a * stride, rowB = b * stride;
            int baseIdx = ty * tensorW;
            for (int x = 0; x < dw; x++)
            {
                int tx = ox + x;
                if (tx < 0 || tx >= tensorW) continue;
                float wx = fx[x];
                int pa = rowA + x0[x], pb = rowA + x1[x], pc = rowB + x0[x], pd = rowB + x1[x];
                for (int ch = 0; ch < 3; ch++)
                {
                    float top = frame[pa + ch] + (frame[pb + ch] - frame[pa + ch]) * wx;
                    float bot = frame[pc + ch] + (frame[pd + ch] - frame[pc + ch]) * wx;
                    float v = (top + (bot - top) * fy) * scale;
                    int plane0 = ch == 0 ? c0 : ch == 2 ? c2 : 1;   // which tensor plane this source channel lands in
                    t[plane0 * plane + baseIdx + tx] = v;
                }
            }
        }
        return t;
    }

    /// <summary>The rectangles a frame is looked at in for plates: the whole frame, and on a frame of 1280 px or more
    /// also four overlapping quarters (a far plate is a few pixels once 1080p is shrunk to 640).</summary>
    public static List<(int X, int Y, int W, int H)> PlateViews(int frameW, int frameH, bool tiles)
    {
        var views = new List<(int, int, int, int)> { (0, 0, frameW, frameH) };
        if (!tiles || Math.Max(frameW, frameH) < 1280) return views;
        int tw = (int)Math.Ceiling(frameW * 0.6), th = (int)Math.Ceiling(frameH * 0.6);
        foreach (var (x, y) in new[] { (0, 0), (frameW - tw, 0), (0, frameH - th), (frameW - tw, frameH - th) })
            views.Add((x, y, tw, th));
        return views;
    }

    /// <summary>The scales a frame is looked at in for faces: its longest side shrunk to 640 (big, near faces), and on a
    /// frame over 960 px also to 1280 at most (small, far faces).</summary>
    public static List<float> FaceScales(int frameW, int frameH)
    {
        int longest = Math.Max(frameW, frameH);
        var scales = new List<float> { Math.Min(1f, 640f / longest) };
        if (longest > 960) scales.Add(Math.Min(1f, 1280f / longest));
        return scales.Distinct().ToList();
    }
}

/// <summary>
/// The ONNX Runtime side: the face and/or plate model loaded once, run on BGR24 frames. Not thread-safe across
/// <see cref="Detect"/> calls on one instance from many threads at once (one blur job uses one detector).
/// </summary>
public sealed class PrivacyDetector : IDisposable
{
    public const float FaceThreshold = 0.6f;
    public const float PlateThreshold = 0.35f;

    private readonly InferenceSession? _face;
    private readonly InferenceSession? _plate;
    private readonly string _faceInput = "input", _plateInput = "images";
    private readonly int _faceFixedW, _faceFixedH, _plateW = 640, _plateH = 640;
    private readonly bool _tiles;

    private static readonly object NativeGate = new();
    private static bool _nativeLoaded;

    /// <summary>
    /// Load OUR onnxruntime.dll (the one the NuGet package put beside the app) before the first session. Windows 11 ships
    /// its own, older onnxruntime.dll in System32 (1.17 on the owner's PC, 2026-11-14); when the app's copy is not found
    /// first the managed 1.28 binding calls into it and the process dies with an AccessViolation — seen in the harness.
    /// Once a module of that name is loaded, every later P/Invoke resolves to it.
    /// </summary>
    public static void EnsureNativeLoaded()
    {
        lock (NativeGate)
        {
            if (_nativeLoaded) return;
            string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
                System.Runtime.InteropServices.Architecture.X86 => "win-x86",
                _ => "win-x64",
            };
            string baseDir = AppContext.BaseDirectory;
            foreach (var candidate in new[]
            {
                System.IO.Path.Combine(baseDir, "onnxruntime.dll"),
                System.IO.Path.Combine(baseDir, "runtimes", arch, "native", "onnxruntime.dll"),
            })
            {
                if (System.IO.File.Exists(candidate) && System.Runtime.InteropServices.NativeLibrary.TryLoad(candidate, out _))
                {
                    _nativeLoaded = true;
                    return;
                }
            }
            throw new InvalidOperationException("onnxruntime.dll is missing beside the app — reinstall CluadeX (the one in System32 is a different version and is not used)");
        }
    }

    public PrivacyDetector(string? faceModel, string? plateModel, bool tiles = true)
    {
        EnsureNativeLoaded();
        var opt = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8),
        };
        _tiles = tiles;
        try
        {
            if (faceModel != null)
            {
                _face = new InferenceSession(faceModel, opt);
                var (name, meta) = _face.InputMetadata.First();
                _faceInput = name;
                var d = meta.Dimensions;
                if (d.Length == 4 && d[2] > 0 && d[3] > 0) { _faceFixedH = d[2]; _faceFixedW = d[3]; }
            }
            if (plateModel != null)
            {
                _plate = new InferenceSession(plateModel, opt);
                var (name, meta) = _plate.InputMetadata.First();
                _plateInput = name;
                var d = meta.Dimensions;
                if (d.Length == 4 && d[2] > 0 && d[3] > 0) { _plateH = d[2]; _plateW = d[3]; }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Faces and plates in a BGR24 frame, in frame pixels, after non-maximum suppression.</summary>
    public List<PrivacyBox> Detect(byte[] frame, int w, int h)
    {
        var all = new List<PrivacyBox>();
        if (_face != null)
            foreach (float s in PrivacyDecode.FaceScales(w, h)) all.AddRange(Faces(frame, w, h, s));
        if (_plate != null)
            foreach (var v in PrivacyDecode.PlateViews(w, h, _tiles)) all.AddRange(Plates(frame, w, h, v));
        return PrivacyDecode.Nms(all, 0.4f);
    }

    private List<PrivacyBox> Faces(byte[] frame, int w, int h, float scale)
    {
        int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(h * scale));
        int padW, padH;
        if (_faceFixedW > 0)
        {
            // a model with a fixed input: fit inside it
            float fit = Math.Min((float)_faceFixedW / sw, (float)_faceFixedH / sh);
            if (fit < 1) { sw = Math.Max(1, (int)(sw * fit)); sh = Math.Max(1, (int)(sh * fit)); }
            padW = _faceFixedW; padH = _faceFixedH;
        }
        else
        {
            padW = (sw - 1) / 32 * 32 + 32;
            padH = (sh - 1) / 32 * 32 + 32;
        }
        float[] data = PrivacyDecode.ToTensor(frame, w, h, 0, 0, w, h, padW, padH, 0, 0, sw, sh, rgb: false, scale: 1f, fill: 0f);
        var input = NamedOnnxValue.CreateFromTensor(_faceInput, new DenseTensor<float>(data, new[] { 1, 3, padH, padW }));
        using var results = _face!.Run(new[] { input });
        float[] Out(string name) => results.FirstOrDefault(r => r.Name == name)?.AsTensor<float>().ToArray() ?? Array.Empty<float>();
        var cls = PrivacyDecode.YuNetStrides.Select(s => Out("cls_" + s)).ToList();
        var obj = PrivacyDecode.YuNetStrides.Select(s => Out("obj_" + s)).ToList();
        var bbox = PrivacyDecode.YuNetStrides.Select(s => Out("bbox_" + s)).ToList();
        float back = (float)w / sw;   // input pixels → frame pixels
        return PrivacyDecode.YuNet(cls, obj, bbox, padW, padH, FaceThreshold)
            .Select(b => b with { X1 = b.X1 * back, Y1 = b.Y1 * back, X2 = b.X2 * back, Y2 = b.Y2 * back })
            .ToList();
    }

    private List<PrivacyBox> Plates(byte[] frame, int w, int h, (int X, int Y, int W, int H) v)
    {
        float r = Math.Min((float)_plateW / v.W, (float)_plateH / v.H);
        int dw = Math.Max(1, (int)Math.Round(v.W * r)), dh = Math.Max(1, (int)Math.Round(v.H * r));
        int ox = (_plateW - dw) / 2, oy = (_plateH - dh) / 2;
        float[] data = PrivacyDecode.ToTensor(frame, w, h, v.X, v.Y, v.W, v.H, _plateW, _plateH, ox, oy, dw, dh,
            rgb: true, scale: 1f / 255f, fill: 114f / 255f);
        var input = NamedOnnxValue.CreateFromTensor(_plateInput, new DenseTensor<float>(data, new[] { 1, 3, _plateH, _plateW }));
        using var results = _plate!.Run(new[] { input });
        var t = results.First().AsTensor<float>();
        int rowLength = t.Dimensions.Length >= 2 ? t.Dimensions[^1] : 7;
        float sx = (float)v.W / dw, sy = (float)v.H / dh;
        return PrivacyDecode.YoloEnd2End(t.ToArray(), rowLength, PlateThreshold)
            .Select(b => b with
            {
                X1 = Math.Clamp(v.X + (b.X1 - ox) * sx, 0, w), Y1 = Math.Clamp(v.Y + (b.Y1 - oy) * sy, 0, h),
                X2 = Math.Clamp(v.X + (b.X2 - ox) * sx, 0, w), Y2 = Math.Clamp(v.Y + (b.Y2 - oy) * sy, 0, h),
            })
            .Where(b => b.Width >= 2 && b.Height >= 2)
            .ToList();
    }

    public void Dispose()
    {
        _face?.Dispose();
        _plate?.Dispose();
    }
}
