using System.Security.Cryptography;
using System.Text;

namespace MemoLite.Core;

public enum TextEncodingKind { Utf8, Utf8Bom, Utf16Le, Utf16Be, Cp949 }

public sealed record TextFile(string Text, TextEncodingKind Encoding, string Newline, string Digest);

public static class TextFiles
{
    static TextFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    public static int NormalizedOffset(string text, int offset) => Normalize(text[..Math.Clamp(offset, 0, text.Length)]).Length;

    public static int OriginalOffset(string text, int normalizedOffset)
    {
        int original = 0, normalized = 0;
        while (original < text.Length && normalized < normalizedOffset)
        {
            if (text[original] == '\r' && original + 1 < text.Length && text[original + 1] == '\n') original++;
            original++; normalized++;
        }
        return original;
    }
    public static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    public static Encoding GetEncoding(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 => new UTF8Encoding(false, true),
        TextEncodingKind.Utf8Bom => new UTF8Encoding(true, true),
        TextEncodingKind.Utf16Le => new UnicodeEncoding(false, true, true),
        TextEncodingKind.Utf16Be => new UnicodeEncoding(true, true, true),
        TextEncodingKind.Cp949 => Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static TextFile Decode(byte[] data)
    {
        TextEncodingKind kind;
        int offset;
        if (data.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) (kind, offset) = (TextEncodingKind.Utf8Bom, 3);
        else if (data.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) (kind, offset) = (TextEncodingKind.Utf16Le, 2);
        else if (data.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) (kind, offset) = (TextEncodingKind.Utf16Be, 2);
        else
        {
            offset = 0;
            try { GetEncoding(TextEncodingKind.Utf8).GetString(data); kind = TextEncodingKind.Utf8; }
            catch (DecoderFallbackException) { kind = TextEncodingKind.Cp949; }
        }
        string text = GetEncoding(kind).GetString(data, offset, data.Length - offset);
        if (text.Contains('\0')) throw new InvalidDataException("텍스트 파일이 아닙니다. NUL 문자가 포함되어 있습니다.");
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n') lf++;
        }
        string newline = lf > crlf && lf >= cr ? "\n" : cr > crlf && cr > lf ? "\r" : "\r\n";
        return new TextFile(Normalize(text), kind, newline, Digest(data));
    }

    public static byte[] Encode(string text, TextEncodingKind kind, string newline)
    {
        if (newline is not ("\r\n" or "\n" or "\r")) throw new ArgumentException("지원하지 않는 줄바꿈 형식입니다.", nameof(newline));
        var encoding = GetEncoding(kind);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(Normalize(text).Replace("\n", newline))];
    }

    public static async Task<TextFile> ReadAsync(string path) => Decode(await File.ReadAllBytesAsync(path));
    public static async Task<string> FingerprintAsync(string path) => Digest(await File.ReadAllBytesAsync(path));

    public static async Task<string> WriteAsync(string path, string text, TextEncodingKind kind, string newline)
    {
        byte[] data = Encode(text, kind, newline);
        path = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".memolite-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(data);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Digest(data);
    }

    public static string EncodingLabel(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 => "UTF-8", TextEncodingKind.Utf8Bom => "UTF-8 BOM",
        TextEncodingKind.Utf16Le => "UTF-16 LE", TextEncodingKind.Utf16Be => "UTF-16 BE",
        TextEncodingKind.Cp949 => "ANSI (CP949)", _ => kind.ToString()
    };

    public static string NewlineLabel(string newline) => newline switch
    { "\n" => "Unix (LF)", "\r" => "Mac (CR)", _ => "Windows (CRLF)" };
}
