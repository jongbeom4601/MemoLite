using MemoLite.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MemoLite;

/// <summary>The sidebar library, the 정리 button, and title bar search.</summary>
public sealed partial class MainWindow
{
    private readonly string libraryDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemoLite", "library");
    private List<LibraryMemo> libraryMemos = [];
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private Func<Task>? firstSearchResult;

    private void InitializeLibrary()
    {
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RenderSidebar(); };
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); SidebarNotice.Visibility = Visibility.Collapsed; };
        TitleBarSearchBox.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
        TitleBarSearchBox.KeyDown += OnSearchKeyDown;
        OrganizeButton.Click += async (_, _) => await RunAsync(OrganizeAsync);
        Tabs.TabItemsChanged += (_, _) => ScheduleSearchRefresh();
        Root.ActualThemeChanged += (_, _) => RenderSidebar();
        Closed += (_, _) => { searchTimer.Stop(); noticeTimer.Stop(); };
        RenderSidebar();
    }

    private bool searchActive;

    /// <summary>Re-runs an active search after tab text, titles, or the tab set change.</summary>
    private void ScheduleSearchRefresh()
    {
        if (!searchActive) return;
        searchTimer.Stop();
        searchTimer.Start();
    }

    private IEnumerable<EditorDocument> OrderedDocuments() =>
        Tabs.TabItems.OfType<TabViewItem>().Select(tab => documents.GetValueOrDefault(tab)).OfType<EditorDocument>();

    private async Task LoadLibraryAsync()
    {
        try { libraryMemos = await MemoLibrary.LoadAsync(libraryDirectory); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            libraryMemos = [];
            ShowNotice("정리된 메모 기록을 읽지 못했어요. 기록 파일은 그대로 두었어요.");
        }
        RenderSidebar();
    }

    private void FocusSearch()
    {
        TitleBarSearchBox.Focus(FocusState.Keyboard);
        TitleBarSearchBox.SelectAll();
    }

    private async void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            TitleBarSearchBox.Text = "";
            Active?.Editor.Focus(FocusState.Keyboard);
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (searchTimer.IsEnabled) { searchTimer.Stop(); RenderSidebar(); }
            if (firstSearchResult is { } open) await RunAsync(open);
        }
    }

    private async Task OrganizeAsync()
    {
        var targets = OrderedDocuments().Where(doc => !string.IsNullOrWhiteSpace(doc.State.Text)).ToList();
        if (targets.Count == 0)
        {
            ShowNotice("정리할 내용이 있는 탭이 없어요.");
            return;
        }
        var drafts = targets.Select(doc => new MemoDraft(doc.State.LibraryId, doc.State.DisplayName, doc.State.Text, doc.State.Path)).ToList();
        var (result, memos) = await MemoLibrary.UpdateAsync(libraryDirectory, memos =>
            (MemoLibrary.Organize(memos, drafts, DateTime.UtcNow), memos.ToList()));
        for (int i = 0; i < targets.Count; i++)
        {
            if (result.AssignedIds[i].Length == 0) continue;
            targets[i].State.MarkKeptInLibrary(result.AssignedIds[i], drafts[i].Text);
            UpdateHeader(targets[i]);
        }
        libraryMemos = memos;
        ShowNotice($"탭 {targets.Count}개를 정리했어요 · " + string.Join(", ", result.Counts.Select(pair => $"{pair.Key} {pair.Value}")));
        RenderSidebar();
        ScheduleSessionSave();
    }

    private async Task OpenLibraryMemoAsync(LibraryMemo memo, string[]? terms = null)
    {
        var doc = documents.Values.FirstOrDefault(open => open.State.LibraryId == memo.Id);
        string? path = memo.SourcePath;
        bool useFile = doc is null && path is not null && File.Exists(path);
        if (useFile && terms is { Length: > 0 })
        {
            // A search hit came from the archived text. Open the file only if it still contains the hit.
            try
            {
                var file = await TextFiles.ReadAsync(path!);
                useFile = MemoSearch.Match(Path.GetFileName(path!), TextFiles.Normalize(file.Text), terms) is not null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { useFile = false; }
        }
        if (useFile)
        {
            // A memo organized from a file opens the file itself, so it shows the latest saved text.
            doc = await OpenFileAsync(path!);
            if (doc is not null) doc.State.LibraryId = memo.Id;
        }
        else if (doc is null)
        {
            var blank = ReplaceableBlank();
            doc = NewDocument(restoredState: DocumentState.FromLibrary(memo.Id, memo.Text));
            if (blank is not null) RemoveDocument(blank);
        }
        if (doc is not null) ShowDocument(doc, terms);
        ScheduleSessionSave();
    }

    private void ShowDocument(EditorDocument doc, string[]? terms)
    {
        Tabs.SelectedItem = doc.Tab;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Active != doc || dialogOpen) return;
            if (terms is { Length: > 0 } && MemoSearch.Match(doc.State.DisplayName, doc.State.Text, terms) is { TextIndex: >= 0 } match)
            {
                string raw = doc.Editor.Text;
                int start = TextFiles.OriginalOffset(raw, match.TextIndex);
                int end = TextFiles.OriginalOffset(raw, match.TextIndex + match.Length);
                doc.Editor.Select(start, end - start);
            }
            doc.Editor.Focus(FocusState.Programmatic);
        });
    }

    private async Task RemoveLibraryMemoAsync(LibraryMemo memo)
    {
        if (await ConfirmAsync("정리된 메모에서 제거", $"'{memo.Title}'을(를) 정리된 메모에서 제거할까요?\n열려 있는 탭과 원본 파일은 그대로 남습니다.", "제거")
            != ContentDialogResult.Primary) return;
        libraryMemos = await MemoLibrary.UpdateAsync(libraryDirectory, memos => { MemoLibrary.Remove(memos, memo.Id); return memos.ToList(); });
        foreach (var doc in documents.Values.Where(doc => doc.State.LibraryId == memo.Id))
        {
            doc.State.ForgetLibrary();
            UpdateHeader(doc);
        }
        RenderSidebar();
        ScheduleSessionSave();
    }

    private void ShowNotice(string message)
    {
        SidebarNotice.Text = message;
        SidebarNotice.Visibility = Visibility.Visible;
        noticeTimer.Stop();
        noticeTimer.Start();
    }

    private void RenderSidebar()
    {
        SidebarList.Children.Clear();
        firstSearchResult = null;
        string[] terms = MemoSearch.Terms(TitleBarSearchBox.Text);
        searchActive = terms.Length > 0;
        if (searchActive) RenderSearch(terms);
        else { RenderLibrary(); firstSearchResult = null; }
    }

    private void RenderLibrary()
    {
        SidebarTitle.Text = "정리된 메모";
        SidebarIcon.Symbol = Symbol.AllApps;
        SidebarCount.Text = libraryMemos.Count > 0 ? $"{libraryMemos.Count:N0}개" : "";
        if (libraryMemos.Count == 0)
        {
            ShowEmpty("정리한 메모가\n여기에 모여요", "탭에 메모를 쓰고 정리 버튼을 누르면\n주제별로 모아 드려요.");
            return;
        }
        foreach (var group in libraryMemos.GroupBy(memo => memo.Category)
                     .OrderBy(group => MemoClassifier.Order(group.Key)).ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            AddSection(group.Key, group.Count());
            foreach (var memo in group.OrderByDescending(memo => memo.UpdatedUtc))
                AddItem(memo.Title, MemoSearch.Preview(memo.Title, memo.Text), memo.UpdatedUtc.ToLocalTime().ToString("M월 d일"),
                    () => OpenLibraryMemoAsync(memo), LibraryMenu(memo));
        }
        ShowList();
    }

    private void RenderSearch(string[] terms)
    {
        SidebarTitle.Text = "검색 결과";
        SidebarIcon.Symbol = Symbol.Find;
        var tabHits = OrderedDocuments()
            .Select(doc => (Doc: doc, Match: MemoSearch.Match(doc.State.DisplayName, doc.State.Text, terms)))
            .Where(hit => hit.Match is not null).OrderByDescending(hit => hit.Match!.InTitle).ToList();
        // A memo already open in a tab (linked, or its file reopened) is listed once, under 열린 탭.
        var openIds = documents.Values.Select(doc => doc.State.LibraryId).OfType<string>().ToHashSet();
        var openPaths = documents.Values.Select(doc => doc.State.Path).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var memoHits = libraryMemos
            .Where(memo => !openIds.Contains(memo.Id) && (memo.SourcePath is null || !openPaths.Contains(memo.SourcePath)))
            .Select(memo => (Memo: memo, Match: MemoSearch.Match(memo.Title, memo.Text, terms)))
            .Where(hit => hit.Match is not null)
            .OrderByDescending(hit => hit.Match!.InTitle).ThenByDescending(hit => hit.Memo.UpdatedUtc).ToList();
        int total = tabHits.Count + memoHits.Count;
        SidebarCount.Text = total > 0 ? $"{total:N0}개" : "";
        if (total == 0)
        {
            ShowEmpty("검색 결과가 없어요", $"'{string.Join(' ', terms)}'와 일치하는\n탭이나 정리된 메모가 없어요.");
            return;
        }
        void AddTabHits()
        {
            if (tabHits.Count == 0) return;
            AddSection("열린 탭", tabHits.Count);
            foreach (var (doc, match) in tabHits)
                AddItem(doc.State.DisplayName, match!.Snippet, "", () => { ShowDocument(doc, terms); return Task.CompletedTask; });
        }
        void AddMemoHits()
        {
            if (memoHits.Count == 0) return;
            AddSection("정리된 메모", memoHits.Count);
            foreach (var (memo, match) in memoHits)
                AddItem(memo.Title, match!.Snippet, memo.Category, () => OpenLibraryMemoAsync(memo, terms), LibraryMenu(memo));
        }
        // Title hits rank first across both groups; the first item added is the Enter target.
        bool memosFirst = memoHits.Any(hit => hit.Match!.InTitle) && !tabHits.Any(hit => hit.Match!.InTitle);
        if (memosFirst) { AddMemoHits(); AddTabHits(); }
        else { AddTabHits(); AddMemoHits(); }
        ShowList();
    }

    private MenuFlyout LibraryMenu(LibraryMemo memo)
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "열기", Icon = new SymbolIcon(Symbol.OpenFile) };
        open.Click += async (_, _) => await RunAsync(() => OpenLibraryMemoAsync(memo));
        var remove = new MenuFlyoutItem { Text = "정리된 메모에서 제거", Icon = new SymbolIcon(Symbol.Delete) };
        remove.Click += async (_, _) => await RunAsync(() => RemoveLibraryMemoAsync(memo));
        menu.Items.Add(open);
        menu.Items.Add(remove);
        return menu;
    }

    private void ShowEmpty(string title, string description)
    {
        SidebarEmptyTitle.Text = title;
        SidebarEmptyDescription.Text = description;
        SidebarEmptyState.Visibility = Visibility.Visible;
        SidebarScroller.Visibility = Visibility.Collapsed;
    }

    private void ShowList()
    {
        SidebarEmptyState.Visibility = Visibility.Collapsed;
        SidebarScroller.Visibility = Visibility.Visible;
    }

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    private void AddSection(string name, int count)
    {
        var header = new TextBlock
        {
            Text = $"{name} · {count:N0}", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("ShellSecondaryTextBrush"),
            Margin = new Thickness(12, SidebarList.Children.Count == 0 ? 4 : 16, 12, 4)
        };
        AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        SidebarList.Children.Add(header);
    }

    private void AddItem(string title, string detail, string meta, Func<Task> open, MenuFlyout? menu = null)
    {
        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
        });
        if (meta.Length > 0)
        {
            var metaText = new TextBlock { Text = meta, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeBrush("ShellSecondaryTextBrush") };
            Grid.SetColumn(metaText, 1);
            heading.Children.Add(metaText);
        }
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(heading);
        if (detail.Length > 0)
            content.Children.Add(new TextBlock
            {
                Text = detail, FontSize = 12, Foreground = ThemeBrush("ShellSecondaryTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
            });
        var button = new Button
        {
            Content = content, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            ContextFlyout = menu
        };
        AutomationProperties.SetName(button, string.IsNullOrEmpty(detail) ? title : $"{title}, {detail}");
        ToolTipService.SetToolTip(button, title);
        button.Click += async (_, _) => await RunAsync(open);
        firstSearchResult ??= open;
        SidebarList.Children.Add(button);
    }
}
