using Microsoft.Win32;
using SkiaSharp;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageViewer;

public partial class ImagePane : UserControl
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".cbz" };
    private readonly List<ImageItem> _items = []; private readonly Queue<int> _shuffleBag = new(); private readonly Random _random = new();
    private readonly DispatcherTimer _timer = new(), _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private int _index = -1, _loadVersion; private bool _syncing, _rightDraggingSlider, _syncingPosition, _isActive = true; private string? _sourcePath; private CancellationTokenSource? _animationCancellation;
    public event Action<ImagePane, int>? SynchronizeIntervalRequested; public event Action<ImagePane, bool>? SynchronizeShuffleRequested;

    public void SetActive(bool active)
    {
        if (_isActive == active) return; _isActive = active;
        if (!active) { _timer.Stop(); _animationCancellation?.Cancel(); _loadVersion++; }
        else if (_items.Count > 0) { ShowCurrent(); ApplyInterval(); }
    }

    public ImagePane() { InitializeComponent(); _timer.Tick += (_, _) => Next(); _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); BottomBar.Opacity = 0; }; }
    private async void Open_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Title = "이미지 또는 ZIP/CBZ 열기", Filter = "지원 파일|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.zip;*.cbz|모든 파일|*.*", Multiselect = true }; if (d.ShowDialog() == true) await LoadPathsAsync(d.FileNames); }
    private async void OnDrop(object sender, DragEventArgs e) { DropHint.Visibility = Visibility.Collapsed; if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths) await LoadPathsAsync(paths); }
    private void OnDragEnter(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; DropHint.Visibility = e.Effects == DragDropEffects.Copy ? Visibility.Visible : Visibility.Collapsed; e.Handled = true; }
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(this);
        if (point.X <= 0 || point.Y <= 0 || point.X >= ActualWidth || point.Y >= ActualHeight) DropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    public async Task LoadPathsAsync(IEnumerable<string> paths)
    {
        try
        {
            var pathList = paths.ToArray(); var loaded = await Task.Run(() => ReadItems(pathList));
            if (loaded.Count == 0) { _sourcePath = ResolveSourcePath(pathList); _items.Clear(); _index = -1; ShowSkippingStatus("다음 ZIP/CBZ 또는 폴더로 이동합니다…"); await MoveSourceAsync(1); return; }
            ApplyLoadedItems(pathList, loaded);
        }
        catch (Exception ex) { MessageBox.Show($"파일을 열 수 없습니다.\n\n{ex.Message}", "Image Viewer", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void ApplyLoadedItems(string[] paths, List<ImageItem> loaded)
    {
        _timer.Stop(); _animationCancellation?.Cancel(); Picture.Source = null; _items.Clear(); _items.AddRange(loaded); _sourcePath = ResolveSourcePath(paths); _index = 0; EmptyStatus.Visibility = Visibility.Collapsed; ClearButton.Visibility = Visibility.Visible; ClearButton.Opacity = 0; ClearButton.IsHitTestVisible = false;
        _syncingPosition = true; PositionSlider.Minimum = 1; PositionSlider.Maximum = Math.Max(1, _items.Count); PositionSlider.Value = 1; _syncingPosition = false; ResetShuffle(); ShowCurrent(); ApplyInterval();
    }
    private void ShowSkippingStatus(string destination) { _timer.Stop(); _animationCancellation?.Cancel(); Picture.Source = null; EmptyPanel.Visibility = Visibility.Visible; EmptyStatus.Visibility = Visibility.Visible; EmptyStatus.Text = $"지원되는 이미지를 찾지 못했습니다.\n{destination}"; Caption.Visibility = Visibility.Collapsed; BottomBar.Opacity = 0; }
    private static List<ImageItem> ReadItems(IEnumerable<string> paths)
    {
        var result = new List<ImageItem>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Where(IsImage).OrderBy(NaturalKey, StringComparer.CurrentCultureIgnoreCase)) result.Add(new(Path.GetFileName(file), file));
            else if (File.Exists(path) && IsImage(path)) result.Add(new(Path.GetFileName(path), path));
            else if (File.Exists(path) && ArchiveExtensions.Contains(Path.GetExtension(path))) { using var archive = ZipFile.OpenRead(path); foreach (var entry in archive.Entries.Where(e => IsImage(e.FullName)).OrderBy(e => NaturalKey(e.FullName), StringComparer.CurrentCultureIgnoreCase)) result.Add(new($"{Path.GetFileName(path)} · {entry.FullName}", null, path, entry.FullName)); }
        }
        return result;
    }
    private static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
    private static string NaturalKey(string value) => Regex.Replace(value, @"\d+", m => m.Value.PadLeft(16, '0'));
    private static string? ResolveSourcePath(string[] paths)
    {
        if (paths.Length != 1) return paths.Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p)).Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? Path.GetDirectoryName(paths[0]) : null;
        var path = paths[0]; if (Directory.Exists(path) || ArchiveExtensions.Contains(Path.GetExtension(path))) return Path.GetFullPath(path); return File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;
    }

    private async void ShowCurrent()
    {
        if (!_isActive || _index < 0 || _index >= _items.Count) return; _animationCancellation?.Cancel(); _animationCancellation?.Dispose(); _animationCancellation = new CancellationTokenSource(); var token = _animationCancellation.Token; var version = ++_loadVersion; var item = _items[_index];
        _syncingPosition = true; PositionSlider.Value = _index + 1; PositionText.Text = $"{_index + 1} / {_items.Count}"; _syncingPosition = false; Caption.Visibility = Visibility.Visible; CaptionText.Text = $"불러오는 중…   {item.Name}";
        try
        {
            var targetWidth = Math.Max(1, (int)(ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX));
            if (IsAnimationCandidate(item))
            {
                var encoded = await Task.Run(() => ReadEncodedItem(item), token); var frameCount = await Task.Run(() => GetFrameCount(encoded), token);
                if (frameCount > 1) { if (version != _loadVersion) return; EmptyPanel.Visibility = Visibility.Collapsed; _ = Task.Run(() => PlayAnimationSafe(encoded, targetWidth, version, token), token); return; }
            }
            var bitmap = await Task.Run(() => DecodeBitmap(item, targetWidth)); if (version != _loadVersion) return; Picture.Source = bitmap; EmptyPanel.Visibility = Visibility.Collapsed; CaptionText.Text = $"{item.Name}   {_index + 1} / {_items.Count}";
        }
        catch (Exception ex) { if (version != _loadVersion) return; _timer.Stop(); Picture.Source = null; EmptyPanel.Visibility = Visibility.Visible; CaptionText.Text = $"열기 실패: {item.Name} · {ex.Message}"; }
    }
    private static bool IsAnimationCandidate(ImageItem item) { var ext = Path.GetExtension(item.ArchiveEntry ?? item.FilePath ?? item.Name); return ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase); }
    private static byte[] ReadEncodedItem(ImageItem item)
    {
        const long max = 256L * 1024 * 1024;
        if (item.ArchivePath != null) { using var archive = ZipFile.OpenRead(item.ArchivePath); var entry = archive.GetEntry(item.ArchiveEntry!) ?? throw new InvalidDataException("압축 항목을 찾을 수 없습니다."); if (entry.Length > max) throw new InvalidDataException("애니메이션 항목이 256MB를 초과합니다."); using var input = entry.Open(); using var memory = new MemoryStream((int)entry.Length); input.CopyTo(memory); return memory.ToArray(); }
        var info = new FileInfo(item.FilePath!); if (info.Length > max) throw new InvalidDataException("애니메이션 파일이 256MB를 초과합니다."); return File.ReadAllBytes(item.FilePath!);
    }
    private static int GetFrameCount(byte[] encoded) { using var data = SKData.CreateCopy(encoded); using var codec = SKCodec.Create(data); return codec?.FrameInfo.Length ?? 0; }
    private void PlayAnimation(byte[] encoded, int targetWidth, int version, CancellationToken token)
    {
        using var data = SKData.CreateCopy(encoded); using var codec = SKCodec.Create(data); if (codec == null) return; var frames = codec.FrameInfo; if (frames.Length < 2) return;
        var sourceInfo = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul); using var decoded = new SKBitmap(sourceInfo); int outputWidth = Math.Min(sourceInfo.Width, Math.Max(1, targetWidth)); int outputHeight = Math.Max(1, (int)Math.Round(sourceInfo.Height * (outputWidth / (double)sourceInfo.Width))); using var output = new SKBitmap(new SKImageInfo(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int frame = 0; !token.IsCancellationRequested; frame = (frame + 1) % frames.Length)
        {
            if (!Dispatcher.Invoke(() => IsVisible)) { if (token.WaitHandle.WaitOne(100)) return; frame--; continue; }
            var options = frame == 0 ? new SKCodecOptions(0) : new SKCodecOptions(frame, frame - 1); var result = codec.GetPixels(sourceInfo, decoded.GetPixels(), decoded.RowBytes, options); if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput) throw new InvalidDataException($"애니메이션 프레임 디코딩 실패: {result}");
            using (var canvas = new SKCanvas(output)) { canvas.Clear(SKColors.Transparent); canvas.DrawBitmap(decoded, new SKRect(0, 0, outputWidth, outputHeight), new SKSamplingOptions(SKFilterMode.Linear), null); canvas.Flush(); }
            var source = BitmapSource.Create(outputWidth, outputHeight, 96, 96, PixelFormats.Bgra32, null, output.GetPixels(), output.RowBytes * outputHeight, output.RowBytes); source.Freeze(); Dispatcher.Invoke(() => { if (version == _loadVersion && !token.IsCancellationRequested) Picture.Source = source; }); if (token.WaitHandle.WaitOne(Math.Clamp(frames[frame].Duration, 20, 10_000))) return;
        }
    }
    private void PlayAnimationSafe(byte[] encoded, int targetWidth, int version, CancellationToken token) { try { PlayAnimation(encoded, targetWidth, version, token); } catch (OperationCanceledException) { } catch (Exception ex) { Dispatcher.Invoke(() => { if (version != _loadVersion || token.IsCancellationRequested) return; _timer.Stop(); Caption.Visibility = Visibility.Visible; CaptionText.Text = $"애니메이션 재생 실패 · {ex.Message}"; }); } }
    private static BitmapImage DecodeBitmap(ImageItem item, int targetWidth)
    {
        Stream? source = null; ZipArchive? archive = null; MemoryStream? buffered = null; string? tempPath = null;
        try
        {
            if (item.ArchivePath != null) { archive = ZipFile.OpenRead(item.ArchivePath); var entry = archive.GetEntry(item.ArchiveEntry!) ?? throw new InvalidDataException("압축 항목을 찾을 수 없습니다."); using var input = entry.Open(); if (entry.Length <= 64L * 1024 * 1024) { buffered = entry.Length <= int.MaxValue ? new MemoryStream((int)entry.Length) : new MemoryStream(); input.CopyTo(buffered); buffered.Position = 0; source = buffered; } else { tempPath = Path.Combine(Path.GetTempPath(), $"ImageViewer-{Guid.NewGuid():N}.tmp"); using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan)) input.CopyTo(output); source = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.DeleteOnClose | FileOptions.SequentialScan); } }
            else source = new FileStream(item.FilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; bitmap.DecodePixelWidth = targetWidth; bitmap.StreamSource = source; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        finally { source?.Dispose(); buffered?.Dispose(); archive?.Dispose(); if (tempPath != null) try { File.Delete(tempPath); } catch { } }
    }

    private void Next() { if (_items.Count == 0) return; if (ShuffleToggle.IsChecked == true) { if (_shuffleBag.Count == 0) ResetShuffle(); _index = _shuffleBag.Dequeue(); } else _index = (_index + 1) % _items.Count; ShowCurrent(); }
    private void Previous() { if (_items.Count == 0) return; _index = (_index - 1 + _items.Count) % _items.Count; ShowCurrent(); }
    private void ResetShuffle() { _shuffleBag.Clear(); foreach (var i in Enumerable.Range(0, _items.Count).Where(i => i != _index).OrderBy(_ => _random.Next())) _shuffleBag.Enqueue(i); if (_items.Count == 1) _shuffleBag.Enqueue(0); }
    private void RemoveFromShuffleBag(int index) { if (_shuffleBag.Count == 0) return; var remaining = _shuffleBag.Where(i => i != index).ToArray(); _shuffleBag.Clear(); foreach (var i in remaining) _shuffleBag.Enqueue(i); }
    private void ApplyInterval() { var seconds = (int)IntervalSlider.Value; _timer.Stop(); if (_isActive && seconds > 0 && _items.Count > 1) { _timer.Interval = TimeSpan.FromSeconds(seconds); _timer.Start(); } }
    private void Interval_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_syncing || SecondsBox == null) return; _syncing = true; SecondsBox.Text = ((int)e.NewValue).ToString(); _syncing = false; ApplyInterval(); }
    private void SetSeconds(int value) { _syncing = true; value = Math.Clamp(value, 0, 60); IntervalSlider.Value = value; SecondsBox.Text = value.ToString(); _syncing = false; ApplyInterval(); }
    public void SetSecondsFromSynchronization(int value) => SetSeconds(value);
    private void Seconds_LostFocus(object sender, RoutedEventArgs e) => SetSeconds(int.TryParse(SecondsBox.Text, out var n) ? n : 0);
    private void Seconds_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);
    private void Seconds_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Up) { SetSeconds((int)IntervalSlider.Value + 1); e.Handled = true; } else if (e.Key == Key.Down) { SetSeconds((int)IntervalSlider.Value - 1); e.Handled = true; } else if (e.Key == Key.Enter) { SetSeconds(int.TryParse(SecondsBox.Text, out var n) ? n : 0); Keyboard.ClearFocus(); } }
    private void Increase_Click(object sender, RoutedEventArgs e) => SetSeconds((int)IntervalSlider.Value + 1); private void Decrease_Click(object sender, RoutedEventArgs e) => SetSeconds((int)IntervalSlider.Value - 1);
    private void Increase_RightClick(object sender, MouseButtonEventArgs e) { e.Handled = true; SetSeconds((int)IntervalSlider.Value + 1); SynchronizeIntervalRequested?.Invoke(this, (int)IntervalSlider.Value); }
    private void Decrease_RightClick(object sender, MouseButtonEventArgs e) { e.Handled = true; SetSeconds((int)IntervalSlider.Value - 1); SynchronizeIntervalRequested?.Invoke(this, (int)IntervalSlider.Value); }
    private void Slider_RightButtonDown(object sender, MouseButtonEventArgs e) { _rightDraggingSlider = true; IntervalSlider.CaptureMouse(); SetSliderFromRightDrag(e.GetPosition(IntervalSlider).X); e.Handled = true; }
    private void Slider_RightMouseMove(object sender, MouseEventArgs e) { if (_rightDraggingSlider && e.RightButton == MouseButtonState.Pressed) { SetSliderFromRightDrag(e.GetPosition(IntervalSlider).X); e.Handled = true; } }
    private void Slider_RightButtonUp(object sender, MouseButtonEventArgs e) { if (!_rightDraggingSlider) return; SetSliderFromRightDrag(e.GetPosition(IntervalSlider).X); _rightDraggingSlider = false; IntervalSlider.ReleaseMouseCapture(); e.Handled = true; }
    private void SetSliderFromRightDrag(double x) { var ratio = Math.Clamp(x / Math.Max(1, IntervalSlider.ActualWidth), 0, 1); SetSeconds((int)Math.Round(IntervalSlider.Minimum + ratio * (IntervalSlider.Maximum - IntervalSlider.Minimum))); SynchronizeIntervalRequested?.Invoke(this, (int)IntervalSlider.Value); }
    private void Shuffle_Changed(object sender, RoutedEventArgs e) => ResetShuffle();
    private void Shuffle_RightClick(object sender, MouseButtonEventArgs e) { e.Handled = true; ShuffleToggle.IsChecked = ShuffleToggle.IsChecked != true; SynchronizeShuffleRequested?.Invoke(this, ShuffleToggle.IsChecked == true); }
    public void SetShuffleFromSynchronization(bool enabled) => ShuffleToggle.IsChecked = enabled;
    private void Position_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_syncingPosition || _items.Count == 0) return; var selected = Math.Clamp((int)Math.Round(e.NewValue) - 1, 0, _items.Count - 1); if (selected == _index) return; _index = selected; if (ShuffleToggle.IsChecked == true) RemoveFromShuffleBag(_index); ShowCurrent(); }
    private void Next_Click(object sender, RoutedEventArgs e) => Next(); private void Previous_Click(object sender, RoutedEventArgs e) => Previous();
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop(); _animationCancellation?.Cancel(); _animationCancellation?.Dispose(); _animationCancellation = null; _loadVersion++;
        _items.Clear(); _shuffleBag.Clear(); _index = -1; _sourcePath = null; Picture.Source = null;
        EmptyPanel.Visibility = Visibility.Visible; EmptyStatus.Visibility = Visibility.Collapsed; Caption.Visibility = Visibility.Collapsed; ClearButton.Visibility = Visibility.Collapsed; ClearButton.Opacity = 0; ClearButton.IsHitTestVisible = false; BottomBar.Opacity = 0;
        _syncingPosition = true; PositionSlider.Minimum = 1; PositionSlider.Maximum = 1; PositionSlider.Value = 1; PositionText.Text = "0 / 0"; _syncingPosition = false;
    }
    private async void PreviousSource_RightClick(object sender, MouseButtonEventArgs e) { e.Handled = true; await MoveSourceAsync(-1); } private async void NextSource_RightClick(object sender, MouseButtonEventArgs e) { e.Handled = true; await MoveSourceAsync(1); }
    private async Task MoveSourceAsync(int direction)
    {
        if (string.IsNullOrEmpty(_sourcePath)) return; var current = Path.GetFullPath(_sourcePath); var parent = Directory.GetParent(current)?.FullName; if (parent == null || !Directory.Exists(parent)) return;
        var sources = Directory.EnumerateFiles(parent).Where(f => ArchiveExtensions.Contains(Path.GetExtension(f))).OrderBy(NaturalKey, StringComparer.CurrentCultureIgnoreCase).Concat(Directory.EnumerateDirectories(parent).OrderBy(NaturalKey, StringComparer.CurrentCultureIgnoreCase)).ToList();
        var at = sources.FindIndex(p => string.Equals(Path.GetFullPath(p), current, StringComparison.OrdinalIgnoreCase)); if (at < 0 || sources.Count < 2) { MessageBox.Show(direction > 0 ? "다음 폴더나 압축파일이 없습니다." : "이전 폴더나 압축파일이 없습니다.", "Image Viewer", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        for (int step = 1; step < sources.Count; step++)
        {
            var candidateIndex = (at + direction * step % sources.Count + sources.Count) % sources.Count; var candidate = sources[candidateIndex]; var following = sources[(candidateIndex + direction + sources.Count) % sources.Count]; var kind = Directory.Exists(following) ? "폴더" : "ZIP/CBZ"; List<ImageItem> loaded; try { loaded = await Task.Run(() => ReadItems([candidate])); } catch { loaded = []; }
            if (loaded.Count == 0) { ShowSkippingStatus($"{(direction > 0 ? "다음" : "이전")} {kind}로 이동합니다…"); await Dispatcher.Yield(DispatcherPriority.Render); continue; } ApplyLoadedItems([candidate], loaded); return;
        }
        if (_items.Count > 0) { EmptyStatus.Visibility = Visibility.Collapsed; ShowCurrent(); ApplyInterval(); } else { EmptyStatus.Text = "지원되는 이미지를 포함한 다른 폴더나 ZIP/CBZ가 없습니다."; _syncingPosition = true; PositionSlider.Minimum = 1; PositionSlider.Maximum = 1; PositionSlider.Value = 1; PositionText.Text = "0 / 0"; _syncingPosition = false; }
        MessageBox.Show("지원되는 이미지를 포함한 다른 폴더나 압축파일이 없습니다.", "Image Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void Pane_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(this); var y = point.Y; Caption.Opacity = y < 85 ? 1 : 0;
        var showClear = _items.Count > 0 && y < 90 && point.X > ActualWidth - 90;
        ClearButton.Opacity = showClear ? 1 : 0; ClearButton.IsHitTestVisible = showClear;
        if (y > ActualHeight - 115) { BottomBar.Opacity = 1; _hideTimer.Stop(); } else if (BottomBar.Opacity > 0 && !_hideTimer.IsEnabled) _hideTimer.Start();
    }
    private void Pane_MouseLeave(object sender, MouseEventArgs e) { _hideTimer.Stop(); BottomBar.Opacity = 0; Caption.Opacity = 0; ClearButton.Opacity = 0; ClearButton.IsHitTestVisible = false; DropHint.Visibility = Visibility.Collapsed; }
    private void Pane_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IntervalMinLabel == null) return;
        var compact = e.NewSize.Width < 430;
        var narrow = e.NewSize.Width < 300;
        IntervalMinLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        IntervalMaxLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SecondsUnit.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        ShuffleToggle.Content = narrow ? "랜덤" : "무작위";
        ShuffleToggle.Padding = narrow ? new Thickness(4, 6, 4, 6) : new Thickness(10, 6, 10, 6);
        PositionText.MinWidth = narrow ? 44 : 60;
        PositionText.Margin = narrow ? new Thickness(0, 0, 2, 0) : new Thickness(0, 0, 6, 0);
        BottomBar.Padding = narrow ? new Thickness(5) : new Thickness(8);
    }
}
