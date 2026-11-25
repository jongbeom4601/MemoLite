using System.Text;
using MemoLite.Core;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace MemoLite;

public sealed partial class MainWindow : Window
{
    private const double DocumentTabWidth = 134;
    private const string TabCloseGlyph = "\uE711";
    private sealed record EditorDocument(DocumentState State, TextBox Editor, Grid Surface, TabViewItem Tab,
        TextBlock Caption, Ellipse ModifiedIndicator)
    {
        public Button? CloseButton { get; set; }
        public bool PointerOver { get; set; }
    }
    private readonly Dictionary<TabViewItem, EditorDocument> documents = [];
    private readonly MenuBar menu = new() { Height = 32, MinHeight = 32, Padding = new Thickness(8, 0, 8, 0) };
    private Grid? menuOwner;
    private readonly Dictionary<string, MenuFlyoutItem> editItems = [];
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private ToggleMenuFlyoutItem wrapItem = null!;
    private ToggleMenuFlyoutItem statusItem = null!;
    private int zoom = 100;
    private bool wordWrap = true;
    private bool busy;
    private bool dialogOpen;
    private bool allowClose;
    private bool closePending;
    private EditorDocument? Active => Tabs.SelectedItem is TabViewItem tab ? documents.GetValueOrDefault(tab) : null;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 800;
            presenter.PreferredMinimumHeight = 500;
        }
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        BuildMenus();
        AddShortcuts();
        InitializeLibrary();
        statusTimer.Tick += (_, _) => { statusTimer.Stop(); Refresh(); };
        sessionTimer.Tick += OnSessionTimer;
        Tabs.TabItemsChanged += (_, _) => ScheduleSessionSave();
        Tabs.Loaded += OnTabsLoaded;
        Tabs.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnTabWheel), true);
        Root.PreviewKeyDown += OnEditorKeys;
        WorkspaceGrid.SizeChanged += (_, _) => AlignTitleBarSearch();
        TitleBarContentHost.SizeChanged += (_, _) => AlignTitleBarSearch();
        TitleBarContentHost.Loaded += (_, _) => AlignTitleBarSearch();
        Root.Loaded += OnLoaded;
        AppWindow.Closing += OnWindowClosing;
        Closed += (_, _) => { statusTimer.Stop(); sessionTimer.Stop(); sessionFile?.Dispose(); };
        NewDocument();
    }

    private void AlignTitleBarSearch()
    {
        if (!TitleBarContentHost.IsLoaded || WorkspaceGrid.ActualWidth <= 0) return;

        double gap = WorkspaceGrid.ColumnSpacing;
        int columns = WorkspaceGrid.ColumnDefinitions.Count;
        double columnWidth = (WorkspaceGrid.ActualWidth - (columns - 1) * gap) / columns;
        double searchLeft = WorkspaceGrid.Margin.Left + 2 * (columnWidth + gap);
        double contentLeft = TitleBarContentHost.TransformToVisual(Root).TransformPoint(new Point()).X;
        double inset = Math.Max(0, searchLeft - contentLeft);
        double available = TitleBarContentHost.ActualWidth - inset - WorkspaceGrid.Margin.Right;
        double searchWidth = 7 * columnWidth + 6 * gap;

        TitleBarSearchHost.Margin = new Thickness(inset, 0, WorkspaceGrid.Margin.Right, 0);
        TitleBarSearchHost.Width = Math.Max(0, Math.Min(searchWidth, available));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnLoaded;
        Active?.Editor.Focus(FocusState.Programmatic);
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
#if DEBUG
        if (args.Length == 2 && args[0] == "--trace-tab-input")
        {
            await StartTabInputTraceAsync(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--profile-tabs")
        {
            await RunTabProfileAsync(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--smoke-test")
        {
            await RunSmokeTestsAsync(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] is "--session-write-test" or "--session-read-test")
        {
            await RunSessionTestsAsync(args[0], args[1]);
            return;
        }
#endif
        await RunAsync(async () =>
        {
            await InitializeSessionAsync();
            await LoadLibraryAsync();
            foreach (string path in args) await OpenFileAsync(path);
            await PersistSessionAsync();
        });
    }

    private void BuildMenus()
    {
        var file = new MenuBarItem { Title = "파일", AccessKey = "F" };
        file.Items.Add(MenuItem("새 파일", "Ctrl+N", () => { NewDocument(); return Task.CompletedTask; }, Symbol.Add));
        file.Items.Add(MenuItem("열기…", "Ctrl+O", OpenDialogAsync, Symbol.OpenFile));
        file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(MenuItem("저장", "Ctrl+S", async () => { if (Active is { } doc) await SaveAsync(doc); }, Symbol.Save));
        file.Items.Add(MenuItem("다른 이름으로 저장…", "Ctrl+Shift+S", async () => { if (Active is { } doc) await SaveAsync(doc, true); }));
        file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(MenuItem("탭 닫기", "Ctrl+W", async () => { if (Active is { } doc) await CloseDocumentAsync(doc); }));
        file.Items.Add(MenuItem("끝내기", "Alt+F4", () =>
        {
            DispatcherQueue.TryEnqueue(async () => await RequestCloseAsync());
            return Task.CompletedTask;
        }));
        var edit = new MenuBarItem { Title = "편집", AccessKey = "E" };
        AddEditItems(edit.Items, editItems);
        edit.PointerEntered += (_, _) => { if (Active is { } doc) UpdateEditItems(editItems, doc, includeClipboard: true); };
        edit.GotFocus += (_, _) => { if (Active is { } doc) UpdateEditItems(editItems, doc, includeClipboard: true); };
        var view = new MenuBarItem { Title = "보기", AccessKey = "V" };
        wrapItem = new ToggleMenuFlyoutItem { Text = "자동 줄 바꿈", IsChecked = true };
        wrapItem.Click += (_, _) => SetWrap(wrapItem.IsChecked);
        view.Items.Add(wrapItem);
        view.Items.Add(new MenuFlyoutSeparator());
        view.Items.Add(MenuItem("확대", "Ctrl++", () => { ChangeZoom(10); return Task.CompletedTask; }, Symbol.ZoomIn));
        view.Items.Add(MenuItem("축소", "Ctrl+-", () => { ChangeZoom(-10); return Task.CompletedTask; }, Symbol.ZoomOut));
        view.Items.Add(MenuItem("기본 배율", "Ctrl+0", () => { zoom = 100; ChangeZoom(0); return Task.CompletedTask; }));
        view.Items.Add(new MenuFlyoutSeparator());
        statusItem = new ToggleMenuFlyoutItem { Text = "상태 표시줄", IsChecked = true };
        statusItem.Click += (_, _) =>
        {
            StatusBar.Visibility = statusItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            ScheduleSessionSave();
        };
        view.Items.Add(statusItem);
        menu.Items.Add(file); menu.Items.Add(edit); menu.Items.Add(view);
        foreach (var item in menu.Items) item.Margin = new Thickness(4, 0, 4, 0);
        AutomationProperties.SetName(menu, "메모장 메뉴");
    }

    private MenuFlyoutItem MenuItem(string text, string shortcut, Func<Task> action, Symbol? icon = null)
    {
        var item = new MenuFlyoutItem { Text = text, KeyboardAcceleratorTextOverride = shortcut };
        if (icon is { } symbol) item.Icon = new SymbolIcon(symbol);
        item.Click += async (_, _) => await RunAsync(action);
        return item;
    }

    private void AddEditItems(IList<MenuFlyoutItemBase> items, Dictionary<string, MenuFlyoutItem> lookup)
    {
        void Add(string key, string text, string shortcut, Action action, Symbol? icon = null)
        {
            var item = MenuItem(text, shortcut, () => { action(); return Task.CompletedTask; }, icon);
            items.Add(item); lookup[key] = item;
        }
        Add("undo", "실행 취소", "Ctrl+Z", () => Active?.Editor.Undo(), Symbol.Undo);
        Add("redo", "다시 실행", "Ctrl+Y", () => Active?.Editor.Redo(), Symbol.Redo);
        items.Add(new MenuFlyoutSeparator());
        Add("cut", "잘라내기", "Ctrl+X", () => Active?.Editor.CutSelectionToClipboard(), Symbol.Cut);
        Add("copy", "복사", "Ctrl+C", () => Active?.Editor.CopySelectionToClipboard(), Symbol.Copy);
        Add("paste", "붙여넣기", "Ctrl+V", () => Active?.Editor.PasteFromClipboard(), Symbol.Paste);
        items.Add(new MenuFlyoutSeparator());
        Add("select", "모두 선택", "Ctrl+A", () => Active?.Editor.SelectAll(), Symbol.SelectAll);
    }

    private void AddShortcuts()
    {
        void Bind(VirtualKey key, VirtualKeyModifiers modifiers, Func<Task> action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += async (_, e) => { e.Handled = true; await RunAsync(action); };
            Root.KeyboardAccelerators.Add(accelerator);
        }
        const VirtualKeyModifiers ctrl = VirtualKeyModifiers.Control;
        Bind(VirtualKey.N, ctrl, () => { NewDocument(); return Task.CompletedTask; });
        Bind(VirtualKey.O, ctrl, OpenDialogAsync);
        Bind(VirtualKey.S, ctrl, async () => { if (Active is { } doc) await SaveAsync(doc); });
        Bind(VirtualKey.S, ctrl | VirtualKeyModifiers.Shift, async () => { if (Active is { } doc) await SaveAsync(doc, true); });
        Bind(VirtualKey.F, ctrl, () => { FocusSearch(); return Task.CompletedTask; });
        Bind(VirtualKey.W, ctrl, async () => { if (Active is { } doc) await CloseDocumentAsync(doc); });
        Bind(VirtualKey.Z, ctrl | VirtualKeyModifiers.Shift, () => { Active?.Editor.Redo(); return Task.CompletedTask; });
        Bind(VirtualKey.Number0, ctrl, () => { zoom = 100; ChangeZoom(0); return Task.CompletedTask; });
        Bind(VirtualKey.Add, ctrl, () => { ChangeZoom(10); return Task.CompletedTask; });
        Bind(VirtualKey.Subtract, ctrl, () => { ChangeZoom(-10); return Task.CompletedTask; });
        Bind((VirtualKey)0xBB, ctrl, () => { ChangeZoom(10); return Task.CompletedTask; });
        Bind((VirtualKey)0xBB, ctrl | VirtualKeyModifiers.Shift, () => { ChangeZoom(10); return Task.CompletedTask; });
        Bind((VirtualKey)0xBD, ctrl, () => { ChangeZoom(-10); return Task.CompletedTask; });
    }

    private static bool KeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnEditorKeys(object sender, KeyRoutedEventArgs e)
    {
        if (busy || dialogOpen || closePending || Active is not { } doc) return;
        if (e.Key == VirtualKey.Tab && KeyDown(VirtualKey.Control))
        {
            int direction = KeyDown(VirtualKey.Shift) ? -1 : 1;
            Tabs.SelectedIndex = (Tabs.SelectedIndex + direction + Tabs.TabItems.Count) % Tabs.TabItems.Count;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Tab && !KeyDown(VirtualKey.Shift) && ReferenceEquals(e.OriginalSource, doc.Editor))
        {
            doc.Editor.SelectedText = "\t";
            e.Handled = true;
        }
    }

    private EditorDocument NewDocument(string? path = null, TextFile? loaded = null, DocumentState? restoredState = null)
    {
        using var timing = MeasureOperation("Tab.create");
        var state = restoredState ?? new DocumentState(path is null ? DocumentState.UntitledName : Path.GetFileName(path));
        if (path is not null && loaded is not null) state.Load(path, loaded);
        var editor = new TextBox
        {
            Style = (Style)Application.Current.Resources["MemoEditorStyle"], AcceptsReturn = true,
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false,
            TextWrapping = wordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontSize = 16 * zoom / 100.0, Text = state.Text, MaxLength = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, wordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        editor.Resources["TextControlBorderBrush"] = new SolidColorBrush(Colors.Transparent);
        editor.Resources["TextControlBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);
        editor.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(Colors.Transparent);
        editor.Resources["TextControlBorderThicknessFocused"] = new Thickness(0);
        editor.Resources["TextControlBackgroundFocused"] = Application.Current.Resources["EditorSurfaceBrush"];
        editor.Resources["TextControlBackgroundPointerOver"] = Application.Current.Resources["EditorSurfaceBrush"];
        AutomationProperties.SetName(editor, "메모 편집 영역");
        AutomationProperties.SetAutomationId(editor, "MemoEditor");
        var surface = new Grid();
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(editor, 1); surface.Children.Add(editor);
        var menuDivider = new Border { Style = (Style)Application.Current.Resources["EditorMenuDividerStyle"] };
        Grid.SetRow(menuDivider, 1); surface.Children.Add(menuDivider);
        var caption = new TextBlock { Width = DocumentTabWidth - 76, TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        var modified = new Ellipse { Width = 6, Height = 6,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Fill = (Brush)Application.Current.Resources["ShellSecondaryTextBrush"],
            IsHitTestVisible = false };
        var tab = new TabViewItem { Header = caption, Content = surface, IsClosable = true,
            MinWidth = DocumentTabWidth, MaxWidth = DocumentTabWidth,
            IconSource = new SymbolIconSource { Symbol = Symbol.Document } };
        var doc = new EditorDocument(state, editor, surface, tab, caption, modified);
        tab.Loaded += (_, _) => AttachTabCloseIndicator(doc);
        tab.PointerEntered += (_, _) => SetTabPointerOver(doc, true);
        tab.PointerExited += (_, e) =>
        {
            var point = e.GetCurrentPoint(tab).Position;
            if (point.X < 0 || point.X >= tab.ActualWidth || point.Y < 0 || point.Y >= tab.ActualHeight)
                SetTabPointerOver(doc, false);
        };
        tab.Unloaded += (_, _) => SetTabPointerOver(doc, false);
        tab.ActualThemeChanged += (_, _) =>
            modified.Fill = (Brush)Application.Current.Resources["ShellSecondaryTextBrush"];
        TraceTabAppearance(doc);
        documents.Add(tab, doc);
        editor.ContextFlyout = CreateEditorContextMenu(doc);
        editor.Loaded += (_, _) =>
        {
            // A queued selection callback can run before the new TextBox is
            // loaded, in which case Focus returns false. Retry when it is ready.
            if (Active == doc && !dialogOpen && !closePending) editor.Focus(FocusState.Programmatic);
        };
        editor.TextChanged += (_, _) =>
        {
            state.Text = TextFiles.Normalize(editor.Text);
            UpdateHeader(doc); ScheduleStatus(); ScheduleSessionSave(); ScheduleSearchRefresh();
        };
        editor.SelectionChanged += (_, _) => { ScheduleStatus(); ScheduleSessionSave(); };
        editor.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
        {
            if (!KeyDown(VirtualKey.Control) || busy || dialogOpen) return;
            var delta = e.GetCurrentPoint(editor).Properties.MouseWheelDelta;
            if (delta != 0) ChangeZoom(delta > 0 ? 10 : -10);
            e.Handled = true;
        }), true);
        Tabs.TabItems.Add(tab); Tabs.SelectedItem = tab;
        UpdateHeader(doc);
        ScheduleSessionSave();
        return doc;
    }

    private MenuFlyout CreateEditorContextMenu(EditorDocument doc)
    {
        using var timing = MeasureOperation("Tab.context-menu");
        var context = new MenuFlyout();
        var ownItems = new Dictionary<string, MenuFlyoutItem>();
        AddEditItems(context.Items, ownItems);
        context.Opening += (_, _) => UpdateEditItems(ownItems, doc, includeClipboard: true);
        return context;
    }

    private void OnAddTab(TabView sender, object args)
    { if (!busy && !dialogOpen && !closePending) NewDocument(); }

    private void OnTabsLoaded(object sender, RoutedEventArgs e)
    {
        // The native strip delays new headers with AddDelete/Entrance/Content
        // transitions. Show additions immediately; retain drag reorder motion.
        var strip = Descendants<ListViewBase>(Tabs).FirstOrDefault(list => list.Name == "TabListView");
        if (strip is not null)
            strip.ItemContainerTransitions = new TransitionCollection { new ReorderThemeTransition() };
    }

    private async void OnTabClose(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (documents.TryGetValue(args.Tab, out var doc)) await RunAsync(async () => { await CloseDocumentAsync(doc); });
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Active is not { } doc) return;
        // Parent can be null while WinUI unloads an inactive tab. Track the owning
        // collection explicitly so the shared menu never has two logical parents.
        if (menuOwner != doc.Surface)
        {
            menuOwner?.Children.Remove(menu);
            doc.Surface.Children.Add(menu);
            menuOwner = doc.Surface;
            Grid.SetRow(menu, 0);
        }
        ScheduleSessionSave();
        DispatcherQueue.TryEnqueue(() =>
        {
            using var timing = MeasureOperation("Tab.focus-and-refresh");
            if (Active != doc) return;
            if (!dialogOpen) doc.Editor.Focus(FocusState.Programmatic);
            Refresh();
        });
    }

    private void UpdateHeader(EditorDocument doc)
    {
        string title = doc.State.DisplayName;
        if (doc.Caption.Text != title) doc.Caption.Text = title;
        UpdateTabCloseIndicator(doc);
        AutomationProperties.SetName(doc.Tab, doc.State.IsDirty ? $"{title}, 수정됨" : title);
        ToolTipService.SetToolTip(doc.Tab, doc.State.Path ?? title);
        if (Active == doc) Title = $"{(doc.State.IsDirty ? "*" : "")}{title} — MemoLite";
    }

    private void AttachTabCloseIndicator(EditorDocument doc)
    {
        var button = Descendants<Button>(doc.Tab).FirstOrDefault(child => child.Name == "CloseButton");
        if (button is null) return;
        if (doc.CloseButton is { } previous && previous != button) previous.Content = TabCloseGlyph;
        doc.CloseButton = button;
        UpdateTabCloseIndicator(doc);
    }

    private void SetTabPointerOver(EditorDocument doc, bool over)
    {
        doc.PointerOver = over;
        UpdateTabCloseIndicator(doc);
    }

    private static void UpdateTabCloseIndicator(EditorDocument doc)
    {
        bool showMarker = doc.State.IsDirty && !doc.PointerOver;
        doc.ModifiedIndicator.Opacity = showMarker ? 1 : 0;
        if (doc.CloseButton is not { } button) return;
        // Reuse the native close button so its size, hit target, accessibility,
        // and save/discard confirmation remain the same for both appearances.
        object content = showMarker ? doc.ModifiedIndicator : TabCloseGlyph;
        if (!Equals(button.Content, content)) button.Content = content;
    }

    private void ScheduleStatus() { if (!statusTimer.IsEnabled) statusTimer.Start(); }

    private void Refresh()
    {
        using var timing = MeasureOperation("UI.refresh");
        if (Active is not { } doc) return;
        var (line, column) = DocumentState.Position(doc.Editor.Text, doc.Editor.SelectionStart);
        int count = doc.State.Text.EnumerateRunes().Count();
        string selected = doc.Editor.SelectionLength > 0 ? $"  ·  {TextFiles.Normalize(doc.Editor.SelectedText).EnumerateRunes().Count():N0}자 선택" : "";
        PositionStatus.Text = $"줄 {line:N0}, 열 {column:N0}  ·  {count:N0}자{selected}";
        FormatStatus.Text = $"{TextFiles.NewlineLabel(doc.State.Newline)}    {TextFiles.EncodingLabel(doc.State.Encoding)}";
        ZoomStatus.Text = $"{zoom}%";
        UpdateEditItems(editItems, doc); UpdateHeader(doc);
    }

    private void UpdateEditItems(Dictionary<string, MenuFlyoutItem> items, EditorDocument doc, bool includeClipboard = false)
    {
        items["undo"].IsEnabled = doc.Editor.CanUndo;
        items["redo"].IsEnabled = doc.Editor.CanRedo;
        items["cut"].IsEnabled = items["copy"].IsEnabled = doc.Editor.SelectionLength > 0;
        // Clipboard access can wait for another process. Only query when the
        // user is about to use an edit menu, never while opening a tab.
        if (!includeClipboard) return;
        using var timing = MeasureOperation("Clipboard.query");
        try { items["paste"].IsEnabled = Clipboard.GetContent().Contains(StandardDataFormats.Text); }
        catch { items["paste"].IsEnabled = false; }
    }

    private void SetWrap(bool enabled)
    {
        wordWrap = enabled; wrapItem.IsChecked = enabled;
        foreach (var doc in documents.Values)
        {
            doc.Editor.TextWrapping = enabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
            ScrollViewer.SetHorizontalScrollBarVisibility(doc.Editor, enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        }
        ScheduleSessionSave();
    }

    private void ChangeZoom(int delta)
    {
        zoom = Math.Clamp(zoom + delta, 10, 500);
        foreach (var doc in documents.Values) doc.Editor.FontSize = Math.Max(1, 16 * zoom / 100.0);
        Refresh();
        ScheduleSessionSave();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private ScrollViewer? TabScroller() => Descendants<ListViewBase>(Tabs)
        .SelectMany(Descendants<ScrollViewer>).FirstOrDefault();

    private void OnTabWheel(object sender, PointerRoutedEventArgs e)
    {
        var scroll = TabScroller();
        if (scroll is null) return;
        var bounds = scroll.TransformToVisual(Tabs).TransformBounds(new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight));
        var point = e.GetCurrentPoint(Tabs);
        if (point.Position.Y < bounds.Top || point.Position.Y > bounds.Bottom) return;
        if (scroll.ScrollableWidth > 0)
        {
            double target = scroll.HorizontalOffset - point.Properties.MouseWheelDelta / 120.0 * 80;
            scroll.ChangeView(Math.Clamp(target, 0, scroll.ScrollableWidth), null, null, true);
        }
        e.Handled = true;
    }

    private async Task OpenDialogAsync()
    {
        var picker = new FileOpenPicker(AppWindow.Id)
        { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, Title = "파일 열기", SettingsIdentifier = "MemoLite.Open" };
        picker.FileTypeChoices.Add("텍스트 문서", new List<string> { ".txt" });
        picker.FileTypeChoices.Add("모든 파일", new List<string> { "*" });
        foreach (var file in await picker.PickMultipleFilesAsync()) await OpenFileAsync(file.Path);
    }

    /// <summary>
    /// The active tab if opening a document may replace it: untitled, empty, and unmodified.
    /// An emptied library draft is still modified and keeps its undo history, so it is never replaced.
    /// </summary>
    private EditorDocument? ReplaceableBlank() =>
        Active is { State: { Path: null, Text.Length: 0, IsDirty: false } } blank ? blank : null;

    private async Task<EditorDocument?> OpenFileAsync(string path)
    {
        path = Path.GetFullPath(path);
        var existing = documents.Values.FirstOrDefault(d => DocumentState.SamePath(d.State.Path, path));
        if (existing is not null) { Tabs.SelectedItem = existing.Tab; return existing; }
        try
        {
            var loaded = await TextFiles.ReadAsync(path);
            var blank = ReplaceableBlank();
            var doc = NewDocument(path, loaded);
            if (blank is not null) RemoveDocument(blank);
            return doc;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ShowMessageAsync("파일을 열 수 없습니다", $"{path}\n\n{error.Message}");
            return null;
        }
    }

    private async Task<bool> SaveAsync(EditorDocument doc, bool saveAs = false)
    {
        string? path = doc.State.Path;
        if (saveAs || path is null)
        {
            var picker = new FileSavePicker(AppWindow.Id)
            {
                SuggestedFileName = doc.State.Name,
                DefaultFileExtension = ".txt", SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                Title = "다른 이름으로 저장", SettingsIdentifier = "MemoLite.Save"
            };
            if (doc.State.Path is not null) picker.SuggestedFolder = Path.GetDirectoryName(doc.State.Path)!;
            picker.FileTypeChoices.Add("텍스트 문서", new List<string> { ".txt" });
            picker.FileTypeChoices.Add("모든 파일", new List<string> { "*" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return false;
            path = file.Path;
        }
        return await WriteDocumentAsync(doc, path);
    }

    private async Task<bool> WriteDocumentAsync(EditorDocument doc, string path)
    {
        if (documents.Values.Any(other => other != doc && DocumentState.SamePath(other.State.Path, path)))
        {
            await ShowMessageAsync("이미 열린 파일", "다른 탭에서 열려 있는 파일입니다. 다른 이름을 선택하세요.");
            return false;
        }
        try
        {
            if (DocumentState.SamePath(doc.State.Path, path) && doc.State.Digest is not null &&
                (!File.Exists(path) || await TextFiles.FingerprintAsync(path) != doc.State.Digest))
            {
                if (await ConfirmAsync("파일 변경 확인", "이 파일이 외부에서 변경되었거나 삭제되었습니다. 현재 내용으로 저장할까요?", "저장") != ContentDialogResult.Primary)
                    return false;
            }
            string content = doc.State.Text = TextFiles.Normalize(doc.Editor.Text);
            var encoding = doc.State.Encoding;
            string digest;
            try { digest = await TextFiles.WriteAsync(path, content, encoding, doc.State.Newline); }
            catch (EncoderFallbackException)
            {
                if (await ConfirmAsync("UTF-8로 저장", "기존 인코딩으로 표현할 수 없는 문자가 있습니다. UTF-8로 저장할까요?", "UTF-8로 저장") != ContentDialogResult.Primary)
                    return false;
                encoding = TextEncodingKind.Utf8; content = doc.State.Text = TextFiles.Normalize(doc.Editor.Text);
                digest = await TextFiles.WriteAsync(path, content, encoding, doc.State.Newline);
            }
            doc.State.MarkSaved(path, content, digest, encoding);
            UpdateHeader(doc); Refresh();
            ScheduleSessionSave();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ShowMessageAsync("파일을 저장할 수 없습니다", $"{path}\n\n{error.Message}");
            return false;
        }
    }

    private async Task<bool> ConfirmCloseAsync(EditorDocument doc)
    {
        // TextChanged can still be queued when a tab is closed immediately after
        // an edit. Read the control before deciding that a document is clean.
        doc.State.Text = TextFiles.Normalize(doc.Editor.Text);
        if (!doc.State.IsDirty) return true;
        Tabs.SelectedItem = doc.Tab;
        var result = await ConfirmAsync("변경 내용 저장", $"'{doc.State.DisplayName}'의 변경 내용을 저장할까요?", "저장", "저장 안 함");
        if (result == ContentDialogResult.None) return false;
        if (result == ContentDialogResult.Secondary) return true;
        return await SaveAsync(doc) && !doc.State.IsDirty;
    }

    private async Task<bool> CloseDocumentAsync(EditorDocument doc)
    {
        if (!await ConfirmCloseAsync(doc)) return false;
        // Commit the removal before changing the UI: a failed checkpoint must
        // neither lose a tab nor bring a discarded draft back on the next run.
        bool wasBusy = busy;
        busy = true;
        sessionTimer.Stop();
        try
        {
            if (sessionFile is not null) await PersistSessionAsync(excluded: doc);
            RemoveDocument(doc);
            if (documents.Count == 0) NewDocument();
            return true;
        }
        finally { busy = wasBusy; ScheduleSessionSave(); }
    }

    private void RemoveDocument(EditorDocument doc)
    {
        documents.Remove(doc.Tab); Tabs.TabItems.Remove(doc.Tab);
        if (menuOwner == doc.Surface)
        {
            doc.Surface.Children.Remove(menu);
            menuOwner = null;
        }
        doc.Tab.Content = null;
    }

    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose) return;
        args.Cancel = true;
        await RequestCloseAsync();
    }

    // Window.Close() bypasses AppWindow.Closing. All application-owned exit
    // commands must pass through this method before calling the native Close.
    private async Task RequestCloseAsync()
    {
        if (busy || closePending || dialogOpen) return;
        closePending = true;
        sessionTimer.Stop();
        try
        {
            var snapshot = CaptureSession();
            Tabs.IsEnabled = false;
            foreach (var doc in documents.Values) doc.Editor.IsReadOnly = true;
            await (sessionFile ?? throw new InvalidOperationException("탭 임시 보관 폴더를 준비하지 못했습니다.")).SaveAsync(snapshot);
            allowClose = true; DispatcherQueue.TryEnqueue(Close);
        }
        catch (Exception error)
        {
            await ShowMessageAsync("탭을 보관하지 못했습니다", $"내용을 보호하기 위해 창을 닫지 않았습니다.\n\n{error.Message}");
        }
        finally
        {
            if (!allowClose)
            {
                foreach (var doc in documents.Values) doc.Editor.IsReadOnly = false;
                Tabs.IsEnabled = true;
                closePending = false;
                ScheduleSessionSave();
            }
        }
    }

    private async Task<ContentDialogResult> ConfirmAsync(string title, string message, string primary, string? secondary = null)
    {
        dialogOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = message,
                PrimaryButtonText = primary, SecondaryButtonText = secondary ?? "", CloseButtonText = "취소", DefaultButton = ContentDialogButton.Primary };
#if DEBUG
            if (confirmForTest is not null) return confirmForTest(dialog);
#endif
            return await dialog.ShowAsync();
        }
        finally { dialogOpen = false; }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        dialogOpen = true;
        try { await new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = message, CloseButtonText = "확인" }.ShowAsync(); }
        finally { dialogOpen = false; }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || dialogOpen || closePending) return;
        busy = true;
        try { await action(); }
        catch (Exception error) { await ShowMessageAsync("작업을 완료하지 못했습니다", error.Message); }
        finally { busy = false; Refresh(); }
    }
}
