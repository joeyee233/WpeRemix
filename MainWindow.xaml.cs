using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WpeRemix.Core;

namespace WpeRemix;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly IWallpaperProcessor _processor;
    private readonly string _defaultOutputDirectory;
    private CancellationTokenSource? _cancellation;
    private JobItem? _selectedJob;
    private bool _isBusy;
    private bool _isAdding;
    private bool _showDropOverlay;
    private bool _cancelRequested;
    private bool _closeWhenIdle;
    private string _outputDirectory;
    private bool _isOutputDirectoryCustom;
    private BitmapSource? _previewImage;
    private string _previewCaption = string.Empty;

    public MainWindow()
    {
        _processor = new WallpaperProcessor(new SceneComposer());
        _defaultOutputDirectory = KnownFolders.Downloads;
        _outputDirectory = _defaultOutputDirectory;
        LoadSettings();
        InitializeComponent();
        DataContext = this;
        Jobs.CollectionChanged += (_, _) => RefreshQueueState();
        PreviewDragLeave += (_, _) => ShowDropOverlay = false;
        PreviewMouseDown += Window_PreviewMouseDown;
        PreviewKeyDown += CompositionInfo_PreviewKeyDown;
        Deactivated += (_, _) => CompositionInfoCard.Visibility = Visibility.Collapsed;
        LocationChanged += (_, _) => CompositionInfoCard.Visibility = Visibility.Collapsed;
        SizeChanged += (_, _) => CompositionInfoCard.Visibility = Visibility.Collapsed;
        StateChanged += (_, _) => CompositionInfoCard.Visibility = Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!SystemParameters.HighContrast)
        {
            int enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<JobItem> Jobs { get; } = new ObservableCollection<JobItem>();
    public int JobCount => Jobs.Count;
    public bool IsBusy => _isBusy;
    public bool CanEdit => !_isBusy && !_isAdding;
    public bool CanCancel => _isBusy && !_cancelRequested;
    public bool CanStart => CanEdit && Jobs.Any(job => job.CanProcess) && !string.IsNullOrWhiteSpace(OutputDirectory);
    public bool CanUsePrimaryAction
    {
        get
        {
            if (_isBusy)
            {
                return CanCancel;
            }
            return CanStart;
        }
    }
    public bool CanOpenImage => SelectedJob != null && SelectedJob.HasResult && !string.IsNullOrWhiteSpace(SelectedJob.ResultImage) && File.Exists(SelectedJob.ResultImage);
    public bool HasSelectedJob => SelectedJob != null;
    public bool HasSelectedReport => !string.IsNullOrWhiteSpace(SelectedDetail);
    public bool HasPreviewCaption => !string.IsNullOrWhiteSpace(PreviewCaption);
    public bool CanOpenSelectedPath => FindContainingDirectory(SelectedPath) != null;
    public string SelectedPath
    {
        get
        {
            if (SelectedJob == null)
            {
                return string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(SelectedJob.OutputDirectory))
            {
                return SelectedJob.OutputDirectory;
            }
            return SelectedJob.SourcePath;
        }
    }
    public string StartLabel
    {
        get
        {
            if (_isAdding)
            {
                return "正在读取…";
            }
            if (_cancelRequested && _isBusy)
            {
                return "正在取消…";
            }
            if (_isBusy)
            {
                return "取消处理";
            }
            int count = Jobs.Count(job => job.CanProcess);
            if (count == 0)
            {
                return "开始处理";
            }
            return "开始处理 x" + count;
        }
    }

    public JobItem? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (_selectedJob == value)
            {
                return;
            }
            _selectedJob = value;
            OnPropertyChanged();
            RefreshPreview();
        }
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (string.Equals(_outputDirectory, value, StringComparison.Ordinal))
            {
                return;
            }
            _outputDirectory = value;
            _isOutputDirectoryCustom = !SameDirectory(value, _defaultOutputDirectory);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanUsePrimaryAction));
        }
    }

    public bool ShowDropOverlay
    {
        get => _showDropOverlay;
        set
        {
            _showDropOverlay = value;
            OnPropertyChanged();
        }
    }

    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set
        {
            _previewImage = value;
            OnPropertyChanged();
        }
    }

    public string PreviewCaption
    {
        get => _previewCaption;
        private set
        {
            _previewCaption = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPreviewCaption));
        }
    }

    public string PreviewTitle
    {
        get
        {
            if (SelectedJob == null)
            {
                return string.Empty;
            }
            return SelectedJob.Name;
        }
    }

    public string PreviewPlaceholder
    {
        get
        {
            if (SelectedJob == null)
            {
                return "一张好画面，值得留下";
            }
            if (SelectedJob.Status == "已提取")
            {
                return "素材已提取";
            }
            if (SelectedJob.Status == "失败")
            {
                return "这张壁纸暂未处理成功";
            }
            return "等待合成画面";
        }
    }

    public string SelectedDetail
    {
        get
        {
            if (SelectedJob == null)
            {
                return string.Empty;
            }
            if (SelectedJob.IsRunning)
            {
                return SelectedJob.Detail;
            }
            return SelectedJob.FullDetail;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void RefreshQueueState()
    {
        OnPropertyChanged(nameof(JobCount));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanUsePrimaryAction));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanCancel));
    }

    private void RefreshPreview()
    {
        PreviewImage = null;
        PreviewCaption = string.Empty;
        if (SelectedJob != null)
        {
            if (CanOpenImage)
            {
                try
                {
                    using (FileStream stream = File.OpenRead(SelectedJob.ResultImage!))
                    {
                        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                        int width = decoder.Frames[0].PixelWidth;
                        int height = decoder.Frames[0].PixelHeight;
                        PreviewCaption = width + " × " + height + "  ·  PNG  ·  " + FormatSize(new FileInfo(SelectedJob.ResultImage!).Length);
                    }
                    BitmapImage image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 1200;
                    image.UriSource = new Uri(SelectedJob.ResultImage!, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                    PreviewImage = image;
                }
                catch (Exception exception) when (exception is IOException || exception is NotSupportedException || exception is ArgumentException || exception is FileFormatException || exception is COMException)
                {
                    PreviewCaption = "预览不可用，可在文件夹中查看图片";
                }
            }
        }
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(PreviewPlaceholder));
        OnPropertyChanged(nameof(SelectedDetail));
        OnPropertyChanged(nameof(CanOpenImage));
        OnPropertyChanged(nameof(HasSelectedJob));
        OnPropertyChanged(nameof(HasSelectedReport));
        OnPropertyChanged(nameof(SelectedPath));
        OnPropertyChanged(nameof(CanOpenSelectedPath));
    }

    private static string FormatSize(long length)
    {
        if (length >= 1024 * 1024)
        {
            return (length / 1024.0 / 1024.0).ToString("0.0") + " MB";
        }
        return (length / 1024.0).ToString("0") + " KB";
    }

    private void PreviewLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double detailHeight = 0;
        double actionHeight = 0;
        if (PreviewDetails.Visibility == Visibility.Visible)
        {
            detailHeight = PreviewDetails.ActualHeight + PreviewDetails.Margin.Top;
        }
        if (PreviewActions.Visibility == Visibility.Visible)
        {
            actionHeight = PreviewActions.ActualHeight + PreviewActions.Margin.Top;
        }
        PreviewResultImage.MaxHeight = Math.Max(140, PreviewViewport.ActualHeight - detailHeight - actionHeight);
    }

    private void CompositionInfo_Click(object sender, RoutedEventArgs e)
    {
        if (CompositionInfoCard.Visibility == Visibility.Visible)
        {
            CompositionInfoCard.Visibility = Visibility.Collapsed;
            CompositionInfoButton.Focus();
        }
        else
        {
            CompositionInfoCard.Visibility = Visibility.Visible;
            CompositionInfoCard.UpdateLayout();
            CompositionInfoButton.Focus();
        }
    }

    private void CompositionInfo_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && CompositionInfoCard.Visibility == Visibility.Visible)
        {
            CompositionInfoCard.Visibility = Visibility.Collapsed;
            CompositionInfoButton.Focus();
            e.Handled = true;
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (CompositionInfoCard.Visibility != Visibility.Visible)
        {
            return;
        }
        if (IsWithin(e.OriginalSource as DependencyObject, CompositionInfoButton) || IsWithin(e.OriginalSource as DependencyObject, CompositionInfoCard))
        {
            return;
        }
        CompositionInfoCard.Visibility = Visibility.Collapsed;
    }

    private static bool IsWithin(DependencyObject? current, DependencyObject root)
    {
        while (current != null)
        {
            if (current == root)
            {
                return true;
            }
            if (current is Visual)
            {
                current = VisualTreeHelper.GetParent(current);
            }
            else
            {
                current = LogicalTreeHelper.GetParent(current);
            }
        }
        return false;
    }

    internal async Task AddInputsAsync(IEnumerable<string> paths)
    {
        if (!CanEdit)
        {
            return;
        }
        _isAdding = true;
        RefreshQueueState();
        try
        {
            string[] candidates = paths.ToArray();
            foreach (string candidate in candidates)
            {
                try
                {
                    IReadOnlyList<string> discovered = await Task.Run(() => InputDiscovery.Discover(new[] { candidate }));
                    if (discovered.Count == 0)
                    {
                        throw new InvalidDataException("没有找到支持的壁纸资源");
                    }
                    foreach (string path in discovered)
                    {
                        string fullPath = Path.GetFullPath(path);
                        if (Jobs.Any(job => string.Equals(job.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                        JobItem item = new JobItem(fullPath, ReadDisplayName(fullPath));
                        Jobs.Add(item);
                    }
                }
                catch (Exception exception)
                {
                    var item = new JobItem(candidate, candidate);
                    item.Fail("添加失败：" + exception.Message);
                    Jobs.Add(item);
                }
            }
            if (SelectedJob == null && Jobs.Count > 0)
            {
                SelectedJob = Jobs[0];
            }
        }
        finally
        {
            _isAdding = false;
            RefreshQueueState();
            CloseIfRequested();
        }
    }

    private static string ReadDisplayName(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (Directory.Exists(path))
            {
                directory = path;
            }
            if (directory != null)
            {
                string metadataPath = Path.Combine(directory, "project.json");
                if (File.Exists(metadataPath) && new FileInfo(metadataPath).Length < 1024 * 1024)
                {
                    using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
                    if (metadata.RootElement.TryGetProperty("title", out JsonElement title) && title.ValueKind == JsonValueKind.String)
                    {
                        string? text = title.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is JsonException)
        {
            // Missing or invalid metadata must not prevent importing the resource.
        }
        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit)
        {
            return;
        }
        try
        {
            IReadOnlyList<string> paths = InputPicker.Show(this, WallpaperLocations.InputDirectory);
            if (paths.Count > 0)
            {
                await AddInputsAsync(paths);
            }
        }
        catch (Exception exception) when (exception is COMException || exception is IOException || exception is UnauthorizedAccessException || exception is InvalidOperationException)
        {
            MessageBox.Show(this, "无法选择资源：" + exception.Message, "WpeRemix", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void QueuePanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!CanEdit || e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current != null && current != QueuePanel)
        {
            if (current is ButtonBase || current is ListBoxItem || current is ScrollBar)
            {
                return;
            }
            if (current is Visual)
            {
                current = VisualTreeHelper.GetParent(current);
            }
            else
            {
                current = LogicalTreeHelper.GetParent(current);
            }
        }
        if (current == QueuePanel)
        {
            e.Handled = true;
            AddFolder_Click(sender, e);
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit)
        {
            return;
        }
        OpenFolderDialog dialog = new OpenFolderDialog
        {
            Title = "选择一个或多个壁纸文件夹",
            InitialDirectory = WallpaperLocations.InputDirectory,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await AddInputsAsync(dialog.FolderNames);
        }
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new OpenFolderDialog { Title = "选择输出文件夹" };
        if (Directory.Exists(OutputDirectory))
        {
            dialog.InitialDirectory = OutputDirectory;
        }
        if (dialog.ShowDialog(this) == true)
        {
            OutputDirectory = dialog.FolderName;
            SaveSettings();
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        ShowDropOverlay = CanEdit && e.Data.GetDataPresent(DataFormats.FileDrop);
        if (ShowDropOverlay)
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        ShowDropOverlay = false;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            await AddInputsAsync(paths);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (CanEdit)
        {
            SelectedJob = null;
            Jobs.Clear();
        }
    }

    private void RemoveJob_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit || sender is not FrameworkElement element || element.DataContext is not JobItem item)
        {
            return;
        }
        int index = Jobs.IndexOf(item);
        if (index < 0)
        {
            return;
        }
        bool selected = SelectedJob == item;
        if (selected)
        {
            SelectedJob = null;
        }
        Jobs.RemoveAt(index);
        if (selected && Jobs.Count > 0)
        {
            SelectedJob = Jobs[Math.Min(index, Jobs.Count - 1)];
        }
        e.Handled = true;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            Cancel_Click(sender, e);
            return;
        }
        if (!CanStart)
        {
            return;
        }
        string outputPath;
        try
        {
            outputPath = Path.GetFullPath(OutputDirectory.Trim());
            Directory.CreateDirectory(outputPath);
        }
        catch (Exception exception)
        {
            foreach (JobItem job in Jobs.Where(item => item.CanProcess))
            {
                job.Fail("输出目录不可用：" + exception.Message);
            }
            RefreshPreview();
            RefreshQueueState();
            return;
        }
        OutputDirectory = outputPath;
        SaveSettings();
        JobItem[] batch = Jobs.Where(job => job.CanProcess).ToArray();
        ExportOptions options = new ExportOptions(outputPath, ExportMode.CompositeAndExtract);
        _isBusy = true;
        _cancelRequested = false;
        _cancellation = new CancellationTokenSource();
        RefreshQueueState();
        try
        {
            for (int index = 0; index < batch.Length; index++)
            {
                if (_cancellation.IsCancellationRequested)
                {
                    break;
                }
                JobItem job = batch[index];
                SelectedJob = job;
                job.Begin();
                RefreshPreview();
                Progress<ExportProgress> progress = new Progress<ExportProgress>(update =>
                {
                    if (!job.IsRunning)
                    {
                        return;
                    }
                    job.Progress = update.Fraction;
                    job.Detail = update.Stage;
                    if (SelectedJob == job)
                    {
                        OnPropertyChanged(nameof(SelectedDetail));
                        OnPropertyChanged(nameof(HasSelectedReport));
                    }
                });
                try
                {
                    ExportResult result = await _processor.ProcessAsync(job.SourcePath, options, progress, _cancellation.Token);
                    job.Complete(result);
                }
                catch (OperationCanceledException)
                {
                    job.Cancel();
                    break;
                }
                catch (Exception exception)
                {
                    job.Fail(exception.Message);
                }
                if (SelectedJob == job)
                {
                    RefreshPreview();
                }
            }
        }
        finally
        {
            _isBusy = false;
            _cancellation.Dispose();
            _cancellation = null;
            RefreshQueueState();
            RefreshPreview();
            CloseIfRequested();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (CanCancel && _cancellation != null)
        {
            _cancelRequested = true;
            _cancellation.Cancel();
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanUsePrimaryAction));
            OnPropertyChanged(nameof(StartLabel));
        }
    }

    internal void ShowExportResult(ExportResult result)
    {
        var item = new JobItem(result.OutputDirectory, Path.GetFileName(result.OutputDirectory));
        item.Complete(result);
        Jobs.Add(item);
        SelectedJob = item;
    }

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        if (CanOpenImage && SelectedJob != null)
        {
            OpenPath(SelectedJob.ResultImage!);
        }
    }

    private void OpenSelectedPath_Click(object sender, RoutedEventArgs e)
    {
        string? directory = FindContainingDirectory(SelectedPath);
        if (directory == null)
        {
            return;
        }
        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(directory);
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "无法打开文件夹：" + exception.Message, "WpeRemix", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? FindContainingDirectory(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                return null;
            }
            if (Directory.Exists(path))
            {
                return path;
            }
            string? directory = Path.GetDirectoryName(path);
            if (directory != null && Directory.Exists(directory))
            {
                return directory;
            }
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        {
            return null;
        }
        return null;
    }

    private void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "无法打开：" + exception.Message, "WpeRemix", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        CompositionInfoCard.Visibility = Visibility.Collapsed;
        if (!IsLoaded)
        {
            return;
        }
        if (_isBusy || _isAdding)
        {
            e.Cancel = true;
            _closeWhenIdle = true;
            if (_cancellation != null)
            {
                Cancel_Click(this, new RoutedEventArgs());
            }
            return;
        }
        SaveSettings();
    }

    private void CloseIfRequested()
    {
        if (_closeWhenIdle && !_isBusy && !_isAdding)
        {
            Close();
        }
    }

    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpeRemix", "settings.json");

    private void LoadSettings()
    {
        try
        {
            string pathToRead = SettingsPath;
            if (!File.Exists(pathToRead))
            {
                pathToRead = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Werip", "settings.json");
            }
            if (!File.Exists(pathToRead))
            {
                pathToRead = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WPE Rip", "settings.json");
            }
            if (!File.Exists(pathToRead))
            {
                return;
            }
            if (new FileInfo(pathToRead).Length > 65536)
            {
                return;
            }
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(pathToRead));
            if (document.RootElement.TryGetProperty("OutputDirectory", out JsonElement path))
            {
                string? savedPath = path.GetString();
                if (!string.IsNullOrWhiteSpace(savedPath) && Path.IsPathFullyQualified(savedPath))
                {
                    int version = 1;
                    if (document.RootElement.TryGetProperty("SettingsVersion", out JsonElement savedVersion))
                    {
                        savedVersion.TryGetInt32(out version);
                    }
                    if (version >= 2)
                    {
                        if (!document.RootElement.TryGetProperty("IsOutputDirectoryCustom", out JsonElement custom) || custom.ValueKind != JsonValueKind.True)
                        {
                            return;
                        }
                    }
                    else
                    {
                        string legacyDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WPE Rip");
                        string renamedDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Werip");
                        if (SameDirectory(savedPath, legacyDefault) || SameDirectory(savedPath, renamedDefault))
                        {
                            return;
                        }
                    }
                    _outputDirectory = savedPath;
                    _isOutputDirectoryCustom = !SameDirectory(savedPath, _defaultOutputDirectory);
                }
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is JsonException || exception is InvalidOperationException)
        {
            // A damaged settings file does not prevent launching the application.
        }
    }

    private void SaveSettings()
    {
        try
        {
            string? directory = Path.GetDirectoryName(SettingsPath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
                string content = JsonSerializer.Serialize(new SavedSettings(OutputDirectory, 2, _isOutputDirectoryCustom), new JsonSerializerOptions { WriteIndented = true });
                string temporaryPath = SettingsPath + ".tmp";
                File.WriteAllText(temporaryPath, content);
                File.Move(temporaryPath, SettingsPath, true);
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            MessageBox.Show(this, "输出路径已应用，但无法保存偏好设置：" + exception.Message, "WpeRemix", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool SameDirectory(string first, string second)
    {
        try
        {
            return Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        {
            return false;
        }
    }

    private sealed record SavedSettings(string OutputDirectory, int SettingsVersion, bool IsOutputDirectoryCustom);
}

public sealed class JobItem : INotifyPropertyChanged
{
    private string _status = "等待处理";
    private string _detail;
    private double _progress;
    private bool _isRunning;

    public JobItem(string sourcePath, string name)
    {
        SourcePath = sourcePath;
        Name = name;
        _detail = Path.GetFileName(sourcePath);
        FullDetail = string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string SourcePath { get; }
    public string Name { get; }
    public string? OutputDirectory { get; private set; }
    public string? ResultImage { get; private set; }
    public string FullDetail { get; private set; }
    public bool CanProcess => _status == "等待处理" || _status == "失败" || _status == "已取消";
    public bool HasResult => _status == "已完成" || _status == "已合成" || _status == "已提取";
    public string Status => _status;
    public bool IsRunning => _isRunning;

    public string Detail
    {
        get => _detail;
        set
        {
            _detail = value;
            OnPropertyChanged();
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            _progress = Math.Clamp(value, 0, 1);
            OnPropertyChanged();
        }
    }

    public void Begin()
    {
        _isRunning = true;
        _status = "处理中";
        OutputDirectory = null;
        ResultImage = null;
        Progress = 0;
        Detail = "准备解析资源";
        FullDetail = string.Empty;
        NotifyState();
    }

    public void Complete(ExportResult result)
    {
        _isRunning = false;
        Progress = 1;
        OutputDirectory = result.OutputDirectory;
        ResultImage = result.ImagePath;
        if (result.ImagePath == null)
        {
            _status = "已提取";
            Detail = "素材图 x" + result.AssetCount;
        }
        else
        {
            _status = "已完成";
            Detail = "PNG x1，素材图 x" + result.AssetCount;
        }
        IReadOnlyList<string> specificWarnings = CompositionNotices.GetSpecificWarnings(result.Warnings);
        FullDetail = string.Join(Environment.NewLine, specificWarnings);
        if (specificWarnings.Count > 0)
        {
            Detail += " · " + specificWarnings.Count + " 条提示";
        }
        NotifyState();
    }

    public void Fail(string message)
    {
        _isRunning = false;
        _status = "失败";
        int lineEnd = message.IndexOfAny(new[] { '\r', '\n' });
        if (lineEnd >= 0)
        {
            Detail = message[..lineEnd];
        }
        else
        {
            Detail = message;
        }
        FullDetail = message;
        NotifyState();
    }

    public void Cancel()
    {
        _isRunning = false;
        _status = "已取消";
        Detail = "点击开始处理可重试";
        FullDetail = "当前任务已取消，已完成的其他任务仍然保留";
        NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanProcess));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(FullDetail));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
