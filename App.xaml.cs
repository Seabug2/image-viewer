using System.Windows;

namespace ImageViewer;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow(); MainWindow = window; window.Show();
        if (e.Args.Length > 0) await window.OpenPathsAsync(e.Args);
    }
}
