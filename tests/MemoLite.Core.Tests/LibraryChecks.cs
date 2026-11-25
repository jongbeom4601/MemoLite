using MemoLite.Core;

internal static class LibraryChecks
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        string Folder() => Path.Combine(root, "library-" + Guid.NewGuid().ToString("N"));
        var now = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc);
        Task Sync(Action run) { run(); return Task.CompletedTask; }

        await test("Classifier picks topics from Korean keywords", () => Sync(() =>
        {
            check(MemoClassifier.Classify("주간 회의", "프로젝트 보고 정리") == "업무", "Work memo");
            check(MemoClassifier.Classify("자료구조", "강의 복습, 시험 범위 확인") == "공부", "Study memo");
            check(MemoClassifier.Classify("오늘", "[ ] 우유 구매\n[x] 과제 제출") == "할 일", "Todo memo");
            check(MemoClassifier.Classify("토요일", "오후 3시 약속") == "일정", "Schedule memo");
            check(MemoClassifier.Classify("앱 아이디어", "떠오른 컨셉") == "아이디어", "Idea memo");
            check(MemoClassifier.Classify("제목 없음", "그냥 적어 둔 글") == MemoClassifier.Other, "Fallback");
        }));
        await test("Classifier weighs the title above the body", () => Sync(() =>
            check(MemoClassifier.Classify("시험 공부", "회의") == "공부", "Title keyword lost")));

        await test("Organize inserts, then updates the same memo by id and by path", () => Sync(() =>
        {
            var memos = new List<LibraryMemo>();
            var first = MemoLibrary.Organize(memos, [new(null, "회의 메모", "회의 메모\n프로젝트 일정", null),
                new(null, "빈 탭", "  \n", null), new(null, "강의.txt", "강의 정리", @"C:\notes\강의.txt")], now);
            check(memos.Count == 2 && first.AssignedIds[1] == "" && first.AssignedIds[0] != "", "Insert or skip failed");
            check(first.Counts.Sum(pair => pair.Value) == 2, "Counts wrong");
            MemoLibrary.Organize(memos, [new(first.AssignedIds[0], "회의 메모", "회의 메모\n바뀐 내용", null),
                new(null, "강의.txt", "강의 정리 2", @"C:\NOTES\강의.txt")], now.AddHours(1));
            check(memos.Count == 2, "Re-organizing duplicated memos");
            check(memos.Single(memo => memo.Id == first.AssignedIds[0]).Text.EndsWith("바뀐 내용"), "Id update lost");
            check(memos.Single(memo => memo.Id == first.AssignedIds[2]).Text == "강의 정리 2", "Path update lost");
        }));
        await test("Library round trips through disk and removes memos", async () =>
        {
            string folder = Folder();
            check((await MemoLibrary.LoadAsync(folder)).Count == 0, "Missing library is not empty");
            var result = await MemoLibrary.UpdateAsync(folder, memos =>
                MemoLibrary.Organize(memos, [new(null, "한글 😀", "한글 😀\r\n둘째 줄", null)], now));
            var loaded = await MemoLibrary.LoadAsync(folder);
            check(loaded.Count == 1 && loaded[0].Text == "한글 😀\n둘째 줄" && loaded[0].Title == "한글 😀", "Round trip changed memo");
            check(await MemoLibrary.UpdateAsync(folder, memos => MemoLibrary.Remove(memos, result.AssignedIds[0])), "Remove failed");
            check((await MemoLibrary.LoadAsync(folder)).Count == 0, "Removed memo came back");
        });
        await test("Damaged library is reported and never overwritten", async () =>
        {
            string folder = Folder();
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, MemoLibrary.FileName);
            await File.WriteAllTextAsync(path, "{ broken");
            try { await MemoLibrary.UpdateAsync(folder, memos => memos.Count); throw new Exception("Expected failure"); }
            catch (InvalidDataException) { }
            check(await File.ReadAllTextAsync(path) == "{ broken", "Damaged library was overwritten");
        });
        await test("Session keeps the library link of a tab", async () =>
        {
            string folder = Folder();
            var state = DocumentState.FromLibrary("memo-1", "보관된 메모\r\n본문");
            check(!state.IsDirty && state.Text == "보관된 메모\n본문", "Library memo opened as modified");
            using (var session = await SessionFile.OpenAsync(folder))
                await session.SaveAsync(new SessionSnapshot([state.Snapshot()]));
            using var reopened = await SessionFile.OpenAsync(folder);
            check(DocumentState.Restore(reopened.Restored!.Documents[0]).LibraryId == "memo-1", "Library link lost");
        });
        await test("Kept untitled draft clears its modified mark; files stay modified", () => Sync(() =>
        {
            var draft = new DocumentState(DocumentState.UntitledName) { Text = "초안" };
            draft.MarkKeptInLibrary("memo-1", "초안");
            check(!draft.IsDirty && draft.LibraryId == "memo-1", "Draft still modified");
            var file = new DocumentState("a.txt");
            file.MarkSaved(Path.Combine(root, "a.txt"), "원본", "digest", TextEncodingKind.Utf8);
            file.Text = "수정";
            file.MarkKeptInLibrary("memo-2", "수정");
            check(file.IsDirty, "File edits were marked saved without writing the file");
        }));
        await test("Edits typed while organizing stay modified", () => Sync(() =>
        {
            var draft = new DocumentState(DocumentState.UntitledName) { Text = "초안 그리고 새 글" };
            draft.MarkKeptInLibrary("memo-1", "초안");
            check(draft.IsDirty, "Text newer than the library copy was marked saved");
        }));
        await test("Removing a draft from the library restores its save prompt", () => Sync(() =>
        {
            var draft = DocumentState.FromLibrary("memo-1", "보관된 초안");
            draft.ForgetLibrary();
            check(draft.IsDirty && draft.LibraryId is null, "Unlinked draft can close without a prompt");
        }));
        await test("Two tabs never claim one memo in a pass", () => Sync(() =>
        {
            string a = Path.Combine(root, "a.txt"), b = Path.Combine(root, "b.txt");
            var memos = new List<LibraryMemo> { new("memo-x", "a.txt", "원래 내용", "기타", a, now) };
            // The tab saved as b.txt keeps its link; a reopened a.txt comes first in tab order.
            var result = MemoLibrary.Organize(memos, [new(null, "a.txt", "다시 연 a", a), new("memo-x", "b.txt", "b 내용", b)], now);
            check(memos.Count == 2 && result.AssignedIds.Distinct().Count() == 2, "Both tabs share a memo");
            check(result.AssignedIds[1] == "memo-x" && memos.Single(memo => memo.Id == "memo-x").SourcePath == b, "Linked tab lost its memo");
        }));
        await test("Re-organizing a fallback copy keeps the source file", () => Sync(() =>
        {
            string a = Path.Combine(root, "a.txt");
            var memos = new List<LibraryMemo> { new("memo-x", "a.txt", "원래 내용", "기타", a, now) };
            MemoLibrary.Organize(memos, [new("memo-x", "a.txt", "파일 없이 고친 내용", null)], now);
            check(memos.Single().SourcePath == a, "Source path was dropped");
        }));
        await test("Loading waits for a writer's lock", async () =>
        {
            string folder = Folder();
            await MemoLibrary.UpdateAsync(folder, memos => MemoLibrary.Organize(memos, [new(null, "메모", "내용", null)], now));
            Task<List<LibraryMemo>> load;
            using (new FileStream(Path.Combine(folder, MemoLibrary.FileName + ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                load = MemoLibrary.LoadAsync(folder);
                await Task.Delay(250);
                check(!load.IsCompleted, "Read did not wait for the lock");
            }
            check((await load).Count == 1, "Read after lock release failed");
        });

        await test("Search needs every term and prefers title hits", () => Sync(() =>
        {
            var terms = MemoSearch.Terms("  회의   결과 ");
            check(terms.Length == 2, "Terms not split");
            check(MemoSearch.Match("주간 회의", "결과 공유", terms) is { InTitle: true }, "Title + body match failed");
            check(MemoSearch.Match("주간 회의", "다른 내용", terms) is null, "Partial match accepted");
            const string body = "first line\nMemoLite 검색 TEST";
            var hit = MemoSearch.Match("메모", body, MemoSearch.Terms("test"))!;
            check(!hit.InTitle && hit.TextIndex == body.IndexOf("TEST") && hit.Length == 4, "Case-insensitive body index wrong");
            check(MemoSearch.Match("a", "b", MemoSearch.Terms("   ")) is null, "Blank query matched");
        }));
        await test("Snippets stay on one line and keep surrogate pairs", () => Sync(() =>
        {
            string text = new string('가', 40) + "\n😀😀 찾는 말 " + new string('나', 80);
            string snippet = MemoSearch.Snippet(text, text.IndexOf("찾는"));
            check(snippet.StartsWith('…') && snippet.EndsWith('…') && !snippet.Contains('\n'), "Snippet shape");
            check(!snippet.Any(char.IsSurrogate) || snippet.Contains("😀"), "Broken surrogate pair");
            check(MemoSearch.Preview("회의 메모", "회의 메모\n본문 내용") == "본문 내용", "Preview repeated the title");
        }));
    }
}
