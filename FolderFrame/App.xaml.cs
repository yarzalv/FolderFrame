using FolderFrame.Services;
using FolderFrame.Util;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace FolderFrame
{
    public partial class App : Application
    {
        private SingleInstanceCoordinator? _singleInstanceCoordinator;

        protected override void OnStartup(StartupEventArgs e)
        {
            AppLogger.Log($"[App] Starting. PID={Environment.ProcessId}");

            // The app lives in the tray; closing a window must never end it
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                LogException(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogException(args.Exception, "TaskScheduler.UnobservedTaskException");
                args.SetObserved();
            };

            // One instance per user session; starting it again just shows the settings
            _singleInstanceCoordinator = new SingleInstanceCoordinator();
            _singleInstanceCoordinator.InstanceWakeupRequested += () =>
                Current.Dispatcher.Invoke(() => (Current.MainWindow as MainWindow)?.Wakeup());
            if (!_singleInstanceCoordinator.TryAcquire())
            {
                AppLogger.Log("[App] Already running; signaled the running instance.");
                Shutdown();
                return;
            }

            // Ask Windows to restart FolderFrame after a crash or an update reboot
            try { Interop.RegisterApplicationRestart(null, 0); } catch { }

            // SVG files in a folder are untrusted: drawing a thumbnail must not read local files
            // or reach the network through XML entities (XXE), external images or <use> references.
            Svg.SvgDocument.ResolveExternalXmlEntites = Svg.ExternalType.None;
            Svg.SvgDocument.ResolveExternalImages = Svg.ExternalType.None;
            Svg.SvgDocument.ResolveExternalElements = Svg.ExternalType.None;

            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Critical;
            base.OnStartup(e);

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLogger.Log($"[App] Exit. Code={e.ApplicationExitCode}");
            try { _singleInstanceCoordinator?.Dispose(); } catch { }
            base.OnExit(e);
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogException(e.Exception, "DispatcherUnhandledException");
            // Keep running: a failing click handler must not take every folder off the desktop
            e.Handled = true;
        }

        private static void LogException(Exception? ex, string source)
        {
            if (ex == null) return;
            AppLogger.Log($"[App] {source}: {ex}");
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FolderFrame");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
