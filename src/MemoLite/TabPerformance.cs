using System.Diagnostics;
#if DEBUG
using System.Collections.Concurrent;
using MemoLite.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Text;
#endif

namespace MemoLite;

public sealed partial class MainWindow
{
#if DEBUG
    private string? inputTracePath;
    private readonly List<string> inputTrace = [];
    private long inputTraceStart;

    private async Task StartTabInputTraceAsync(string report)
    {
        await InitializeSessionAsync(Path.Combine(report + ".data", Guid.NewGuid().ToString("N")));
        NewDocument(); NewDocument();
        await Task.Delay(300);
        inputTracePath = report;
        inputTraceStart = Stopwatch.GetTimestamp();
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => TraceInput("pointer pressed")), true);
        Root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => TraceInput("pointer released")), true);
        await File.WriteAllTextAsync(report, "READY\n");
    }

    private void TraceInput(string name)
    {
        if (inputTracePath is null) return;
        inputTrace.Add($"{Stopwatch.GetElapsedTime(inputTraceStart).TotalMilliseconds:F3}ms {name}");
    }

    private void TraceTabAppearance(EditorDocument doc)
    {
        if (inputTracePath is null) return;
        long started = Stopwatch.GetTimestamp();
        TraceInput("NewDocument constructed controls");
        doc.Tab.Loaded += TabLoaded;
        doc.Editor.Loaded += EditorLoaded;
        void TabLoaded(object sender, RoutedEventArgs e)
        {
            doc.Tab.Loaded -= TabLoaded;
            TraceInput($"tab Loaded +{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}ms");
        }
        void EditorLoaded(object sender, RoutedEventArgs e)
        {
            doc.Editor.Loaded -= EditorLoaded;
            TraceInput($"editor Loaded +{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}ms");
        }
        EventHandler<object>? rendered = null;
        rendered = async (_, _) =>
        {
            if (doc.Tab.ActualWidth <= 0 || doc.Editor.ActualHeight <= 0) return;
            CompositionTarget.Rendering -= rendered;
            TraceInput($"first arranged render +{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}ms; focus={doc.Editor.FocusState}; tabs={Tabs.TabItems.Count}");
            await File.WriteAllLinesAsync(inputTracePath, inputTrace.ToArray());
        };
        CompositionTarget.Rendering += rendered;
    }

    private sealed class PerformanceScope(Action<double> record) : IDisposable
    {
        private readonly long start = Stopwatch.GetTimestamp();
        public void Dispose() => record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
    private string performanceCase = "warmup";
    private ConcurrentQueue<(string Case, string Operation, double Ms, int Thread)>? performanceSamples;
    private IDisposable? MeasureOperation(string operation)
    {
        if (performanceSamples is null) return null;
        string phase = performanceCase;
        return new PerformanceScope(ms => performanceSamples.Enqueue((phase, operation, ms, Environment.CurrentManagedThreadId)));
    }

    private async Task RunTabProfileAsync(string report)
    {
        int uiThread = Environment.CurrentManagedThreadId;
        var samples = new ConcurrentQueue<(string Case, string Operation, double Ms, int Thread)>();
        performanceSamples = samples;
        SessionFile.TimingObserved = (name, ms, thread) => samples.Enqueue((performanceCase, name, ms, thread));
        long previousTick = Stopwatch.GetTimestamp();
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        heartbeat.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            samples.Enqueue((performanceCase, "UI.frame-gap", Stopwatch.GetElapsedTime(previousTick, now).TotalMilliseconds, uiThread));
            previousTick = now;
        };
        heartbeat.Start();
        var output = new StringBuilder();
        try
        {
            await InitializeSessionAsync(Path.Combine(report + ".data", Guid.NewGuid().ToString("N")));
            await Task.Delay(300);
            performanceCase = "small-notes";
            for (int i = 0; i < 20; i++)
            {
                if (i % 2 == 0) NewDocument();
                else await RunAsync(() => { NewDocument(); return Task.CompletedTask; });
                await Task.Delay(55);
            }
            performanceCase = "prepare-large-notes";
            string text = string.Concat(Enumerable.Repeat("회의 메모와 작업 아이디어를 기록합니다.\n", 2400));
            foreach (var doc in documents.Values.Take(8)) doc.Editor.Text = text;
            await Task.Delay(200);
            performanceCase = "checkpoint-overlap";
            for (int i = 0; i < 12; i++)
            {
                long requested = Stopwatch.GetTimestamp();
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DispatcherQueue.TryEnqueue(() =>
                {
                    samples.Enqueue((performanceCase, "Tab.queued-wait", Stopwatch.GetElapsedTime(requested).TotalMilliseconds, uiThread));
                    NewDocument();
                    opened.SetResult();
                });
                await PersistSessionAsync();
                await opened.Task;
                await Task.Delay(55);
            }
            performanceCase = "idle-checkpoint";
            await Task.Delay(1200);
            output.AppendLine($"UI thread: {uiThread}");
            output.AppendLine("case | operation | n | median ms | p95 ms | max ms | calls on UI thread");
            foreach (var group in samples.Where(s => s.Case is not ("warmup" or "prepare-large-notes")).GroupBy(s => (s.Case, s.Operation)))
            {
                var values = group.Select(s => s.Ms).Order().ToArray();
                output.AppendLine($"{group.Key.Case} | {group.Key.Operation} | {values.Length} | {values[values.Length / 2]:F3} | {values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * .95) - 1)]:F3} | {values[^1]:F3} | {group.Count(s => s.Thread == uiThread)}");
            }
            output.AppendLine("SUCCESS");
        }
        catch (Exception error) { output.AppendLine("FAIL " + error); }
        finally
        {
            heartbeat.Stop(); sessionTimer.Stop(); statusTimer.Stop();
            performanceSamples = null;
            SessionFile.TimingObserved = null;
            await File.WriteAllTextAsync(report, output.ToString());
            allowClose = true;
            Close();
        }
    }
#else
    private static void TraceTabAppearance(EditorDocument doc) { }
    private static IDisposable? MeasureOperation(string operation) => null;
#endif
}
