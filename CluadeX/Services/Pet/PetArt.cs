namespace CluadeX.Services.Pet;

/// <summary>What a pet is, once it has hatched. Decided from how its owner works (PetBrain.Decide), never fixed.</summary>
public enum PetSpecies { Cat, Bunny, Fox, Dragon, Slime, Ghost, Owl, Robot, Fairy, Girl, Penguin, Frog }

public enum PetPose { Stand, Walk1, Walk2, Sit, Sleep, Dangle, Jump }

public enum PetFace { Normal, Happy, Blink, Angry, Sad, Surprised, Sleepy, Love, Smug }

/// <summary>The colours of one individual (hues in degrees) — or, when <paramref name="Design"/> is set, the body
/// and colours its owner's AI drew.</summary>
public sealed record PetLook(PetSpecies Species, double Hue, double AccentHue, bool Shiny, PetDesign? Design = null);

/// <summary>
/// Pixel art drawn from code — no image files. Every sprite is composed on a 24×24 grid out of filled shapes and small
/// stamped parts, then outlined automatically (a dark pixel wherever a filled pixel meets empty space), which is what
/// keeps twelve species looking like one family. Faces are stamped last, so any species can show any feeling.
/// The result is BGRA pixels; the view scales them up with nearest-neighbour to keep them crisp.
/// </summary>
public static class PetArt
{
    public const int Size = 24;
    const double Cx = 11.5;   // the grid's centre line: shapes are symmetric about it

    // Palette letters
    //  .  empty        o  outline (auto)   b  body        s  body shade     h  highlight
    //  a  accent       c  second colour    w  white       k  dark (eyes)    p  blush pink
    //  y  yellow       r  red              n  skin        e  eye colour (robot screen)  g  glass (wings)
    sealed class Canvas
    {
        public readonly char[,] G = new char[Size, Size];
        public Canvas() { for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) G[x, y] = '.'; }
        public bool In(int x, int y) => x >= 0 && y >= 0 && x < Size && y < Size;
        public void Put(int x, int y, char c) { if (In(x, y)) G[x, y] = c; }
        public char At(int x, int y) => In(x, y) ? G[x, y] : '.';

        public void Ellipse(double cx, double cy, double rx, double ry, char c, Func<int, int, bool>? where = null)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    double dx = (x + 0.5 - (cx + 0.5)) / rx, dy = (y + 0.5 - (cy + 0.5)) / ry;
                    if (dx * dx + dy * dy <= 1.0 && (where == null || where(x, y))) G[x, y] = c;
                }
        }

        public void Rect(int x0, int y0, int x1, int y1, char c)
        {
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Put(x, y, c);
        }

        /// <summary>Stamp a pattern ('.' = leave as is) with its top-left at (x, y); mirrored = flipped left↔right.</summary>
        public void Stamp(string[] rows, int x, int y, bool mirrored = false)
        {
            for (int r = 0; r < rows.Length; r++)
                for (int i = 0; i < rows[r].Length; i++)
                {
                    char c = rows[r][mirrored ? rows[r].Length - 1 - i : i];
                    if (c != '.') Put(x + i, y + r, c);
                }
        }

        /// <summary>A pattern drawn on the left of the centre line and its mirror on the right.</summary>
        public void Pair(string[] rows, int leftX, int y)
        {
            Stamp(rows, leftX, y);
            Stamp(rows, Size - leftX - rows[0].Length, y, mirrored: true);
        }

        public void Recolor(char from, char to, Func<int, int, bool> where)
        {
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) if (G[x, y] == from && where(x, y)) G[x, y] = to;
        }

        public void Shift(int dy)
        {
            if (dy == 0) return;
            var copy = (char[,])G.Clone();
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int sy = y - dy;
                    G[x, y] = sy >= 0 && sy < Size ? copy[x, sy] : '.';
                }
        }

        /// <summary>Dark edge around everything filled.</summary>
        public void Outline()
        {
            var copy = (char[,])G.Clone();
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    if (copy[x, y] != '.') continue;
                    bool touches = false;
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < Size && ny < Size && copy[nx, ny] != '.')
                            touches = true;
                    }
                    if (touches) G[x, y] = 'o';
                }
        }
    }

    // ─── faces (left eye; the right one is its mirror) ─────────────────────
    static readonly Dictionary<PetFace, string[]> Eyes = new()
    {
        [PetFace.Normal] = ["wk.", "kk."],
        [PetFace.Happy] = [".k.", "k.k"],
        [PetFace.Blink] = ["...", "kkk"],
        [PetFace.Angry] = ["k..", ".kk"],
        [PetFace.Sad] = ["..k", "kk."],
        [PetFace.Surprised] = ["kwk", "kkk"],
        [PetFace.Sleepy] = ["...", "kk."],
        [PetFace.Love] = ["r.r", ".r."],
        [PetFace.Smug] = ["...", "kkk"],
    };

    static readonly Dictionary<PetFace, string[]> Mouths = new()
    {
        [PetFace.Normal] = ["k..k", ".kk."],
        [PetFace.Happy] = ["kkkk", ".rr."],
        [PetFace.Blink] = ["k..k", ".kk."],
        [PetFace.Angry] = [".kk.", "k..k"],
        [PetFace.Sad] = [".kk.", "k..k"],
        [PetFace.Surprised] = [".kk.", "k..k", ".kk."],
        [PetFace.Sleepy] = ["....", ".kk."],
        [PetFace.Love] = ["k..k", ".kk."],
        [PetFace.Smug] = ["...k", ".kk."],
    };

    /// <summary>Where a species' face sits: the left eye's top-left, the mouth's top-left, and whether it gets blush.</summary>
    static (int eyeX, int eyeY, int mouthY, bool blush) FaceAnchor(PetSpecies s) => s switch
    {
        PetSpecies.Frog => (6, 9, 14, true),
        PetSpecies.Owl => (7, 11, 15, false),
        PetSpecies.Robot => (7, 11, 15, false),
        PetSpecies.Fairy or PetSpecies.Girl => (7, 9, 13, true),
        PetSpecies.Slime => (7, 13, 17, true),
        _ => (7, 12, 15, true),
    };

    // ─── species ───────────────────────────────────────────────────────────
    /// <summary>An AI-drawn body, animated the same way as the built-in ones: feet added under it (or a bob for
    /// floaters), sitting settles it 2 px, the face is stamped where the design says, then the outline.</summary>
    static Canvas ComposeDesign(PetDesign d, PetPose pose, PetFace face)
    {
        var cv = new Canvas();
        bool sitting = pose is PetPose.Sit or PetPose.Sleep;
        int bottom = -1, left = Size, right = -1;
        for (int y = 0; y < Size && y < d.Body.Length; y++)
            for (int x = 0; x < Size && x < d.Body[y].Length; x++)
            {
                char c = d.Body[y][x];
                if (c == '.') continue;
                cv.G[x, y] = c;
                bottom = Math.Max(bottom, y);
                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }
        if (d.Feet && !sitting && bottom >= 0 && bottom < Size - 2)
        {
            int lift1 = pose == PetPose.Walk1 ? 1 : 0, lift2 = pose == PetPose.Walk2 ? 1 : 0;
            int drop = pose == PetPose.Dangle ? 1 : 0, tuck = pose == PetPose.Jump ? 1 : 0;
            int cx = (left + right) / 2;
            int fy = bottom + 1;
            // Under the body, a little in from each side.
            void Foot(int x0, int lift) =>
                cv.Rect(x0, fy - lift + drop - tuck, x0 + 2, fy + 1 - lift + drop * 2 - tuck, 's');
            Foot(cx - 4, lift1);
            Foot(cx + 2, lift2);
        }
        bool hasShade = d.Body.Any(r => r.Contains('s'));
        if (!hasShade && right > left)
            cv.Recolor('b', 's', (x, y) => (x - left) / (double)(right - left + 1) + (y - 0) / (double)(bottom + 1) > 1.35);

        var eye = Eyes[face];
        cv.Pair(eye, d.EyeX, d.EyeY);
        var mouth = Mouths[face];
        cv.Stamp(mouth, 10, d.MouthY);
        if (d.Blush && face is not PetFace.Angry and not PetFace.Sad)
        {
            cv.Stamp(["pp"], d.EyeX - 1, d.EyeY + 2);
            cv.Stamp(["pp"], Size - d.EyeX - 1, d.EyeY + 2);
        }
        if (sitting) cv.Shift(2);
        if (pose == PetPose.Jump) cv.Shift(-1);
        if (d.Floats && pose == PetPose.Walk1) cv.Shift(-1);
        cv.Outline();
        return cv;
    }

    /// <summary>Compose one frame.</summary>
    static Canvas Compose(PetLook look, PetPose pose, PetFace face)
    {
        if (look.Design is { } design) return ComposeDesign(design, pose, face);
        var cv = new Canvas();
        var s = look.Species;
        bool sitting = pose is PetPose.Sit or PetPose.Sleep;
        bool floats = s is PetSpecies.Ghost or PetSpecies.Slime or PetSpecies.Fairy;

        // Feet first, so the body sits over their tops.
        if (!floats && !sitting && s != PetSpecies.Robot)
        {
            int lift1 = pose == PetPose.Walk1 ? 1 : 0, lift2 = pose == PetPose.Walk2 ? 1 : 0;
            int drop = pose == PetPose.Dangle ? 1 : 0;
            int tuck = pose == PetPose.Jump ? 1 : 0;
            char foot = s is PetSpecies.Penguin ? 'y' : s is PetSpecies.Girl ? 'k' : 's';
            cv.Rect(7, 20 - lift1 + drop - tuck, 9, 21 - lift1 + drop * 2 - tuck, foot);
            cv.Rect(14, 20 - lift2 + drop - tuck, 16, 21 - lift2 + drop * 2 - tuck, foot);
        }

        switch (s)
        {
            case PetSpecies.Cat:
                cv.Pair(["b...", "bb..", "bpb.", "bppb"], 5, 4);              // ears
                cv.Ellipse(Cx, 13.5, 7.6, 6.6, 'b');
                cv.Stamp(["..bb", ".b..", "b...", "b..."], 19, 9);              // tail
                cv.Stamp(["s.s"], 10, 8);                                        // forehead stripes
                break;
            case PetSpecies.Bunny:
                cv.Pair(["bb", "bp", "bp", "bp", "bp", "bb"], 7, 1);           // long ears
                cv.Ellipse(Cx, 13.5, 7.4, 6.6, 'b');
                cv.Stamp(["ww", "ww"], 20, 15);                                  // tail puff
                break;
            case PetSpecies.Fox:
                cv.Pair(["k...", "bb..", "bwb.", "bwwb"], 5, 4);
                cv.Ellipse(Cx, 13.5, 7.4, 6.4, 'b');
                cv.Ellipse(Cx, 16.5, 4.2, 3.2, 'w');                            // white chest
                cv.Stamp([".bb.", "bbbb", "bbbb", ".bww", "..ww"], 19, 10);      // bushy tail
                break;
            case PetSpecies.Dragon:
                cv.Pair(["y.", "yy"], 7, 5);                                     // horns
                cv.Pair(["..cc", ".ccc", "cccc", ".c.."], 1, 10);                // wings
                cv.Ellipse(Cx, 13.5, 7.2, 6.6, 'b');
                cv.Ellipse(Cx, 16.5, 4.0, 3.4, 'a');                            // belly
                cv.Stamp(["..y", ".bb", "bb."], 19, 15);                          // tail tip
                break;
            case PetSpecies.Slime:
            {
                int squish = pose == PetPose.Walk1 ? 1 : 0;
                cv.Ellipse(Cx, 16.5 + squish * 0.5, 8.6 + squish, 6.8 - squish, 'b', (x, y) => y <= 21);
                cv.Rect(4 - squish, 20, 19 + squish, 21, 'b');
                cv.Stamp(["b", "b"], 12, 8 + squish);                              // drip on top
                cv.Stamp(["ww", "w."], 6, 12 + squish);                             // gloss
                break;
            }
            case PetSpecies.Ghost:
            {
                cv.Ellipse(Cx, 11.5, 7.4, 7.0, 'b');
                cv.Rect(5, 11, 18, 18, 'b');
                bool alt = pose == PetPose.Walk1;
                for (int x = 5; x <= 18; x++)
                    if (((x - 5) / 2 + (alt ? 1 : 0)) % 2 == 0) cv.Rect(x, 19, x, 20, 'b');
                cv.Stamp(["hh", "h."], 7, 6);
                break;
            }
            case PetSpecies.Owl:
                cv.Pair(["b.", "bb"], 5, 5);                                     // ear tufts
                cv.Ellipse(Cx, 13.5, 7.4, 6.8, 'b');
                cv.Pair(["s.", "ss", "ss", "s."], 4, 13);                        // wings
                cv.Pair(["www", "www", "www"], 6, 10);                           // eye discs
                cv.Ellipse(Cx, 17.0, 3.6, 2.6, 'a');                            // belly
                cv.Stamp(["y", "y"], 11, 14); cv.Stamp(["y"], 12, 14);           // beak
                break;
            case PetSpecies.Robot:
                cv.Stamp(["r", "s", "s"], 11, 2); cv.Stamp(["r"], 12, 2);       // antenna
                cv.Rect(5, 5, 18, 18, 'b');
                cv.Put(5, 5, '.'); cv.Put(18, 5, '.'); cv.Put(5, 18, '.'); cv.Put(18, 18, '.');
                cv.Rect(7, 9, 16, 16, 'k');                                       // screen
                cv.Rect(6, 19, 9, 21, 's'); cv.Rect(14, 19, 17, 21, 's');          // feet blocks
                if (pose == PetPose.Walk1) cv.Rect(6, 21, 9, 21, '.');
                if (pose == PetPose.Walk2) cv.Rect(14, 21, 17, 21, '.');
                cv.Pair(["s", "s"], 3, 12);                                       // arms
                break;
            case PetSpecies.Fairy:
            case PetSpecies.Girl:
            {
                bool fairy = s == PetSpecies.Fairy;
                if (fairy)
                {
                    cv.Pair(["gg..", "ggg.", "gggg", ".ggg", "..gg"], 0, 9);     // wings
                    cv.Rect(8, 0, 15, 0, 'y'); cv.Put(7, 1, 'y'); cv.Put(16, 1, 'y'); cv.Rect(8, 2, 15, 2, 'y');   // halo
                    cv.Rect(8, 1, 15, 1, '.');
                }
                else
                    cv.Pair(["cc.", "ccc", "ccc", "ccc", ".cc", ".c."], 2, 6); // twin tails
                cv.Ellipse(Cx, 9.5, 6.4, 6.0, 'n');                              // head
                cv.Ellipse(Cx, 7.0, 6.6, 4.2, 'c', (x, y) => y <= 8);            // hair top
                cv.Pair(["c", "c", "c", "c"], 5, 8);                              // side locks
                cv.Stamp(["c.c.c.cc"], 8, 9);                                     // bangs
                cv.Rect(8, 15, 15, 15, 'n');                                      // neck/shoulders
                // dress (a bell)
                for (int y = 16; y <= 20; y++) cv.Rect(7 - (y - 16) / 2, y, 16 + (y - 16) / 2, y, 'b');
                cv.Rect(10, 16, 13, 16, 'w');                                     // collar
                if (!fairy) cv.Stamp(["rr.rr", ".rrr.", "rr.rr"], 14, 2);        // bow
                if (fairy && pose is PetPose.Walk1) cv.Shift(-1);                // floats up and down
                break;
            }
            case PetSpecies.Penguin:
                cv.Ellipse(Cx, 13.0, 7.2, 7.4, 'b');
                cv.Ellipse(Cx, 14.5, 5.0, 5.4, 'w');
                cv.Pair(["b.", "bb", "bb", "b."], 3, 13);                         // flippers
                cv.Stamp(["yy"], 11, 14);
                break;
            case PetSpecies.Frog:
                cv.Pair([".bbb.", "bbbbb", "bbbbb"], 5, 6);                      // eye bumps
                cv.Ellipse(Cx, 14.5, 8.2, 6.0, 'b');
                cv.Ellipse(Cx, 17.0, 5.0, 3.0, 'a');
                cv.Pair(["ww.", "ww."], 6, 7);                                    // whites of the eyes
                break;
        }

        // Shade: the lower-right of the body, a little; a highlight on the upper-left for round species.
        cv.Recolor('b', 's', (x, y) => (x - 4) / 16.0 + (y - 6) / 16.0 > 1.35);
        if (s is PetSpecies.Cat or PetSpecies.Bunny or PetSpecies.Fox or PetSpecies.Dragon or PetSpecies.Owl or PetSpecies.Penguin or PetSpecies.Frog)
            cv.Stamp(["hh", "h."], 6, 9);

        // Face
        var (ex, ey, my, blush) = FaceAnchor(s);
        var eye = Eyes[face];
        if (s == PetSpecies.Robot) eye = eye.Select(r => r.Replace('k', 'e').Replace('w', 'e')).ToArray();
        cv.Pair(eye, ex, ey);
        var mouth = Mouths[face];
        if (s == PetSpecies.Robot) mouth = mouth.Select(r => r.Replace('k', 'e').Replace('r', 'e')).ToArray();
        if (s is PetSpecies.Owl) mouth = [];   // the beak is the mouth
        if (mouth.Length > 0) cv.Stamp(mouth, 10, my);
        if (blush && face is not PetFace.Angry and not PetFace.Sad)
        {
            cv.Stamp(["pp"], ex - 1, ey + 2);
            cv.Stamp(["pp"], Size - ex - 1, ey + 2);
        }

        // Sitting: the body settles 2 px (the feet were never drawn); sleeping is sitting with the eyes shut.
        if (sitting) cv.Shift(2);
        if (pose == PetPose.Jump) cv.Shift(-1);

        cv.Outline();
        return cv;
    }

    /// <summary>The egg every pet starts as. <paramref name="progress"/> 0..1 draws the cracks; the spots come from
    /// <paramref name="spotSeed"/>, so no two eggs are speckled alike.</summary>
    static Canvas ComposeEgg(double progress, int spotSeed)
    {
        var cv = new Canvas();
        cv.Ellipse(Cx, 14.0, 6.6, 8.6, 'b', (x, y) => y >= 9);
        cv.Ellipse(Cx, 12.5, 5.6, 7.4, 'b', (x, y) => y < 12);
        var rng = new Random(spotSeed);
        for (int i = 0; i < 6; i++)
        {
            int x = 6 + rng.Next(12), y = 8 + rng.Next(13);
            if (cv.At(x, y) == 'b') { cv.Put(x, y, 'a'); if (rng.Next(2) == 0 && cv.At(x + 1, y) == 'b') cv.Put(x + 1, y, 'a'); }
        }
        cv.Recolor('b', 's', (x, y) => (x - 5) / 13.0 + (y - 6) / 17.0 > 1.25);
        cv.Stamp(["hh", "h."], 8, 8);
        if (progress >= 0.35) cv.Stamp(["k..", ".k.", "k.k"], 13, 9);
        if (progress >= 0.65) cv.Stamp(["k.k.", ".k.k"], 7, 13);
        if (progress >= 0.9) cv.Stamp(["..k..", ".k.k.", "k...k"], 9, 5);
        cv.Outline();
        return cv;
    }

    // ─── colour ────────────────────────────────────────────────────────────
    static uint Hsl(double h, double s, double l, byte a = 255)
    {
        h = ((h % 360) + 360) % 360 / 360.0;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Hue(double t) { t = t < 0 ? t + 1 : t > 1 ? t - 1 : t; return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p; }
        byte r = (byte)Math.Round(Hue(h + 1 / 3.0) * 255), g = (byte)Math.Round(Hue(h) * 255), b = (byte)Math.Round(Hue(h - 1 / 3.0) * 255);
        return (uint)(a << 24 | r << 16 | g << 8 | b);
    }

    /// <summary>Saturation and lightness that suit each species (a navy penguin, a pale ghost …).</summary>
    static (double s, double l) Tone(PetSpecies s) => s switch
    {
        PetSpecies.Ghost => (0.35, 0.88),
        PetSpecies.Penguin => (0.35, 0.30),
        PetSpecies.Robot => (0.12, 0.70),
        PetSpecies.Slime => (0.65, 0.66),
        PetSpecies.Fairy or PetSpecies.Girl => (0.55, 0.70),
        _ => (0.60, 0.64),
    };

    static Dictionary<char, uint> Palette(PetLook look)
    {
        var (sat, lig) = Tone(look.Species);
        double h = look.Hue, ah = look.AccentHue;
        if (look.Shiny) { sat = Math.Min(1, sat + 0.25); h += 180; }
        byte bodyAlpha = look.Species == PetSpecies.Ghost ? (byte)225 : (byte)255;
        return new()
        {
            ['o'] = Hsl(h, Math.Min(1, sat + 0.1), 0.16),
            ['b'] = Hsl(h, sat, lig, bodyAlpha),
            ['s'] = Hsl(h, sat, lig - 0.13, bodyAlpha),
            ['h'] = Hsl(h, sat * 0.6, Math.Min(0.96, lig + 0.2), bodyAlpha),
            ['a'] = Hsl(h, sat * 0.7, Math.Min(0.93, lig + 0.18)),
            ['c'] = Hsl(ah, 0.55, 0.55),
            ['w'] = 0xFFFDFBF7,
            ['k'] = 0xFF231C2B,
            ['p'] = 0xFFF59AB0,
            ['y'] = 0xFFF8C93E,
            ['r'] = 0xFFE5485D,
            ['n'] = 0xFFFBDCC4,
            ['e'] = 0xFF5CF2E0,
            ['g'] = Hsl(ah + 40, 0.75, 0.88, 215),
        };
    }

    /// <summary>One frame as BGRA pixels (Size × Size).</summary>
    public static uint[] Render(PetLook look, PetPose pose, PetFace face) =>
        ToPixels(Compose(look, pose, face), look.Design is { } d ? DesignPalette(d) : Palette(look));

    static uint ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return 0xFF000000u | Convert.ToUInt32(hex, 16);
    }

    static (double h, double s, double l) ToHsl(uint argb)
    {
        double r = (argb >> 16 & 0xFF) / 255.0, g = (argb >> 8 & 0xFF) / 255.0, b = (argb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    /// <summary>The design's own colours; anything it left out is derived from its body colour.</summary>
    static Dictionary<char, uint> DesignPalette(PetDesign d)
    {
        uint body = ParseHex(d.Palette.GetValueOrDefault("b", "#A0C4FF"));
        var (h, s, l) = ToHsl(body);
        var pal = new Dictionary<char, uint>
        {
            ['o'] = Hsl(h, Math.Min(1, s + 0.1), Math.Min(0.18, l * 0.3)),
            ['b'] = body,
            ['s'] = Hsl(h, s, Math.Max(0.08, l - 0.13)),
            ['h'] = Hsl(h, s * 0.6, Math.Min(0.96, l + 0.2)),
            ['a'] = Hsl(h, s * 0.7, Math.Min(0.93, l + 0.18)),
            ['c'] = Hsl(h + 150, 0.55, 0.55),
            ['w'] = 0xFFFDFBF7,
            ['k'] = 0xFF231C2B,
            ['p'] = 0xFFF59AB0,
            ['y'] = 0xFFF8C93E,
            ['r'] = 0xFFE5485D,
            ['n'] = 0xFFFBDCC4,
            ['g'] = Hsl(h + 40, 0.75, 0.88, 215),
        };
        foreach (var (key, hex) in d.Palette)
            if (key.Length == 1 && key[0] != 'g' && key[0] != 'o') pal[key[0]] = ParseHex(hex);
        return pal;
    }

    /// <summary>The egg as BGRA pixels; <paramref name="hue"/> is its colour.</summary>
    public static uint[] RenderEgg(double hue, double progress, int spotSeed)
    {
        var pal = new Dictionary<char, uint>
        {
            ['o'] = Hsl(hue, 0.45, 0.2),
            ['b'] = Hsl(hue, 0.55, 0.80),
            ['s'] = Hsl(hue, 0.50, 0.70),
            ['h'] = 0xFFFFFFFF,
            ['a'] = Hsl(hue + 150, 0.55, 0.62),
            ['k'] = Hsl(hue, 0.4, 0.25),
        };
        return ToPixels(ComposeEgg(progress, spotSeed), pal);
    }

    static uint[] ToPixels(Canvas cv, Dictionary<char, uint> pal)
    {
        var px = new uint[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                char c = cv.G[x, y];
                if (c != '.' && pal.TryGetValue(c, out uint argb)) px[y * Size + x] = Premultiply(argb);
            }
        return px;
    }

    /// <summary>WPF's Pbgra32 wants premultiplied colour.</summary>
    static uint Premultiply(uint argb)
    {
        uint a = argb >> 24;
        if (a == 255) return argb;
        uint r = (argb >> 16 & 0xFF) * a / 255, g = (argb >> 8 & 0xFF) * a / 255, b = (argb & 0xFF) * a / 255;
        return a << 24 | r << 16 | g << 8 | b;
    }
}
