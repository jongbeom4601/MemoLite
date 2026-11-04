using MemoLite.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoLite;

public sealed partial class MainWindow
{
    private SessionFile? sessionFile;
    private bool restoringSession;
    private readonly DispatcherTimer sessionTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };

    private async Task InitializeSessionAsync(string? directory = null)
    {
        restoringSession = true;
        Tabs.IsEnabled = false;
        try
        {
            directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemoLite", "sessions");
            sessionFile = await SessionFile.OpenAsync(directory);
            if (sessionFile.Restored is { } saved) RestoreSession(saved);
            if (sessionFile.UnreadableFiles.Count > 0)
                await ShowMessageAsync("탭 복원 기록 확인", "읽을 수 없는 이전 탭 기록이 있습니다. 해당 기록은 삭제하거나 덮어쓰지 않고 그대로 보관했습니다.\n\n" + string.Join("\n", sessionFile.UnreadableFiles));
        }
        finally
        {
            restoringSession = false;
            Tabs.IsEnabled = true;
        }
    }

    private void RestoreSession(SessionSnapshot saved)
    {
        foreach (var doc in documents.Values.ToArray()) RemoveDocument(doc);
        wordWrap = saved.WordWrap;
        zoom = saved.Zoom;
        wrapItem.IsChecked = wordWrap;
        statusItem.IsChecked = saved.ShowStatusBar;
        StatusBar.Visibility = saved.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
        foreach (var tab in saved.Documents)
        {
            var doc = NewDocument(restoredState: DocumentState.Restore(tab));
            int start = TextFiles.OriginalOffset(doc.Editor.Text, tab.SelectionStart);
            int end = TextFiles.OriginalOffset(doc.Editor.Text, tab.SelectionStart + Math.Max(0, tab.SelectionLength));
            doc.Editor.Select(start, Math.Max(0, end - start));
        }
        if (documents.Count == 0) NewDocument();
        Tabs.SelectedIndex = Math.Clamp(saved.SelectedIndex, 0, Tabs.TabItems.Count - 1);
        Refresh();
    }

    private SessionSnapshot CaptureSession(EditorDocument? excluded = null)
    {
        using var timing = MeasureOperation("Session.capture");
        var ordered = Tabs.TabItems.OfType<TabViewItem>().Select(tab => documents[tab]).Where(doc => doc != excluded).ToList();
        var saved = new List<SessionDocument>();
        foreach (var doc in ordered)
        {
            string raw = doc.Editor.Text;
            doc.State.Text = TextFiles.Normalize(raw);
            int start = TextFiles.NormalizedOffset(raw, doc.Editor.SelectionStart);
            int end = TextFiles.NormalizedOffset(raw, doc.Editor.SelectionStart + doc.Editor.SelectionLength);
            saved.Add(doc.State.Snapshot(start, end - start));
        }
        int selected = ordered.FindIndex(doc => doc == Active);
        if (selected < 0) selected = Math.Clamp(Tabs.SelectedIndex, 0, Math.Max(0, ordered.Count - 1));
        return new SessionSnapshot(saved, selected, WordWrap: wordWrap, Zoom: zoom, ShowStatusBar: statusItem.IsChecked);
    }

    private Task PersistSessionAsync(EditorDocument? excluded = null) =>
        (sessionFile ?? throw new InvalidOperationException("탭 임시 보관 폴더를 준비하지 못했습니다.")).SaveAsync(CaptureSession(excluded));

    private void ScheduleSessionSave()
    {
        if (sessionFile is null || restoringSession || closePending || allowClose) return;
        sessionTimer.Stop();
        sessionTimer.Start();
    }

    private async void OnSessionTimer(object? sender, object e)
    {
        sessionTimer.Stop();
        if (busy || dialogOpen || closePending)
        {
            ScheduleSessionSave();
            return;
        }
        try { await PersistSessionAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep the live text. Closing performs a final write and reports any
            // continuing failure instead of silently discarding the document.
        }
    }
}
