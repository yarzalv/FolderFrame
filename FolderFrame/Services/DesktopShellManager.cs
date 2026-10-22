using FolderFrame.Util;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FolderFrame.Services
{
    /// <summary>
    /// Centralized manager for locating, validating, and interacting with the Windows desktop shell view (SHELLDLL_DefView).
    /// </summary>
    public static class DesktopShellManager
    {
        private static IntPtr _cachedShellView = IntPtr.Zero;

        /// <summary>
        /// Locates the active SHELLDLL_DefView window handle across Progman and WorkerW hierarchies.
        /// </summary>
        public static IntPtr GetDesktopShellView(bool forceRefresh = false)
        {
            if (!forceRefresh && _cachedShellView != IntPtr.Zero && Interop.IsWindow(_cachedShellView))
            {
                return _cachedShellView;
            }

            IntPtr shellView = IntPtr.Zero;

            // Strategy 1: Check Progman -> SHELLDLL_DefView
            try
            {
                IntPtr progman = Interop.FindWindow("Progman", null!);
                if (progman != IntPtr.Zero)
                {
                    shellView = Interop.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null!);
                }
            }
            catch { }

            // Strategy 2: Check WorkerW windows -> SHELLDLL_DefView
            if (shellView == IntPtr.Zero || !Interop.IsWindow(shellView))
            {
                try
                {
                    IntPtr workerw = IntPtr.Zero;
                    do
                    {
                        workerw = Interop.FindWindowEx(IntPtr.Zero, workerw, "WorkerW", null!);
                        if (workerw != IntPtr.Zero)
                        {
                            IntPtr candidate = Interop.FindWindowEx(workerw, IntPtr.Zero, "SHELLDLL_DefView", null!);
                            if (candidate != IntPtr.Zero)
                            {
                                shellView = candidate;
                                break;
                            }
                        }
                    } while (workerw != IntPtr.Zero);
                }
                catch { }
            }

            // Strategy 3: EnumWindows fallback across all top-level windows
            if (shellView == IntPtr.Zero || !Interop.IsWindow(shellView))
            {
                try
                {
                    Interop.EnumWindows((topHandle, _) =>
                    {
                        IntPtr candidate = Interop.FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null!);
                        if (candidate != IntPtr.Zero && Interop.IsWindow(candidate))
                        {
                            shellView = candidate;
                            return false; // Stop enumeration
                        }
                        return true;
                    }, IntPtr.Zero);
                }
                catch { }
            }

            if (shellView != IntPtr.Zero && Interop.IsWindow(shellView))
            {
                _cachedShellView = shellView;
                return shellView;
            }

            _cachedShellView = IntPtr.Zero;
            return IntPtr.Zero;
        }

        public static bool IsDesktopShellAlive()
        {
            return _cachedShellView != IntPtr.Zero && Interop.IsWindow(_cachedShellView);
        }

        public static void InvalidateCache()
        {
            _cachedShellView = IntPtr.Zero;
        }

        /// <summary>
        /// Non-blocking asynchronous waiter for desktop shell to be created (e.g. at system boot or explorer restart).
        /// </summary>
        public static async Task<IntPtr> WaitForDesktopShellAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            while (DateTime.UtcNow - startTime < timeout && !cancellationToken.IsCancellationRequested)
            {
                var handle = GetDesktopShellView(forceRefresh: true);
                if (handle != IntPtr.Zero && Interop.IsWindow(handle))
                {
                    return handle;
                }
                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
            return GetDesktopShellView(forceRefresh: true);
        }

        public static void ClampToVirtualScreen(ref double posX, ref double posY, double width, double height)
        {
            int virtLeft = Interop.GetSystemMetrics(Interop.SM_XVIRTUALSCREEN);
            int virtTop = Interop.GetSystemMetrics(Interop.SM_YVIRTUALSCREEN);
            int virtWidth = Interop.GetSystemMetrics(Interop.SM_CXVIRTUALSCREEN);
            int virtHeight = Interop.GetSystemMetrics(Interop.SM_CYVIRTUALSCREEN);

            if (virtWidth <= 0 || virtHeight <= 0) return; // Fallback if metrics unavailable

            // Metrics are physical pixels, positions are DIPs
            double scale = 1.0;
            try
            {
                uint dpi = Interop.GetDpiForSystem();
                if (dpi > 0) scale = dpi / 96.0;
            }
            catch { }

            double virtRight = (virtLeft + virtWidth) / scale;
            double virtBottom = (virtTop + virtHeight) / scale;
            double virtLeftDip = virtLeft / scale;
            double virtTopDip = virtTop / scale;

            // Ensure at least 40px of width and 30px of height remain in visible screen
            double minVisibleW = Math.Min(40, width);
            double minVisibleH = Math.Min(30, height);

            if (posX + minVisibleW > virtRight) posX = virtRight - minVisibleW;
            if (posX < virtLeftDip) posX = virtLeftDip;

            if (posY + minVisibleH > virtBottom) posY = virtBottom - minVisibleH;
            if (posY < virtTopDip) posY = virtTopDip;
        }
    }
}
