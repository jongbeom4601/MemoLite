#if DEBUG
using MemoLite.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace MemoLite;

public sealed partial class MainWindow
{
    private Func<ContentDialog, ContentDialogResult>? confirmForTest;

    private async Task RunSessionTestsAsync(string mode, string reportPath)
    {
        var results = File.Exists(reportPath) ? (await File.ReadAllLinesAsync(reportPath)).ToList() : [];
        string data = reportPath + ".data";
        string original = Path.Combine(data, "원본.txt");
        const string draftText = "닫아도 남는 메모 😀\n다음 줄";
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            results.Add("PASS " + name);
        }
        try
        {
            await InitializeSessionAsync(Path.Combine(data, "sessions"));
            if (mode == "--session-write-test")
            {
                var first = Active!;
                first.Editor.Text = "원본\r\n";
                await Task.Delay(60);
                Check(await WriteDocumentAsync(first, original), "Session fixture file saved");
                first.Editor.Select(first.Editor.Text.Length, 0);
                first.Editor.SelectedText = "저장하지 않은 수정";
                var draft = NewDocument();
                draft.Editor.Text = draftText;
                await Task.Delay(60);
                int confirmations = 0;
                confirmForTest = dialog => { confirmations++; return ContentDialogResult.None; };
                bool closed = await CloseDocumentAsync(draft);
                Check(!closed && documents.ContainsKey(draft.Tab) && confirmations == 1,
                    $"Closing a modified tab still asks; cancel retains the tab (closed={closed}, prompts={confirmations}, dirty={draft.State.IsDirty})");
                var discarded = NewDocument();
                discarded.Editor.Text = "버리는 메모";
                await Task.Delay(60);
                confirmForTest = _ => ContentDialogResult.Secondary;
                Check(await CloseDocumentAsync(discarded) && !documents.ContainsKey(discarded.Tab), "Discard closes only the requested tab");
                Tabs.TabItems.Remove(draft.Tab);
                Tabs.TabItems.Insert(0, draft.Tab);
                Tabs.SelectedItem = draft.Tab;
                draft.Editor.Select(TextFiles.OriginalOffset(draft.Editor.Text, 2), 3);
                SetWrap(false); ChangeZoom(20);
                statusItem.IsChecked = false; StatusBar.Visibility = Visibility.Collapsed;
                // Any old window-close confirmation would fail this process/timeout.
                confirmForTest = _ => throw new InvalidOperationException("Window close asked for a file save");
                Check(first.State.IsDirty && draft.State.IsDirty, "Both drafts are unsaved before application exit");
                results.Add("PASS Requesting real window close with unsaved tabs");
                await File.WriteAllLinesAsync(reportPath, results);
                await RequestCloseAsync(); // Same production checkpoint as the title-bar X and Exit menu.
                return;
            }

            var restored = Tabs.TabItems.OfType<TabViewItem>().Select(tab => documents[tab]).ToList();
            Check(restored.Count == 2, "Restart restores exactly the retained tabs");
            Check(restored[0].State.Text == draftText && restored[0].State.Path is null && restored[0].State.IsDirty,
                "Restart restores an untitled Unicode draft");
            Check(restored[1].State.Text == "원본\n저장하지 않은 수정" && restored[1].State.SavedText == "원본\n" && restored[1].State.IsDirty,
                "Restart restores edited file content and saved baseline");
            Check((await TextFiles.ReadAsync(original)).Text == "원본\n", "Application close did not overwrite the original file");
            Check(Tabs.SelectedIndex == 0 && Active == restored[0], "Restart restores tab order and active tab");
            var snapshot = CaptureSession();
            Check(snapshot.Documents[0].SelectionStart == 2 && snapshot.Documents[0].SelectionLength == 3, "Restart restores selection");
            Check(zoom == 120 && !wordWrap && StatusBar.Visibility == Visibility.Collapsed, "Restart restores zoom, wrap and status settings");
            Check(restored[0].State.Name == DocumentState.UntitledName && restored[1].State.Name == "원본.txt",
                "Restart restores unnumbered untitled names and keeps saved file names");
            Check(restored[0].Caption.Text == "닫아도 남는 메모 😀" && restored[1].Caption.Text == "원본.txt",
                "Restart derives the draft title from its first line and retains saved file titles");
            int prompts = 0;
            confirmForTest = _ => { prompts++; return ContentDialogResult.None; };
            Check(!await CloseDocumentAsync(restored[0]) && prompts == 1, "Recovered draft still prompts when its tab is removed");
            confirmForTest = _ => ContentDialogResult.Secondary;
            Check(await CloseDocumentAsync(restored[0]), "Recovered draft can be explicitly discarded");
            var disk = JsonSerializer.Deserialize<SessionSnapshot>(await File.ReadAllBytesAsync(sessionFile!.Path));
            Check(disk!.Documents.Count == 1 && disk.Documents[0].Path == original, "Discard is committed to recovery before tab removal");
            results.Add($"SUCCESS {results.Count} session lifecycle checks");
        }
        catch (Exception error) { results.Add("FAIL " + error); }
        await File.WriteAllLinesAsync(reportPath, results);
        allowClose = true;
        Close();
    }
}
#endif
