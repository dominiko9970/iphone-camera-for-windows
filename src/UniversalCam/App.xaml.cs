using System.IO;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace UniversalCam;

public partial class App : System.Windows.Application
{
    private static Mutex? _instanceMutex;
    private static bool _ownsMutex;
    private static StreamWriter? _logWriter;
    internal static WinForms.NotifyIcon? TrayIcon { get; private set; }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, "UniversalCam_SingleInstance", out bool isNew);
        _ownsMutex = isNew;
        if (!isNew)
        {
            MessageBox.Show("UniversalCam is already running.", "UniversalCam",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Tee Console output to logs/run_latest.txt so debugger sessions also produce a log.
        // Walk up from BaseDirectory until we find the workspace root (contains the .sln).
        try
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && dir.GetFiles("*.sln").Length == 0)
                dir = dir.Parent;
            var logsDir = dir != null
                ? Path.Combine(dir.FullName, "logs")
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logsDir);
            var logPath = Path.GetFullPath(Path.Combine(logsDir, "run_latest.txt"));
            _logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
            Console.SetOut(new TeeTextWriter(Console.Out, _logWriter));
            Console.SetError(new TeeTextWriter(Console.Error, _logWriter));
            Console.WriteLine($"[App] Log started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}  path={logPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[App] Could not open log file: {ex.Message}");
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Console.WriteLine($"[FATAL] UI thread exception (handled): {ex.Exception}");
            ex.Handled = true; // keep UI alive so we can see what's failing
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Console.WriteLine($"[FATAL] background thread: {ex.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Console.WriteLine($"[FATAL] unobserved task: {ex.Exception}");
            ex.SetObserved();
        };
        // System tray icon — lets the user hide/show the window and quit cleanly
        TrayIcon = BuildTrayIcon();

        base.OnStartup(e);
    }

    private static WinForms.NotifyIcon BuildTrayIcon()
    {
        var iconStream = Application.GetResourceStream(
            new Uri("pack://application:,,,/universal_cam.ico"))?.Stream;

        var tray = new WinForms.NotifyIcon
        {
            Text    = "UniversalCam",
            Icon    = iconStream != null
                ? new System.Drawing.Icon(iconStream)
                : System.Drawing.SystemIcons.Application,
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open UniversalCam", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) =>
        {
            tray.Visible = false;
            if (Application.Current.MainWindow is Views.MainWindow w)
                w.ForceClose();
            else
                Application.Current.Shutdown();
        });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowMainWindow();
        return tray;
    }

    private static void ShowMainWindow()
    {
        var win = System.Windows.Application.Current.MainWindow;
        if (win is null) return;
        win.Show();
        win.WindowState = System.Windows.WindowState.Normal;
        win.Activate();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        TrayIcon?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _logWriter?.Flush();
        _logWriter?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Writes to two TextWriters simultaneously — used to tee Console to a log file.</summary>
file sealed class TeeTextWriter(TextWriter primary, TextWriter secondary) : TextWriter
{
    public override System.Text.Encoding Encoding => primary.Encoding;
    public override void Write(char value)           { primary.Write(value);           secondary.Write(value); }
    public override void Write(string? value)        { primary.Write(value);           secondary.Write(value); }
    public override void WriteLine(string? value)    { primary.WriteLine(value);       secondary.WriteLine(value); }
    public override void Flush()                     { primary.Flush();                secondary.Flush(); }
    protected override void Dispose(bool disposing)  { if (disposing) { primary.Dispose(); secondary.Dispose(); } base.Dispose(disposing); }
}
