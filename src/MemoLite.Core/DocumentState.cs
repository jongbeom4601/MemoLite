using System.Text;

namespace MemoLite.Core;

public sealed class DocumentState(string name)
{
    public const string UntitledName = "제목 없음";
    public string Name { get; private set; } = name;
    public string? Path { get; private set; }
    public string Text { get; set; } = "";
    public string SavedText { get; private set; } = "";
    public TextEncodingKind Encoding { get; set; } = TextEncodingKind.Utf8;
    public string Newline { get; private set; } = "\r\n";
    public string? Digest { get; private set; }
    /// <summary>The organized memo this tab updates when 정리 runs again.</summary>
    public string? LibraryId { get; set; }
    public bool IsDirty => !string.Equals(Text, SavedText, StringComparison.Ordinal);
    public string DisplayName
    {
        get
        {
            if (Path is not null) return Name;
            int end = Text.AsSpan().IndexOfAny('\r', '\n');
            var firstLine = (end < 0 ? Text.AsSpan() : Text.AsSpan(0, end)).Trim();
            return firstLine.IsEmpty ? UntitledName : firstLine.ToString();
        }
    }

    public void Load(string path, TextFile file)
    {
        Path = System.IO.Path.GetFullPath(path);
        Name = System.IO.Path.GetFileName(Path);
        Text = SavedText = file.Text;
        Encoding = file.Encoding;
        Newline = file.Newline;
        Digest = file.Digest;
    }

    public void MarkSaved(string path, string savedText, string digest, TextEncodingKind encoding)
    {
        Path = System.IO.Path.GetFullPath(path);
        Name = System.IO.Path.GetFileName(Path);
        SavedText = savedText;
        Digest = digest;
        Encoding = encoding;
    }

    /// <summary>
    /// An untitled draft kept in the library no longer needs a save prompt until it changes again.
    /// <paramref name="keptText"/> is the exact text written, so edits made during the write stay modified.
    /// </summary>
    public void MarkKeptInLibrary(string libraryId, string keptText)
    {
        LibraryId = libraryId;
        if (Path is null) SavedText = TextFiles.Normalize(keptText);
    }

    /// <summary>Unlinks the library memo. An untitled draft returns to the unsaved baseline so closing prompts again.</summary>
    public void ForgetLibrary()
    {
        LibraryId = null;
        if (Path is null) SavedText = "";
    }

    public static DocumentState FromLibrary(string libraryId, string text)
    {
        string normalized = TextFiles.Normalize(text);
        return new(UntitledName) { Text = normalized, SavedText = normalized, LibraryId = libraryId };
    }

    public SessionDocument Snapshot(int selectionStart = 0, int selectionLength = 0) =>
        new(Name, Path, Text, SavedText, Encoding, Newline, Digest, selectionStart, selectionLength, LibraryId);

    public static DocumentState Restore(SessionDocument saved) => new(saved.Path is null ? UntitledName : saved.Name)
    {
        Path = saved.Path,
        Text = TextFiles.Normalize(saved.Text),
        SavedText = TextFiles.Normalize(saved.SavedText),
        Encoding = saved.Encoding,
        Newline = saved.Newline,
        Digest = saved.Digest,
        LibraryId = saved.LibraryId
    };

    public static bool SamePath(string? first, string? second) => first is not null && second is not null &&
        string.Equals(System.IO.Path.GetFullPath(first), System.IO.Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    public static (int Line, int Column) Position(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        int line = 1, lineStart = 0;
        for (int i = 0; i < caret; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < caret && text[i + 1] == '\n') i++;
                line++; lineStart = i + 1;
            }
            else if (text[i] == '\n') { line++; lineStart = i + 1; }
        }
        return (line, text[lineStart..caret].EnumerateRunes().Count() + 1);
    }
}
