using System.Text.Json;
using MemoLite.Core;

internal static class SessionChecks
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        string Folder() => Path.Combine(root, "sessions-" + Guid.NewGuid().ToString("N"));
        SessionSnapshot Draft(string text) => new([new DocumentState("제목 없음 1") { Text = text }.Snapshot()]);

        await test("Session restores untitled Unicode draft, selection and view settings", async () =>
        {
            string folder = Folder();
            string text = "저장하지 않은 메모 😀\n다음 줄";
            using (var session = await SessionFile.OpenAsync(folder))
                await session.SaveAsync(new SessionSnapshot([new DocumentState("제목 없음 7") { Text = text }.Snapshot(3, 4)], 0, 8, false, 130, false));
            using var reopened = await SessionFile.OpenAsync(folder);
            var saved = reopened.Restored!;
            var restored = DocumentState.Restore(saved.Documents[0]);
            check(restored.Text == text && restored.IsDirty && restored.Path is null, "Draft was lost or marked saved");
            check(restored.Name == DocumentState.UntitledName, "Legacy numbered draft name was not updated");
            check(saved.Documents[0].SelectionStart == 3 && saved.Documents[0].SelectionLength == 4, "Selection lost");
            check(saved.NextUntitledNumber == 8 && saved.Zoom == 130 && !saved.WordWrap && !saved.ShowStatusBar, "View state lost");
        });
        await test("Session never writes an edited buffer into its original file", async () =>
        {
            string folder = Folder();
            Directory.CreateDirectory(folder);
            string source = Path.Combine(folder, "원본.txt");
            string hash = await TextFiles.WriteAsync(source, "원본\n", TextEncodingKind.Cp949, "\r\n");
            var doc = new DocumentState("new");
            doc.Load(source, await TextFiles.ReadAsync(source));
            doc.Text = "저장하지 않은 수정 😀\n";
            using (var session = await SessionFile.OpenAsync(Path.Combine(folder, "recovery")))
                await session.SaveAsync(new([doc.Snapshot()]));
            using var reopened = await SessionFile.OpenAsync(Path.Combine(folder, "recovery"));
            var restored = DocumentState.Restore(reopened.Restored!.Documents[0]);
            check(restored.IsDirty && restored.Text == doc.Text && restored.SavedText == "원본\n", "Saved baseline lost");
            check(restored.Encoding == TextEncodingKind.Cp949 && restored.Newline == "\r\n" && restored.Digest == hash, "File metadata lost");
            check(restored.Name == "원본.txt", "Saved file name was changed");
            check((await TextFiles.ReadAsync(source)).Text == "원본\n", "Original was overwritten");
            await File.WriteAllTextAsync(source, "외부 변경");
            check(await TextFiles.FingerprintAsync(source) != restored.Digest, "External modification no longer detectable");
        });
        await test("Session preserves clean versus modified states", async () =>
        {
            string folder = Folder();
            var clean = new DocumentState("saved");
            clean.Load(Path.Combine(root, "saved.txt"), new TextFile("저장됨", TextEncodingKind.Utf8, "\n", "hash"));
            var dirty = new DocumentState("draft") { Text = "미저장" };
            using (var session = await SessionFile.OpenAsync(folder))
                await session.SaveAsync(new([clean.Snapshot(), dirty.Snapshot()], 1));
            using var reopened = await SessionFile.OpenAsync(folder);
            check(!DocumentState.Restore(reopened.Restored!.Documents[0]).IsDirty, "Clean file became dirty");
            check(DocumentState.Restore(reopened.Restored.Documents[1]).IsDirty && reopened.Restored.SelectedIndex == 1, "Dirty or active tab lost");
        });
        await test("Concurrent live windows use separate session files", async () =>
        {
            string folder = Folder();
            using (var first = await SessionFile.OpenAsync(folder))
            {
                await first.SaveAsync(Draft("first"));
                using var second = await SessionFile.OpenAsync(folder);
                check(second.Path != first.Path && second.Restored is null, "An active window's tabs were stolen");
                await second.SaveAsync(Draft("second"));
            }
            using var restoredFirst = await SessionFile.OpenAsync(folder);
            using var restoredSecond = await SessionFile.OpenAsync(folder);
            check(new HashSet<string> { restoredFirst.Restored!.Documents[0].Text, restoredSecond.Restored!.Documents[0].Text }.SetEquals(["first", "second"]), "One window overwrote another");
        });
        await test("Corrupted recovery data is preserved without overwrite", async () =>
        {
            string folder = Folder(); Directory.CreateDirectory(folder);
            string damaged = Path.Combine(folder, "damaged.json");
            await File.WriteAllTextAsync(damaged, "{truncated");
            using var session = await SessionFile.OpenAsync(folder);
            check(session.Restored is null && session.UnreadableFiles.Contains(damaged), "Corruption was not detected");
            await session.SaveAsync(Draft("new"));
            check(await File.ReadAllTextAsync(damaged) == "{truncated", "Damaged recovery file was destroyed");
        });
        await test("Future recovery versions are preserved", async () =>
        {
            string folder = Folder(); Directory.CreateDirectory(folder);
            string future = Path.Combine(folder, "future.json");
            string original = JsonSerializer.Serialize(Draft("future") with { Version = 2 });
            await File.WriteAllTextAsync(future, original);
            using var session = await SessionFile.OpenAsync(folder);
            await session.SaveAsync(Draft("current"));
            check(session.Path != future && await File.ReadAllTextAsync(future) == original, "Newer session format was overwritten");
        });
        await test("Failed checkpoint retains the preceding recovery file", async () =>
        {
            string folder = Folder();
            using var session = await SessionFile.OpenAsync(folder);
            await session.SaveAsync(Draft("recoverable"));
            using (var locked = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { await session.SaveAsync(Draft("replacement")); throw new Exception("Expected checkpoint failure"); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            var preserved = JsonSerializer.Deserialize<SessionSnapshot>(await File.ReadAllBytesAsync(session.Path));
            check(preserved!.Documents[0].Text == "recoverable", "Previous recovery was damaged");
            check(!Directory.GetFiles(folder, "*.tmp").Any(), "Checkpoint temporary file leaked");
        });
        await test("Serial checkpoints keep the latest snapshot", async () =>
        {
            string folder = Folder();
            using var session = await SessionFile.OpenAsync(folder);
            var first = session.SaveAsync(Draft("first"));
            var second = session.SaveAsync(Draft("latest"));
            await Task.WhenAll(first, second);
            var saved = JsonSerializer.Deserialize<SessionSnapshot>(await File.ReadAllBytesAsync(session.Path));
            check(saved!.Documents[0].Text == "latest", "An older write overwrote the latest snapshot");
        });
        await test("Closed tabs stay out of the next recovered session", async () =>
        {
            string folder = Folder();
            using (var session = await SessionFile.OpenAsync(folder))
            {
                await session.SaveAsync(new([new DocumentState("keep") { Text = "keep" }.Snapshot(), new DocumentState("discard") { Text = "discard" }.Snapshot()]));
                await session.SaveAsync(Draft("keep"));
            }
            using var reopened = await SessionFile.OpenAsync(folder);
            check(reopened.Restored!.Documents.Count == 1 && reopened.Restored.Documents[0].Text == "keep", "Discarded draft returned");
        });
        await test("Selection offsets round trip across CRLF and emoji", () =>
        {
            string raw = "한글\r\n😀 다음\r\n끝";
            foreach (int position in new[] { 0, 2, 4, 6, 9, raw.Length })
                check(TextFiles.OriginalOffset(raw, TextFiles.NormalizedOffset(raw, position)) == position, "Selection offset changed");
            return Task.CompletedTask;
        });
        await test("Invalid snapshots cannot replace valid recovery data", async () =>
        {
            using var session = await SessionFile.OpenAsync(Folder());
            await session.SaveAsync(Draft("valid"));
            try { await session.SaveAsync(Draft("invalid") with { Zoom = -1 }); throw new Exception("Invalid snapshot accepted"); }
            catch (InvalidDataException) { }
            check(JsonSerializer.Deserialize<SessionSnapshot>(await File.ReadAllBytesAsync(session.Path))!.Documents[0].Text == "valid", "Invalid data replaced recovery");
        });
    }
}
