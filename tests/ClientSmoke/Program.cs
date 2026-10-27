using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Do not show the window: Loaded starts a server connection. Render the
        // view offline and leave the running user's control session untouched.
        var app = new DuoTrack.Client.App();
        app.InitializeComponent();
        var window = new DuoTrack.Client.MainWindow();
        try
        {
            var view = (FrameworkElement)window.Content;
            view.Measure(new Size(864, 791));
            view.Arrange(new Rect(0, 0, 864, 791));
            view.UpdateLayout();
            foreach (var name in new[] { "LayoutLeft", "LayoutRight", "LayoutTop", "LayoutBottom", "EdgeSwitchCheck" })
            {
                if (window.FindName(name) is not FrameworkElement control || control.ActualHeight < 12)
                    throw new Exception($"Missing or collapsed layout control: {name}");
            }
            if (((Button)window.FindName("ControlButton")).IsEnabled)
                throw new Exception("Control should be disabled without an online target");
            var bitmap = new RenderTargetBitmap(864, 791, 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(window.Background, null, new Rect(0, 0, 864, 791));
                context.DrawRectangle(new VisualBrush(view), null, new Rect(0, 0, 864, 791));
            }
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = File.Create(path);
            encoder.Save(output);
            Console.WriteLine($"PASS: offline WPF layout render: {path}");
        }
        finally
        {
            typeof(DuoTrack.Client.MainWindow).GetMethod("ExitApplication", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            app.Shutdown();
        }
    }
}
