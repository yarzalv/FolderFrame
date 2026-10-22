using FolderFrame.Core;
using FolderFrame.Services;
using FolderFrame.Util;
using H.Hooks;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using static FolderFrame.Util.Interop;
using Application = System.Windows.Application;
using MouseEventArgs = H.Hooks.MouseEventArgs;
using Point = System.Windows.Point;

namespace FolderFrame
{
    /// <summary>Hidden window that owns the tray icon, the folder controller and the system hooks.</summary>
    public partial class MainWindow : Window
    {
        private const string AutoRunName = "FolderFrame";

        public static FolderController Controller { get; private set; } = null!;

        private SystemLifecycleManager? _lifecycleManager;
        private DispatcherTimer? _watchdog;
        private LowLevelMouseHook? _mouseHook;
        private DateTime _lastDoubleClickTime = DateTime.MinValue;
        private SettingsWindow? _settingsWindow;
        private bool _servicesInitialized;
        private IntPtr _foregroundHook;
        private Interop.WinEventDelegate? _foregroundProc; // kept in a field so the GC can't collect it
        private DispatcherTimer? _foregroundFollowUp;
        private int _foregroundFollowUpTicks;

        public MainWindow()
        {
            AppLogger.Log("[MainWindow] Starting.");
            InitializeComponent();
            Controller = new FolderController();
            new WindowInteropHelper(this).EnsureHandle(); // so OnSourceInitialized runs and services start

            Controller.LoadAll();
            StartOnLoginMenuItem.IsChecked = StartOnLogin;
            if (StartOnLogin)
            {
                // Off the UI thread: schtasks calls would delay the folders appearing at logon.
                // Re-registers the task if the exe was moved.
                string exePath = Environment.ProcessPath!;
                Task.Run(() => Controller.Settings.EnsureAutoRun(AutoRunName, exePath));
            }
            if (DoubleClickToHide) StartMouseHook();
        }

        #region Settings

        public bool StartOnLogin
        {
            get => Controller.Settings.ReadKeyValueRoot("startOnLogin") is bool b && b;
            set
            {
                Controller.Settings.WriteToRegistryRoot("startOnLogin", value.ToString());
                StartOnLoginMenuItem.IsChecked = value;
                if (value) Controller.Settings.AddToAutoRun(AutoRunName, Environment.ProcessPath!);
                else Controller.Settings.RemoveFromAutoRun(AutoRunName);
            }
        }

        public bool DoubleClickToHide
        {
            get => Controller.Settings.ReadKeyValueRoot("DoubleClickToHide") is bool b && b;
            set
            {
                Controller.Settings.WriteToRegistryRoot("DoubleClickToHide", value.ToString());
                if (value) StartMouseHook();
                else StopMouseHook();
            }
        }

        #endregion

        #region Tray menu

        private void AddFolder_Click(object sender, RoutedEventArgs e) => PickAndAddFolder();

        public void PickAndAddFolder()
        {
            using var picker = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Elige la carpeta que quieres en el escritorio",
                UseDescriptionForTitle = true,
            };
            if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                Controller.AddFolder(picker.SelectedPath);
                _settingsWindow?.RefreshList();
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();

        private void TrayIcon_LeftDoubleClick(Wpf.Ui.Tray.Controls.NotifyIcon sender, RoutedEventArgs e) => ShowSettingsWindow();

        private void ToggleVisibility_Click(object sender, RoutedEventArgs e) => ToggleVisibility();

        private void ToggleVisibility()
        {
            Controller.ToggleVisibility();
            bool hidden = Controller.Windows.Count > 0 && !Controller.Windows[0].IsVisible;
            ToggleVisibilityMenuItem.Header = hidden ? "Mostrar carpetas" : "Ocultar carpetas";
        }

        private void StartOnLoginMenuItem_Click(object sender, RoutedEventArgs e)
        {
            StartOnLogin = StartOnLoginMenuItem.IsChecked;
        }

        public void ShowSettingsWindow()
        {
            if (_settingsWindow != null && _settingsWindow.IsLoaded)
            {
                if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow(Controller, this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        /// <summary>Called when the user starts FolderFrame again while it is already running.</summary>
        public void Wakeup()
        {
            AppLogger.Log("[MainWindow] Wakeup.");
            Controller.RefreshWindows();
            ShowSettingsWindow();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Log("[MainWindow] Exit from tray.");
            try
            {
                _watchdog?.Stop();
                _lifecycleManager?.Dispose();
                Controller.SaveAll();
            }
            catch { }
            Application.Current.Shutdown();
        }

        #endregion

        #region Window and services

        private void Window_Initialized(object sender, EventArgs e)
        {
            ShowInTaskbar = false;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Visibility = Visibility.Hidden;
            Left = -500;
            Top = -500;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = (int)GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            InitializeServices();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e) => InitializeServices();

        private void InitializeServices()
        {
            if (_servicesInitialized) return;
            _servicesInitialized = true;

            try { TrayIcon.Register(); }
            catch (Exception ex) { AppLogger.Log($"[MainWindow] Tray icon registration failed: {ex.Message}"); }

            _lifecycleManager = new SystemLifecycleManager(this);
            _lifecycleManager.DesktopRecoveryRequested += reason => Dispatcher.Invoke(() =>
            {
                AppLogger.Log($"[MainWindow] Recovery requested: {reason}");
                try { TrayIcon.Register(); } catch { }
                Controller.RefreshWindows();
            });
            _lifecycleManager.DesktopShellRecreated += () => Dispatcher.Invoke(() =>
            {
                AppLogger.Log("[MainWindow] Explorer restarted.");
                try { TrayIcon.Register(); } catch { }
                Controller.RefreshWindows();
            });
            _lifecycleManager.SystemShuttingDown += () => Dispatcher.Invoke(() => Controller.SaveAll());
            _lifecycleManager.Initialize();

            StartForegroundHook();

            // Cheap health check: recreate a folder window if its handle died
            _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _watchdog.Tick += (_, _) =>
            {
                if (Controller.AnyBusy) return;
                if (Controller.Windows.Any(w => !w.IsHandleAlive)) Controller.RefreshWindows();
            };
            _watchdog.Start();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            e.Cancel = true;
            Hide();
        }

        /// <summary>
        /// Show desktop (Win+D) raises the desktop above every normal window, which hid the
        /// folders. On each foreground change they are put back just above the desktop, and again
        /// shortly after because the shell finishes raising the desktop with an animation.
        /// </summary>
        private void StartForegroundHook()
        {
            _foregroundFollowUp = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _foregroundFollowUp.Tick += (_, _) =>
            {
                Controller.KeepAllAboveDesktop();
                if (++_foregroundFollowUpTicks >= 2) _foregroundFollowUp.Stop();
            };

            _foregroundProc = (_, _, hwnd, _, _, _, _) =>
            {
                if (hwnd == IntPtr.Zero) return;
                Controller.KeepAllAboveDesktop();
                _foregroundFollowUpTicks = 0;
                _foregroundFollowUp.Stop();
                _foregroundFollowUp.Start();
            };
            // Out-of-context hooks are delivered on this (UI) thread through its message loop
            _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundProc,
                0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (_foregroundHook == IntPtr.Zero) AppLogger.Log("[MainWindow] Foreground hook could not be installed.");
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
            _foregroundFollowUp?.Stop();
            _watchdog?.Stop();
            _lifecycleManager?.Dispose();
            StopMouseHook();
            base.OnClosed(e);
        }

        #endregion

        #region Double click on the desktop

        private void StartMouseHook()
        {
            if (_mouseHook != null) return;
            _mouseHook = new LowLevelMouseHook { AddKeyboardKeys = true };
            _mouseHook.DoubleClick += HandleGlobalDoubleClick;
            _mouseHook.Start();
        }

        private void StopMouseHook()
        {
            _mouseHook?.Stop();
            _mouseHook?.Dispose();
            _mouseHook = null;
        }

        private void HandleGlobalDoubleClick(object? sender, MouseEventArgs e)
        {
            if (e.Keys.ToString() != "MouseLeft") return;
            if ((DateTime.Now - _lastDoubleClickTime).TotalSeconds < 0.3) return;
            _lastDoubleClickTime = DateTime.Now;

            var pt = new POINT { X = e.Position.X, Y = e.Position.Y };
            IntPtr underCursor = WindowFromPoint(pt);
            IntPtr desktopList = FindWindowEx(DesktopShellManager.GetDesktopShellView(), IntPtr.Zero, "SysListView32", "FolderView");
            if (underCursor != desktopList || IsDesktopIconHit(pt)) return;
            Application.Current.Dispatcher.Invoke(ToggleVisibility);
        }

        private static bool IsDesktopIconHit(POINT screenPt)
        {
            try
            {
                var element = AutomationElement.FromPoint(new Point(screenPt.X, screenPt.Y));
                return element.Current.ControlType == ControlType.ListItem;
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}
