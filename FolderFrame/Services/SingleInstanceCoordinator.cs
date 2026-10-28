using FolderFrame.Util;
using System;
using System.Diagnostics;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace FolderFrame.Services
{
    /// <summary>
    /// Ensures single-instance execution per user session (supporting multiple concurrent RDP users)
    /// and allows newly launched instances to wake up the existing instance instead of failing.
    /// </summary>
    public class SingleInstanceCoordinator : IDisposable
    {
        private Mutex? _mutex;
        private EventWaitHandle? _wakeEvent;
        private CancellationTokenSource? _cts;
        private bool _hasOwnership = false;

        public event Action? InstanceWakeupRequested;

        public bool TryAcquire()
        {
            string sessionIdentifier = $"{Environment.UserName}_{Process.GetCurrentProcess().SessionId}";
            string mutexName = $"Local\\FolderFrame_Mutex_{sessionIdentifier}";
            string eventName = $"Local\\FolderFrame_Wake_{sessionIdentifier}";

            try
            {
                // Open to the whole user, whatever the elevation: a copy started as administrator and
                // one started normally must see each other's mutex, or both would run.
                var user = WindowsIdentity.GetCurrent().User!;
                var mutexSecurity = new MutexSecurity();
                mutexSecurity.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));

                bool createdNew;
                try
                {
                    _mutex = MutexAcl.Create(true, mutexName, out createdNew, mutexSecurity);
                }
                catch (UnauthorizedAccessException)
                {
                    // The name exists but belongs to a copy we may not open: that copy is running
                    AppLogger.Log($"[SingleInstanceCoordinator] Mutex {mutexName} exists and is not accessible; treating it as running.");
                    return false;
                }
                AppLogger.Log($"[SingleInstanceCoordinator] TryAcquire: createdNew={createdNew}, mutex={mutexName}");

                // Safety net for copies that don't share the mutex (older versions, other names):
                // the earliest FolderFrame process of this session keeps running, the rest leave
                if (createdNew && AnotherInstanceIsOlder())
                {
                    AppLogger.Log("[SingleInstanceCoordinator] An older FolderFrame process is already running in this session.");
                    createdNew = false;
                }

                if (createdNew)
                {
                    _hasOwnership = true;
                    var eventSecurity = new EventWaitHandleSecurity();
                    eventSecurity.AddAccessRule(new EventWaitHandleAccessRule(user, EventWaitHandleRights.FullControl, AccessControlType.Allow));
                    _wakeEvent = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, eventName, out _, eventSecurity);
                    _cts = new CancellationTokenSource();

                    // Start background listener for wakeup signals from subsequent launches
                    Task.Run(() => ListenForWakeup(_cts.Token), _cts.Token);
                    return true;
                }
                else
                {
                    // Signal the already running instance to wake up
                    try
                    {
                        if (EventWaitHandle.TryOpenExisting(eventName, out var existingEvent))
                        {
                            AppLogger.Log($"[SingleInstanceCoordinator] Signaling existing event: {eventName}");
                            existingEvent.Set();
                            existingEvent.Dispose();
                        }
                        else
                        {
                            AppLogger.Log($"[SingleInstanceCoordinator] TryOpenExisting returned false for: {eventName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"[SingleInstanceCoordinator] Error signaling event: {ex}");
                    }

                    _mutex.Dispose();
                    _mutex = null;
                    return false;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[SingleInstanceCoordinator] Error acquiring mutex: {ex}");
                // In case of any security / sandbox exception, default to allowing execution
                return true;
            }
        }

        /// <summary>True when another FolderFrame process of this session started before this one.</summary>
        private static bool AnotherInstanceIsOlder()
        {
            try
            {
                using var me = Process.GetCurrentProcess();
                foreach (var other in Process.GetProcessesByName(me.ProcessName))
                {
                    using (other)
                    {
                        if (other.Id == me.Id || other.SessionId != me.SessionId) continue;
                        if (other.StartTime < me.StartTime || (other.StartTime == me.StartTime && other.Id < me.Id)) return true;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[SingleInstanceCoordinator] Process check failed: {ex.Message}");
            }
            return false;
        }

        private void ListenForWakeup(CancellationToken token)
        {
            if (_wakeEvent == null) return;
            AppLogger.Log("[SingleInstanceCoordinator] ListenForWakeup started.");

            var waitHandles = new WaitHandle[] { _wakeEvent, token.WaitHandle };

            while (!token.IsCancellationRequested)
            {
                try
                {
                    int index = WaitHandle.WaitAny(waitHandles);
                    if (index == 0) // Wake event signaled
                    {
                        AppLogger.Log("[SingleInstanceCoordinator] Wake event signaled! Invoking InstanceWakeupRequested.");
                        InstanceWakeupRequested?.Invoke();
                    }
                    else
                    {
                        break;
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[SingleInstanceCoordinator] Wakeup listen error: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();

            if (_hasOwnership && _mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch { }
            }

            _mutex?.Dispose();
            _wakeEvent?.Dispose();
        }
    }
}
