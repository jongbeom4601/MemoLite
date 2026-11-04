using Microsoft.UI.Xaml;

namespace MemoLite;

public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemoLite");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "error.log"), $"{DateTimeOffset.Now:O}\n{e.Exception}\n");
            }
            catch { /* Logging must not hide the original error. */ }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
