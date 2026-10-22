using FolderFrame.Util;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FolderFrame.Services
{
    /// <summary>
    /// Coordinates system lifecycle events including Remote Desktop sessions, power sleep/wake,
    /// display resolution changes, Explorer restarts, and system shutdown.
    /// </summary>
    public class SystemLifecycleManager : IDisposable
    {
        private readonly Window _hostWindow;
        private IntPtr _hwnd = IntPtr.Zero;
        private uint _taskbarRestartMessage;
        private bool _isSessionNotificationRegistered = false;
        private bool _disposed = false;

        private readonly DispatcherTimer _debounceTimer;
        private readonly DispatcherTimer _stage2Timer;
        private readonly DispatcherTimer _stage3Timer;

        public event Action<string>? DesktopRecoveryRequested;
        public event Action? DesktopShellRecreated;
        public event Action? SystemShuttingDown;

        public SystemLifecycleManager(Window hostWindow)
        {
            _hostWindow = hostWindow ?? throw new ArgumentNullException(nameof(hostWindow));

            _debounceTimer = new DispatcherTimer(DispatcherPriority.Normal, hostWindow.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer.Stop();
                DesktopRecoveryRequested?.Invoke("DebouncedEvent");
                _stage2Timer.Start();
            };

            _stage2Timer = new DispatcherTimer(DispatcherPriority.Background, hostWindow.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _stage2Timer.Tick += (s, e) =>
            {
                _stage2Timer.Stop();
                DesktopRecoveryRequested?.Invoke("Stage2Verification");
                _stage3Timer.Start();
            };

            _stage3Timer = new DispatcherTimer(DispatcherPriority.Background, hostWindow.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(2000)
            };
            _stage3Timer.Tick += (s, e) =>
            {
                _stage3Timer.Stop();
                DesktopRecoveryRequested?.Invoke("Stage3Verification");
            };
        }

        public void Initialize()
        {
            var interopHelper = new WindowInteropHelper(_hostWindow);
            interopHelper.EnsureHandle();
            _hwnd = interopHelper.Handle;

            // Register for Explorer restart
            _taskbarRestartMessage = Interop.RegisterWindowMessage("TaskbarCreated");

            // Register for Terminal Services session changes (Remote Desktop)
            try
            {
                _isSessionNotificationRegistered = Interop.WTSRegisterSessionNotification(_hwnd, Interop.NOTIFY_FOR_THIS_SESSION);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WTSRegisterSessionNotification failed: {ex.Message}");
            }

            // Hook Win32 window message loop
            var hwndSource = HwndSource.FromHwnd(_hwnd);
            hwndSource?.AddHook(WndProc);

            // Subscribe to .NET SystemEvents
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.SessionEnding += OnSessionEnding;
        }

        private void TriggerStagedRecovery(string reason)
        {
            Debug.WriteLine($"[SystemLifecycleManager] Triggering recovery due to: {reason}");
            DesktopShellManager.InvalidateCache();

            _stage2Timer.Stop();
            _stage3Timer.Stop();
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                Debug.WriteLine("[SystemLifecycleManager] System suspending. Saving state.");
                SystemShuttingDown?.Invoke();
            }
            else if (e.Mode == PowerModes.Resume)
            {
                Debug.WriteLine("[SystemLifecycleManager] System resumed from suspend.");
                TriggerStagedRecovery("PowerModes.Resume");
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            Debug.WriteLine($"[SystemLifecycleManager] SessionSwitch: {e.Reason}");
            switch (e.Reason)
            {
                case SessionSwitchReason.RemoteConnect:
                case SessionSwitchReason.RemoteDisconnect:
                case SessionSwitchReason.ConsoleConnect:
                case SessionSwitchReason.ConsoleDisconnect:
                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.SessionLogon:
                    TriggerStagedRecovery($"SessionSwitch.{e.Reason}");
                    break;
                case SessionSwitchReason.SessionLock:
                    // Save state on lock
                    SystemShuttingDown?.Invoke();
                    break;
            }
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            Debug.WriteLine("[SystemLifecycleManager] SystemEvents.DisplaySettingsChanged");
            TriggerStagedRecovery("DisplaySettingsChanged");
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            Debug.WriteLine($"[SystemLifecycleManager] SystemEvents.SessionEnding (Reason: {e.Reason})");
            SystemShuttingDown?.Invoke();
        }

        public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == _taskbarRestartMessage && _taskbarRestartMessage != 0)
            {
                Debug.WriteLine("[SystemLifecycleManager] TaskbarCreated message received (Explorer restarted)");
                DesktopShellManager.InvalidateCache();
                DesktopShellRecreated?.Invoke();
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case Interop.WM_WTSSESSION_CHANGE:
                    int wtsReason = wParam.ToInt32();
                    Debug.WriteLine($"[SystemLifecycleManager] WM_WTSSESSION_CHANGE: {wtsReason}");
                    if (wtsReason == Interop.WTS_REMOTE_CONNECT ||
                        wtsReason == Interop.WTS_REMOTE_DISCONNECT ||
                        wtsReason == Interop.WTS_CONSOLE_CONNECT ||
                        wtsReason == Interop.WTS_CONSOLE_DISCONNECT ||
                        wtsReason == Interop.WTS_SESSION_UNLOCK ||
                        wtsReason == Interop.WTS_SESSION_LOGON)
                    {
                        TriggerStagedRecovery($"WM_WTSSESSION_CHANGE_{wtsReason}");
                    }
                    break;

                case Interop.WM_DISPLAYCHANGE:
                    Debug.WriteLine("[SystemLifecycleManager] WM_DISPLAYCHANGE received");
                    TriggerStagedRecovery("WM_DISPLAYCHANGE");
                    break;

                case Interop.WM_POWERBROADCAST:
                    int powerEvent = wParam.ToInt32();
                    Debug.WriteLine($"[SystemLifecycleManager] WM_POWERBROADCAST: {powerEvent}");
                    if (powerEvent == Interop.PBT_APMRESUMEAUTOMATIC ||
                        powerEvent == Interop.PBT_APMRESUMESUSPEND)
                    {
                        TriggerStagedRecovery($"WM_POWERBROADCAST_RESUME_{powerEvent}");
                    }
                    else if (powerEvent == Interop.PBT_APMSUSPEND)
                    {
                        SystemShuttingDown?.Invoke();
                    }
                    break;

                case Interop.WM_QUERYENDSESSION:
                case Interop.WM_ENDSESSION:
                    Debug.WriteLine("[SystemLifecycleManager] WM_QUERYENDSESSION / WM_ENDSESSION received");
                    SystemShuttingDown?.Invoke();
                    handled = true;
                    return (IntPtr)1; // Indicate ready to end session cleanly
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _debounceTimer.Stop();
            _stage2Timer.Stop();
            _stage3Timer.Stop();

            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;

            if (_isSessionNotificationRegistered && _hwnd != IntPtr.Zero)
            {
                try
                {
                    Interop.WTSUnRegisterSessionNotification(_hwnd);
                }
                catch { }
                _isSessionNotificationRegistered = false;
            }
        }
    }
}
