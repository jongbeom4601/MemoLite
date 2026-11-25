using System.Text.Json;

namespace MemoLite.Core;

/// <summary>A memo kept in the organized library. Text is stored with "\n" line endings.</summary>
public sealed record LibraryMemo(string Id, string Title, string Text, string Category, string? SourcePath, DateTime UpdatedUtc);

public sealed record LibrarySnapshot(List<LibraryMemo> Memos, int Version = 1);

/// <summary>An open tab offered to the library. LibraryId links a tab to the memo it was organized into.</summary>
public sealed record MemoDraft(string? LibraryId, string Title, string Text, string? SourcePath);

public sealed record OrganizeResult(IReadOnlyList<string> AssignedIds, IReadOnlyList<KeyValuePair<string, int>> Counts);

/// <summary>Offline keyword rules that sort a memo into one topic.</summary>
public static class MemoClassifier
{
    public const string Other = "기타";

    // Earlier rules win ties, so specific intents come before broad topics.
    private static readonly (string Category, string[] Keywords)[] Rules =
    [
        ("할 일", ["할 일", "할일", "todo", "to-do", "해야", "체크리스트", "[ ]", "[x]", "☐", "☑", "마감", "제출", "장보기", "구매", "사야"]),
        ("일정", ["일정", "약속", "예약", "스케줄", "캘린더", "월요일", "화요일", "수요일", "목요일", "금요일", "토요일", "일요일", "오전", "오후", "내일", "모레", "다음 주", "시까지"]),
        ("업무", ["회의", "미팅", "보고", "업무", "프로젝트", "고객", "거래처", "팀장", "결재", "매출", "출장", "협업", "기획서", "발표"]),
        ("공부", ["공부", "강의", "수업", "과제", "시험", "복습", "예습", "개념", "정의", "공식", "교수", "학습", "중간고사", "기말고사", "레포트"]),
        ("아이디어", ["아이디어", "구상", "영감", "컨셉", "브레인스토밍", "만들어 보", "해 보면", "하면 좋을", "떠오른", "기획"]),
    ];

    public static IReadOnlyList<string> Categories { get; } = [.. Rules.Select(rule => rule.Category), Other];

    public static string Classify(string title, string text)
    {
        string best = Other;
        int bestScore = 0;
        foreach (var (category, keywords) in Rules)
        {
            int score = 0;
            // A keyword in the title says more about the topic than one in the body.
            foreach (string keyword in keywords) score += 2 * Count(title, keyword) + Count(text, keyword);
            if (score > bestScore) { best = category; bestScore = score; }
        }
        return best;
    }

    public static int Order(string category)
    {
        for (int i = 0; i < Categories.Count; i++)
            if (Categories[i] == category) return i;
        return int.MaxValue;
    }

    private static int Count(string source, string word)
    {
        int count = 0;
        for (int at = source.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = source.IndexOf(word, at + word.Length, StringComparison.OrdinalIgnoreCase))
            count++;
        return count;
    }
}

/// <summary>Reads and updates the shared library file. Updates are serialized across windows by a lock file.</summary>
public static class MemoLibrary
{
    public const string FileName = "library.json";
    public const int MaxTitleLength = 80;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<List<LibraryMemo>> LoadAsync(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return [];
        // Share the writers' lock so an open read never blocks another window's atomic replace.
        using var gate = await AcquireAsync(path + ".lock");
        return File.Exists(path) ? Read(await File.ReadAllBytesAsync(path)) : [];
    }

    /// <summary>
    /// Reads the latest library, applies <paramref name="change"/>, and writes it back atomically.
    /// A damaged library throws before anything is written, so it is never overwritten.
    /// </summary>
    public static async Task<T> UpdateAsync<T>(string directory, Func<List<LibraryMemo>, T> change)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        using var gate = await AcquireAsync(path + ".lock");
        var memos = File.Exists(path) ? Read(await File.ReadAllBytesAsync(path)) : [];
        T result = change(memos);
        await WriteAsync(path, memos);
        return result;
    }

    /// <summary>Classifies each non-empty draft and inserts or replaces its memo. Returns one id per draft, empty for skipped drafts.</summary>
    public static OrganizeResult Organize(List<LibraryMemo> memos, IReadOnlyList<MemoDraft> drafts, DateTime nowUtc)
    {
        var counts = new Dictionary<string, int>();
        var texts = drafts.Select(draft => TextFiles.Normalize(draft.Text)).ToArray();
        var targets = new int[drafts.Count];
        var claimed = new HashSet<int>();
        // Resolve explicit links before path matches, so two tabs can never claim one memo in a pass.
        for (int i = 0; i < drafts.Count; i++)
        {
            targets[i] = -1;
            if (string.IsNullOrWhiteSpace(texts[i]) || drafts[i].LibraryId is not { } id) continue;
            int index = memos.FindIndex(memo => memo.Id == id);
            if (index >= 0 && claimed.Add(index)) targets[i] = index;
        }
        for (int i = 0; i < drafts.Count; i++)
        {
            if (targets[i] >= 0 || string.IsNullOrWhiteSpace(texts[i]) || drafts[i].SourcePath is not { } path) continue;
            for (int index = 0; index < memos.Count; index++)
                if (!claimed.Contains(index) && DocumentState.SamePath(memos[index].SourcePath, path))
                { claimed.Add(index); targets[i] = index; break; }
        }

        var ids = new List<string>(drafts.Count);
        for (int i = 0; i < drafts.Count; i++)
        {
            string text = texts[i];
            if (string.IsNullOrWhiteSpace(text)) { ids.Add(""); continue; }
            int index = targets[i];
            var existing = index >= 0 ? memos[index] : null;
            string title = Title(drafts[i].Title);
            string category = MemoClassifier.Classify(title, text);
            // A fallback copy opened while its file was missing has no path; keep the file association.
            var memo = new LibraryMemo(existing?.Id ?? Guid.NewGuid().ToString("N"), title, text, category,
                drafts[i].SourcePath ?? existing?.SourcePath, nowUtc);
            if (existing is not null) memos[index] = memo; else memos.Add(memo);
            ids.Add(memo.Id);
            counts[category] = counts.GetValueOrDefault(category) + 1;
        }
        var ordered = counts.OrderBy(pair => MemoClassifier.Order(pair.Key)).ThenBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        return new OrganizeResult(ids, ordered);
    }

    public static bool Remove(List<LibraryMemo> memos, string id) => memos.RemoveAll(memo => memo.Id == id) > 0;

    private static string Title(string title)
    {
        title = title.Trim();
        if (title.Length == 0) return DocumentState.UntitledName;
        if (title.Length <= MaxTitleLength) return title;
        int end = MaxTitleLength;
        if (char.IsHighSurrogate(title[end - 1])) end--;
        return title[..end] + "…";
    }

    private static List<LibraryMemo> Read(byte[] data)
    {
        LibrarySnapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<LibrarySnapshot>(data, JsonOptions); }
        catch (JsonException error) { throw new InvalidDataException("정리된 메모 기록을 읽을 수 없습니다. 원본 기록은 그대로 보관했습니다.", error); }
        if (snapshot is null || snapshot.Version != 1 || snapshot.Memos is null ||
            snapshot.Memos.Any(memo => memo is null || string.IsNullOrWhiteSpace(memo.Id) || memo.Title is null ||
                memo.Text is null || string.IsNullOrWhiteSpace(memo.Category) ||
                (memo.SourcePath is not null && !Path.IsPathFullyQualified(memo.SourcePath))))
            throw new InvalidDataException("정리된 메모 기록을 읽을 수 없습니다. 원본 기록은 그대로 보관했습니다.");
        return snapshot.Memos;
    }

    private static async Task WriteAsync(string path, List<LibraryMemo> memos)
    {
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(new LibrarySnapshot(memos), JsonOptions);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<FileStream> AcquireAsync(string lockPath)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 50) { await Task.Delay(100); }
        }
    }
}

public sealed record SearchMatch(bool InTitle, int TextIndex, int Length, string Snippet);

/// <summary>Case-insensitive search where every whitespace-separated term must appear in the title or text.</summary>
public static class MemoSearch
{
    public const int SnippetWidth = 60;

    public static string[] Terms(string? query) =>
        (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static SearchMatch? Match(string title, string text, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return null;
        bool inTitle = false;
        int first = -1, length = 0;
        foreach (string term in terms)
        {
            bool titleHit = title.Contains(term, StringComparison.OrdinalIgnoreCase);
            int at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (!titleHit && at < 0) return null;
            inTitle |= titleHit;
            if (at >= 0 && (first < 0 || at < first)) { first = at; length = term.Length; }
        }
        return new SearchMatch(inTitle, first, length, Snippet(text, Math.Max(0, first)));
    }

    /// <summary>A one-line excerpt around <paramref name="index"/> with ellipses where text was cut.</summary>
    public static string Snippet(string text, int index, int width = SnippetWidth)
    {
        index = Math.Clamp(index, 0, text.Length);
        int start = Math.Max(0, index - width / 3);
        int end = Math.Min(text.Length, start + width);
        if (start > 0 && char.IsLowSurrogate(text[start])) start--;
        if (end < text.Length && char.IsLowSurrogate(text[end])) end++;
        string body = string.Join(' ', text[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return (start > 0 ? "…" : "") + body + (end < text.Length ? "…" : "");
    }

    /// <summary>The body under the title line, for list previews.</summary>
    public static string Preview(string title, string text)
    {
        text = TextFiles.Normalize(text).TrimStart();
        int end = text.IndexOf('\n');
        string firstLine = (end < 0 ? text : text[..end]).Trim();
        if (firstLine == title.Trim()) text = end < 0 ? "" : text[(end + 1)..];
        return Snippet(text, 0);
    }
}
