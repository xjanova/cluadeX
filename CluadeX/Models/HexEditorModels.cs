using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CluadeX.Models;

/// <summary>
/// One row of the hex editor (16 bytes by default).
/// Held as observable so cell edits update the grid immediately.
/// </summary>
public class HexRow : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public long Offset { get; set; }
    public string OffsetHex => $"{Offset:X8}";
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
    public int Length => Bytes.Length;

    /// <summary>Hex representation for display: "DE AD BE EF  ..."</summary>
    public string HexDisplay
    {
        get
        {
            var sb = new StringBuilder(Bytes.Length * 3);
            for (int i = 0; i < Bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                if (i == 8) sb.Append(' '); // extra gap mid-row
                sb.Append(Bytes[i].ToString("X2"));
            }
            // pad short rows so columns align
            int missing = 16 - Bytes.Length;
            for (int i = 0; i < missing; i++) sb.Append("   ");
            return sb.ToString();
        }
    }

    /// <summary>ASCII representation: printable chars or '.'.</summary>
    public string AsciiDisplay
    {
        get
        {
            var sb = new StringBuilder(Bytes.Length);
            foreach (var b in Bytes)
            {
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '·');
            }
            return sb.ToString();
        }
    }

    /// <summary>Refresh dependent display strings after Bytes mutation.</summary>
    public void RefreshDisplay()
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HexDisplay)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AsciiDisplay)));
    }
}

/// <summary>One byte-level patch operation — used by the undo/redo stack.</summary>
public class HexEdit
{
    public long Offset { get; set; }
    public byte[] OldBytes { get; set; } = Array.Empty<byte>();
    public byte[] NewBytes { get; set; } = Array.Empty<byte>();
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Description { get; set; } = "";
}

/// <summary>One hit from search-in-file.</summary>
public class HexSearchResult
{
    public long Offset { get; set; }
    public int Length { get; set; }
    public string OffsetHex => $"0x{Offset:X8}";
    public string Preview { get; set; } = "";
}

/// <summary>Inspector value at the current selected byte / range.</summary>
public class HexInspector : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private long _offset;
    public long Offset
    {
        get => _offset;
        set { _offset = value; Raise(nameof(Offset)); Raise(nameof(OffsetHex)); }
    }
    public string OffsetHex => $"0x{_offset:X8} ({_offset})";

    private string _int8 = "—";
    private string _uint8 = "—";
    private string _int16Le = "—";
    private string _uint16Le = "—";
    private string _int32Le = "—";
    private string _uint32Le = "—";
    private string _int64Le = "—";
    private string _float32 = "—";
    private string _float64 = "—";
    private string _utf8 = "—";
    private string _utf16 = "—";

    public string Int8 { get => _int8; set { _int8 = value; Raise(nameof(Int8)); } }
    public string UInt8 { get => _uint8; set { _uint8 = value; Raise(nameof(UInt8)); } }
    public string Int16Le { get => _int16Le; set { _int16Le = value; Raise(nameof(Int16Le)); } }
    public string UInt16Le { get => _uint16Le; set { _uint16Le = value; Raise(nameof(UInt16Le)); } }
    public string Int32Le { get => _int32Le; set { _int32Le = value; Raise(nameof(Int32Le)); } }
    public string UInt32Le { get => _uint32Le; set { _uint32Le = value; Raise(nameof(UInt32Le)); } }
    public string Int64Le { get => _int64Le; set { _int64Le = value; Raise(nameof(Int64Le)); } }
    public string Float32 { get => _float32; set { _float32 = value; Raise(nameof(Float32)); } }
    public string Float64 { get => _float64; set { _float64 = value; Raise(nameof(Float64)); } }
    public string Utf8 { get => _utf8; set { _utf8 = value; Raise(nameof(Utf8)); } }
    public string Utf16Le { get => _utf16; set { _utf16 = value; Raise(nameof(Utf16Le)); } }

    private void Raise(string p) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));

    /// <summary>Decode every interpretation of the bytes at the given window.</summary>
    public void Update(byte[] data, long offset)
    {
        Offset = offset;
        if (data.Length == 0)
        {
            Int8 = UInt8 = Int16Le = UInt16Le = Int32Le = UInt32Le = Int64Le = Float32 = Float64 = Utf8 = Utf16Le = "—";
            return;
        }
        byte b0 = data[0];
        Int8 = ((sbyte)b0).ToString();
        UInt8 = b0.ToString();
        if (data.Length >= 2)
        {
            Int16Le = BitConverter.ToInt16(data, 0).ToString();
            UInt16Le = BitConverter.ToUInt16(data, 0).ToString();
        }
        else { Int16Le = UInt16Le = "—"; }
        if (data.Length >= 4)
        {
            Int32Le = BitConverter.ToInt32(data, 0).ToString();
            UInt32Le = BitConverter.ToUInt32(data, 0).ToString();
            Float32 = BitConverter.ToSingle(data, 0).ToString("G7");
        }
        else { Int32Le = UInt32Le = Float32 = "—"; }
        if (data.Length >= 8)
        {
            Int64Le = BitConverter.ToInt64(data, 0).ToString();
            Float64 = BitConverter.ToDouble(data, 0).ToString("G15");
        }
        else { Int64Le = Float64 = "—"; }
        try
        {
            // Stop at first NUL for printable preview
            int nul = Array.IndexOf(data, (byte)0);
            int len = nul < 0 ? Math.Min(data.Length, 32) : Math.Min(nul, 32);
            Utf8 = len > 0 ? Encoding.UTF8.GetString(data, 0, len) : "—";
        }
        catch { Utf8 = "—"; }
        try
        {
            int len = Math.Min(data.Length, 64) & ~1; // even bytes
            Utf16Le = len > 0 ? Encoding.Unicode.GetString(data, 0, len) : "—";
        }
        catch { Utf16Le = "—"; }
    }
}

/// <summary>Detected magic / metadata about an open file.</summary>
public class HexFileInfo
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string MagicHex { get; set; } = "";
    public string DetectedType { get; set; } = "Unknown";

    public string SizeDisplay
    {
        get
        {
            if (Size < 1024) return $"{Size} B";
            if (Size < 1024 * 1024) return $"{Size / 1024.0:F1} KB";
            if (Size < 1024L * 1024 * 1024) return $"{Size / 1024.0 / 1024.0:F2} MB";
            return $"{Size / 1024.0 / 1024.0 / 1024.0:F2} GB";
        }
    }

    /// <summary>Compute SHA-256 + magic detection from the first ~16 bytes.</summary>
    public static HexFileInfo From(string path, byte[] sample, long fullSize)
    {
        var info = new HexFileInfo { Path = path, Size = fullSize };
        if (sample.Length > 0)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(sample.Length, 16); i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(sample[i].ToString("X2"));
            }
            info.MagicHex = sb.ToString();
            info.DetectedType = DetectMagic(sample);
        }
        return info;
    }

    /// <summary>Compute SHA-256 of file contents (caller passes full data).</summary>
    public void ComputeSha256(Stream stream)
    {
        try
        {
            stream.Position = 0;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            Sha256 = Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch { Sha256 = ""; }
    }

    private static string DetectMagic(byte[] b)
    {
        if (b.Length >= 4)
        {
            if (b[0] == 0x4D && b[1] == 0x5A) return "PE (Windows executable)";
            if (b[0] == 0x7F && b[1] == 0x45 && b[2] == 0x4C && b[3] == 0x46) return "ELF (Linux executable)";
            if (b[0] == 0xCA && b[1] == 0xFE && b[2] == 0xBA && b[3] == 0xBE) return "Java class / Mach-O fat";
            if (b[0] == 0x50 && b[1] == 0x4B && b[2] == 0x03 && b[3] == 0x04) return "ZIP (or DOCX/JAR/XLSX)";
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "PNG image";
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "JPEG image";
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "GIF image";
            if (b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46) return "PDF";
            if (b[0] == 0x1F && b[1] == 0x8B) return "GZIP";
            if (b[0] == 0x42 && b[1] == 0x5A && b[2] == 0x68) return "BZip2";
            if (b[0] == 0xFD && b[1] == 0x37 && b[2] == 0x7A) return "XZ";
            if (b[0] == 0x37 && b[1] == 0x7A && b[2] == 0xBC) return "7-Zip";
            if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46) return "RIFF (WAV/AVI)";
            if (b[0] == 0x66 && b[1] == 0x4C && b[2] == 0x61 && b[3] == 0x43) return "FLAC audio";
            if (b[0] == 0x49 && b[1] == 0x44 && b[2] == 0x33) return "MP3 (ID3 tag)";
            if (b[0] == 0x47 && b[1] == 0x47 && b[2] == 0x55 && b[3] == 0x46) return "GGUF (LLM weights)";
            if (b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return "UTF-8 text (BOM)";
            if (b[0] == 0xFF && b[1] == 0xFE) return "UTF-16 LE text (BOM)";
            if (b[0] == 0xFE && b[1] == 0xFF) return "UTF-16 BE text (BOM)";
        }
        // ASCII-ish check
        bool printable = true;
        int sampleLen = Math.Min(b.Length, 256);
        for (int i = 0; i < sampleLen; i++)
        {
            byte c = b[i];
            if (c == 0) { printable = false; break; }
            if ((c < 0x20 || c > 0x7E) && c != 0x09 && c != 0x0A && c != 0x0D)
            {
                printable = false; break;
            }
        }
        return printable ? "Text (ASCII / UTF-8)" : "Binary (Unknown)";
    }
}
