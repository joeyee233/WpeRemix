using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpeRemix.Core;

namespace WpeRemix;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && (e.Args[0] == "--snapshot" || e.Args[0] == "--snapshot-result"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var previewWindow = new MainWindow();
            if (e.Args[0] == "--snapshot-result")
            {
                if (e.Args.Length != 3 || new FileInfo(e.Args[2]).Length > 1024 * 1024)
                {
                    throw new ArgumentException("结果预览需要一个不超过 1 MB 的结果 JSON");
                }
                var result = JsonSerializer.Deserialize<ExportResult>(File.ReadAllText(e.Args[2]));
                if (result == null)
                {
                    throw new InvalidDataException("无效的导出结果");
                }
                previewWindow.ShowExportResult(result);
            }
            else if (e.Args.Length > 2)
            {
                await previewWindow.AddInputsAsync(e.Args.Skip(2));
            }
            var content = (FrameworkElement)previewWindow.Content;
            content.Measure(new Size(previewWindow.Width, previewWindow.Height));
            content.Arrange(new Rect(0, 0, previewWindow.Width, previewWindow.Height));
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)previewWindow.Width, (int)previewWindow.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(e.Args[1]))
            {
                encoder.Save(stream);
            }
            Shutdown();
            return;
        }
        if (CliRunner.IsRequested(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int exitCode = await CliRunner.RunAsync(e.Args);
            Shutdown(exitCode);
            return;
        }
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            try
            {
                File.WriteAllText(logPath, exception.ToString());
            }
            catch (IOException)
            {
                logPath = "启动目录不可写";
            }
            catch (UnauthorizedAccessException)
            {
                logPath = "启动目录不可写";
            }
            MessageBox.Show("WpeRemix 无法启动\n" + exception.Message + "\n诊断日志：" + logPath, "WpeRemix", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
