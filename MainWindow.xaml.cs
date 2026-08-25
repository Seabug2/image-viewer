using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.IO;

namespace ImageViewer;

public partial class MainWindow : Window
{
    private readonly ImagePane[] _panes = Enumerable.Range(0, 6).Select(_ => new ImagePane()).ToArray();
    private readonly System.Windows.Threading.DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private bool _fullscreen; private WindowState _stateBeforeFullscreen; private Rect _boundsBeforeFullscreen;
    public MainWindow()
    {
        InitializeComponent(); foreach (var pane in _panes) { pane.SynchronizeIntervalRequested += SynchronizeIntervals; pane.SynchronizeShuffleRequested += SynchronizeShuffle; }
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); LayoutBar.Opacity = 0; }; SetLayout(1);
    }
    private void SynchronizeIntervals(ImagePane source, int seconds) { foreach (var pane in _panes) if (!ReferenceEquals(pane, source)) pane.SetSecondsFromSynchronization(seconds); }
    private void SynchronizeShuffle(ImagePane source, bool enabled) { foreach (var pane in _panes) if (!ReferenceEquals(pane, source)) pane.SetShuffleFromSynchronization(enabled); }
    public Task OpenPathsAsync(IEnumerable<string> paths) => _panes[0].LoadPathsAsync(paths);
    private void SetLayout(int count)
    {
        for (int i = count; i < _panes.Length; i++) _panes[i].SetActive(false);
        PaneGrid.Children.Clear(); PaneGrid.RowDefinitions.Clear(); PaneGrid.ColumnDefinitions.Clear(); int rows = count is 4 or 6 ? 2 : 1; int cols = count switch { 1 => 1, 2 or 4 => 2, 3 or 6 => 3, _ => 1 };
        for (int i = 0; i < rows; i++) PaneGrid.RowDefinitions.Add(new RowDefinition()); for (int i = 0; i < cols; i++) PaneGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < count; i++) { PaneGrid.Children.Add(_panes[i]); Grid.SetRow(_panes[i], i / cols); Grid.SetColumn(_panes[i], i % cols); }
        PaneGrid.UpdateLayout();
        for (int i = 0; i < count; i++) _panes[i].SetActive(true);
    }
    private void Layout_Click(object sender, RoutedEventArgs e) => SetLayout(int.Parse((string)((Button)sender).Tag));
    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        var acceptsFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        foreach (var pane in _panes)
        {
            var isTarget = false;
            if (acceptsFiles && pane.IsVisible)
            {
                var point = e.GetPosition(pane);
                isTarget = point.X >= 0 && point.Y >= 0 && point.X < pane.ActualWidth && point.Y < pane.ActualHeight;
            }
            pane.SetDropHintActive(isTarget);
        }
    }
    private void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(this);
        if (point.X <= 0 || point.Y <= 0 || point.X >= ActualWidth || point.Y >= ActualHeight) ClearAllDropHints();
    }
    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        ClearAllDropHints();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length < 2 || !paths.All(IsAssignableSourcePath)) return;
        var start = FindPaneAt(e);
        if (start < 0) return;

        e.Handled = true;
        var assignable = Math.Min(paths.Length, _panes.Length - start);
        for (int offset = 0; offset < assignable; offset++) await _panes[start + offset].LoadPathsAsync([paths[offset]]);
    }
    private int FindPaneAt(DragEventArgs e)
    {
        for (int i = 0; i < _panes.Length; i++)
        {
            var pane = _panes[i];
            if (!pane.IsVisible) continue;
            var point = e.GetPosition(pane);
            if (point.X >= 0 && point.Y >= 0 && point.X < pane.ActualWidth && point.Y < pane.ActualHeight) return i;
        }
        return -1;
    }
    private static bool IsAssignableSourcePath(string path)
    {
        if (Directory.Exists(path)) return true;
        if (!File.Exists(path)) return false;
        var extension = Path.GetExtension(path);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cbz", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase) || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }
    private void ClearAllDropHints() { foreach (var pane in _panes) pane.SetDropHintActive(false); }
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) { ExitFullscreen(); return; } var handle = new WindowInteropHelper(this).Handle; var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest); var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }; if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        _stateBeforeFullscreen = WindowState; _boundsBeforeFullscreen = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true; SetWindowPos(handle, HwndTop, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, ShowWindowFlag); _fullscreen = true; FullscreenButton.Content = "⛶ 나가기";
    }
    private void ExitFullscreen() { Topmost = false; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; Left = _boundsBeforeFullscreen.Left; Top = _boundsBeforeFullscreen.Top; Width = _boundsBeforeFullscreen.Width; Height = _boundsBeforeFullscreen.Height; WindowState = _stateBeforeFullscreen; _fullscreen = false; FullscreenButton.Content = "⛶"; }
    private const uint MonitorDefaultToNearest = 2, ShowWindowFlag = 0x0040; private static readonly IntPtr HwndTop = IntPtr.Zero;
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }
    private void Root_MouseMove(object sender, MouseEventArgs e) { if (e.GetPosition(Root).Y < 105) { LayoutBar.Opacity = 1; _hideTimer.Stop(); } else if (LayoutBar.Opacity > 0 && !_hideTimer.IsEnabled) _hideTimer.Start(); }
    private void Root_MouseLeave(object sender, MouseEventArgs e) { _hideTimer.Stop(); LayoutBar.Opacity = 0; }
}
