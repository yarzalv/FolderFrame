using FolderFrame.Util;
using System;
using System.Diagnostics;
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
                _mutex = new Mutex(true, mutexName, out bool createdNew);
                AppLogger.Log($"[SingleInstanceCoordinator] TryAcquire: createdNew={createdNew}, mutex={mutexName}");
                if (createdNew)
                {
                    _hasOwnership = true;
                    _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
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
