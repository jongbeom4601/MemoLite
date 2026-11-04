#if DEBUG
using MemoLite.Core;
using Microsoft.UI.Xaml;

namespace MemoLite;

public sealed partial class MainWindow
{
    private async Task RunSmokeTestsAsync(string reportPath)
    {
        var results = new List<string>();
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, $"smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            results.Add($"PASS {name}");
        }
        try
        {
            await Task.Delay(200);
            var first = Active!;
            first.Editor.Text = "첫째 문서\r\n두 번째 줄";
            await Task.Delay(80);
            Check(first.State.IsDirty && first.State.Text == "첫째 문서\n두 번째 줄",
                $"Korean text and modified state (dirty={first.State.IsDirty}, editor={System.Text.Json.JsonSerializer.Serialize(first.Editor.Text)}, state={System.Text.Json.JsonSerializer.Serialize(first.State.Text)})");
            string path = Path.Combine(directory, "한글 문서.txt");
            Check(await WriteDocumentAsync(first, path), "Save file");
            Check(!first.State.IsDirty && (await TextFiles.ReadAsync(path)).Text == first.State.Text, "Save content round trip");
            first.Editor.Select(first.Editor.Text.Length, 0);
            first.Editor.SelectedText = " 수정";
            await Task.Delay(80);
            Check(first.Editor.CanUndo, "Native undo available");
            first.Editor.Undo();
            await Task.Delay(80);
            Check(!first.State.IsDirty && first.Editor.CanRedo, "Undo to saved content");
            first.Editor.Redo();
            await Task.Delay(80);
            Check(first.State.IsDirty && first.State.Text.EndsWith(" 수정"), "Native redo");
            first.Editor.Undo();
            await Task.Delay(80);
            var second = NewDocument();
            Check(second.Editor.Text == "" && !second.State.IsDirty && first.Editor.Text.Length > 0, "Independent tabs");
            var third = NewDocument();
            Check(second.State.Name == "제목 없음" && third.State.Name == "제목 없음" && second.Tab != third.Tab,
                "New tabs share an unnumbered title and remain separate documents");
            RemoveDocument(third); Tabs.SelectedItem = second.Tab;
            await Task.Delay(80);
            Root.UpdateLayout();
            double cleanWidth = second.Tab.ActualWidth;
            string longTitle = string.Concat(Enumerable.Repeat("아주 긴 첫 줄 제목 😀 ", 12));
            second.Editor.Text = longTitle + "\r\n제목에 포함하지 않는 둘째 줄";
            await Task.Delay(80);
            Root.UpdateLayout();
            Check(second.Caption.Text == longTitle.Trim() && second.ModifiedIndicator.Opacity == 1,
                "Unsaved tab uses its first line and shows a separate modified marker");
            Check(Math.Abs(cleanWidth - 134) < 0.5 && Math.Abs(second.Tab.ActualWidth - cleanWidth) < 0.5 &&
                Math.Abs(first.Tab.ActualWidth - cleanWidth) < 0.5,
                $"Clean, modified and long-title tabs keep the same fixed width (clean={cleanWidth}, modified={second.Tab.ActualWidth}, saved={first.Tab.ActualWidth})");
            Check(second.Caption.IsTextTrimmed, "Long tab title is visually ellipsized");
            Check(second.CloseButton is not null && ReferenceEquals(second.CloseButton.Content, second.ModifiedIndicator) &&
                second.ModifiedIndicator.Parent is not null && second.ModifiedIndicator.Fill is Microsoft.UI.Xaml.Media.SolidColorBrush,
                "Modified marker occupies the native close button instead of the title");
            SetTabPointerOver(second, true);
            Root.UpdateLayout();
            Check(Equals(second.CloseButton!.Content, TabCloseGlyph) && second.ModifiedIndicator.Opacity == 0 &&
                Math.Abs(second.Tab.ActualWidth - cleanWidth) < 0.5,
                "Hover reveals the close glyph without resizing the tab");
            SetTabPointerOver(second, false);
            Check(ReferenceEquals(second.CloseButton.Content, second.ModifiedIndicator), "Leaving a modified tab restores its marker");
            second.Editor.Text = "";
            await Task.Delay(80);
            Root.UpdateLayout();
            Check(second.Caption.Text == DocumentState.UntitledName && second.ModifiedIndicator.Opacity == 0 &&
                Math.Abs(second.Tab.ActualWidth - cleanWidth) < 0.5,
                "Clearing the draft restores its title and hides the marker without resizing");
            second.Editor.Text = "선택 영역";
            second.Editor.Select(0, 2);
            Refresh();
            Check(PositionStatus.Text.Contains("2자 선택"), "Selection status");
            second.Editor.Text = "";
            SetWrap(false);
            Check(documents.Values.All(d => d.Editor.TextWrapping == TextWrapping.NoWrap), "Word wrap off across tabs");
            ChangeZoom(20);
            Check(zoom == 120 && documents.Values.All(d => Math.Abs(d.Editor.FontSize - 19.2) < 0.01), "Zoom across tabs");
            SetWrap(true); zoom = 100; ChangeZoom(0);
            StatusBar.Visibility = Visibility.Collapsed;
            Check(StatusBar.Visibility == Visibility.Collapsed, "Status bar hide");
            StatusBar.Visibility = Visibility.Visible;
            var reopened = await OpenFileAsync(path);
            Check(reopened == first && Active == first && documents.Count == 2, "Duplicate file selects existing tab");
            Check(await CloseDocumentAsync(second), "Close clean tab");
            for (int i = 0; i < 20; i++) NewDocument();
            await Task.Delay(300);
            Root.UpdateLayout();
            var scroller = TabScroller();
            Check(scroller is not null && scroller.ScrollableWidth > 0, "Scrollable native tab strip");
            scroller!.ChangeView(0, null, null, true);
            await Task.Delay(100);
            var selected = Active;
            scroller.ChangeView(Math.Min(80, scroller.ScrollableWidth), null, null, true);
            await Task.Delay(100);
            Check(scroller.HorizontalOffset > 0 && Active == selected, "Horizontal tab scroll retains active document");
            results.Add($"SUCCESS {results.Count} UI checks");
        }
        catch (Exception error) { results.Add($"FAIL {error}"); }
        finally
        {
            await File.WriteAllLinesAsync(reportPath, results);
            Directory.Delete(directory, true);
            allowClose = true;
            Close();
        }
    }
}
#endif
