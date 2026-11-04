using System.Text;
using MemoLite.Core;

int passed = 0, failed = 0;
string directory = Path.Combine(Path.GetTempPath(), $"MemoLite-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
string path = Path.Combine(directory, "한글 메모.txt");
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Test(string name, Func<Task> run)
{
    try { await run(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error}"); }
}
Task Sync(Action run) { run(); return Task.CompletedTask; }

try
{
    foreach (var kind in Enum.GetValues<TextEncodingKind>())
    foreach (string newline in new[] { "\r\n", "\n", "\r" })
    {
        await Test($"round trip: {kind}, {TextFiles.NewlineLabel(newline)}", async () =>
        {
            var original = TextFiles.Encode("회의 메모\n둘째 줄\n", kind, newline);
            await File.WriteAllBytesAsync(path, original);
            var loaded = await TextFiles.ReadAsync(path);
            Check(loaded.Text == "회의 메모\n둘째 줄\n", "Text changed");
            Check(loaded.Encoding == kind && loaded.Newline == newline, "Format changed");
            string digest = await TextFiles.WriteAsync(path, loaded.Text, loaded.Encoding, loaded.Newline);
            byte[] saved = await File.ReadAllBytesAsync(path);
            Check(original.SequenceEqual(saved), "Bytes changed");
            Check(digest == loaded.Digest, "Fingerprint changed");
        });
    }
    await Test("Unicode and trailing newline", async () =>
    {
        await TextFiles.WriteAsync(path, "한글 😀\n", TextEncodingKind.Utf8, "\r\n");
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(Encoding.UTF8.GetBytes("한글 😀\r\n")), "Unicode lost");
    });
    await Test("Empty file and no final newline", async () =>
    {
        foreach (string value in new[] { "", "마지막 줄" })
        {
            await TextFiles.WriteAsync(path, value, TextEncodingKind.Utf8, "\r\n");
            Check((await TextFiles.ReadAsync(path)).Text == value, "Unexpected newline");
        }
    });
    await Test("Encoding error preserves original", async () =>
    {
        await File.WriteAllTextAsync(path, "original");
        try { await TextFiles.WriteAsync(path, "😀", TextEncodingKind.Cp949, "\r\n"); throw new Exception("Expected encoding failure"); }
        catch (EncoderFallbackException) { }
        Check(await File.ReadAllTextAsync(path) == "original", "Original was changed");
    });
    await Test("Locked destination preserves original and removes temporary file", async () =>
    {
        await File.WriteAllTextAsync(path, "original");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await TextFiles.WriteAsync(path, "replacement", TextEncodingKind.Utf8, "\r\n"); throw new Exception("Expected save failure"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Check(await File.ReadAllTextAsync(path) == "original", "Original was changed");
        Check(!Directory.GetFiles(directory, ".memolite-*.tmp").Any(), "Temporary file leaked");
    });
    await Test("Binary content rejected", () => Sync(() =>
    {
        try { TextFiles.Decode([65, 0, 66]); throw new Exception("Binary was accepted"); }
        catch (InvalidDataException) { }
    }));
    await Test("Predominant newline normalized", () => Sync(() =>
    {
        var file = TextFiles.Decode(Encoding.UTF8.GetBytes("a\r\nb\nc\nd"));
        Check(file.Newline == "\n" && file.Text == "a\nb\nc\nd", "Wrong predominant newline");
    }));
    await Test("Saved snapshot keeps later edits dirty", () => Sync(() =>
    {
        var doc = new DocumentState("new");
        doc.Text = "before";
        string snapshot = doc.Text;
        doc.Text = "after";
        doc.MarkSaved(path, snapshot, "digest", TextEncodingKind.Utf8);
        Check(doc.IsDirty && doc.SavedText == "before", "Later edit was marked saved");
        doc.Text = "before";
        Check(!doc.IsDirty, "Undo to saved content still dirty");
    }));
    await Test("Independent document states", () => Sync(() =>
    {
        var first = new DocumentState("first") { Text = "first" };
        var second = new DocumentState("second");
        Check(first.IsDirty && !second.IsDirty && second.Text == "", "Document states leaked");
    }));
    await Test("Load retains file metadata", () => Sync(() =>
    {
        var doc = new DocumentState("new");
        doc.Load(path, new TextFile("내용", TextEncodingKind.Utf16Be, "\n", "hash"));
        Check(!doc.IsDirty && doc.Text == "내용" && doc.Newline == "\n" && doc.Digest == "hash", "Load metadata lost");
    }));
    await Test("Untitled display title follows the first line and survives recovery", () => Sync(() =>
    {
        var doc = new DocumentState(DocumentState.UntitledName);
        foreach (string separator in new[] { "\n", "\r\n", "\r" })
        {
            doc.Text = "  첫 줄 제목 😀  " + separator + "둘째 줄";
            Check(doc.DisplayName == "첫 줄 제목 😀", "First-line title was not extracted");
            Check(DocumentState.Restore(doc.Snapshot()).DisplayName == doc.DisplayName, "Recovered title changed");
        }
        doc.Text = "바뀐 제목";
        Check(doc.DisplayName == "바뀐 제목", "Edited first line did not update the title");
        foreach (string text in new[] { "", "  \t", "\n둘째 줄", " \r\n둘째 줄" })
        {
            doc.Text = text;
            Check(doc.DisplayName == DocumentState.UntitledName, "Blank first line did not use the default title");
        }
    }));
    await Test("Saved tabs keep their file name when the first line changes", () => Sync(() =>
    {
        var doc = new DocumentState(DocumentState.UntitledName) { Text = "첫 줄" };
        doc.MarkSaved(path, doc.Text, "digest", TextEncodingKind.Utf8);
        doc.Text = "바뀐 첫 줄";
        Check(doc.DisplayName == Path.GetFileName(path), "Edited file title replaced the file name");
        Check(DocumentState.Restore(doc.Snapshot()).DisplayName == doc.DisplayName, "Recovered file name changed");
    }));
    await Test("Paths are compared case-insensitively", () => Sync(() =>
    {
        Check(DocumentState.SamePath(path, path.ToUpperInvariant()), "Case mismatch");
        Check(!DocumentState.SamePath(null, path), "Untitled document has a path");
    }));
    await Test("Cursor handles Korean, emoji and Windows newlines", () => Sync(() =>
    {
        Check(DocumentState.Position("한글😀\r\n다음", 4) == (1, 4), "Emoji counted twice");
        Check(DocumentState.Position("한글😀\r\n다음", 7) == (2, 2), "CRLF position wrong");
    }));
    await Test("External edits change fingerprint", async () =>
    {
        await File.WriteAllTextAsync(path, "first");
        string first = await TextFiles.FingerprintAsync(path);
        await File.WriteAllTextAsync(path, "second");
        Check(first != await TextFiles.FingerprintAsync(path), "Edit not detected");
    });
    await SessionChecks.RunAsync(directory, Test, Check);
}
finally { Directory.Delete(directory, recursive: true); }
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
