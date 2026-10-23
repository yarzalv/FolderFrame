using FolderFrame.Core;
using FolderFrame.Services;
using FolderFrame.Util;
using IWshRuntimeLibrary;
using Microsoft.WindowsAPICodePack.Shell;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using static FolderFrame.Util.Interop;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using File = System.IO.File;
using Imaging = System.Windows.Interop.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = Wpf.Ui.Controls.MenuItem;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Path = System.IO.Path;

namespace FolderFrame
{
    /// <summary>
    /// One folder on the desktop: a collapsed icon that opens into a card with the folder's apps.
    /// The window is a top-level tool window kept just above the desktop, below every app.
    /// </summary>
    public partial class FolderWindow : Window
    {
        public const double IconWidth = 84;
        public const double IconHeight = 104;

        public FolderConfig Config { get; }
        public ObservableCollection<FileItem> FileItems { get; } = new();

        private readonly FolderController _controller;
        private readonly ICollectionView _view;
        private readonly ShellContextMenu _shellMenu = new();
        private string _currentFolderPath;
        private FileSystemWatcher? _fileWatcher;
        private CancellationTokenSource _loadCts = new();
        private CancellationTokenSource? _reloadDebounceCts;

        // Expand / collapse state
        private bool _isExpanded;
        private bool _isAnimating;
        private int _animGeneration;
        private RECT _anchorRectPx;          // collapsed icon rect captured when the card opens
        private bool _hasAnchorRectPx;
        private IntPtr _foregroundAtExpand = IntPtr.Zero;
        private double _expandScale = 1.0;      // display scale when the card opened
        private double _thumbnailScale = 0;     // display scale the current thumbnails were made for
        private bool _suppressHoverUntilLeave;
        private DateTime _lastCollapseTime = DateTime.MinValue;
        private readonly DispatcherTimer _expandedCheckTimer;

        // Interaction state
        private bool _allowZOrderChange;
        private bool _menuOpen;
        private bool _isDragging;
        private bool _dragMoving;
        private bool _mouseDownOnIcon;
        private int _iconDownClickCount;
        private Point _iconDownPos;
        private FileItem? _itemDragCandidate;
        private Point _itemDownPos;

        // Smooth scrolling
        private double _scrollTarget;
        private double _scrollCurrent;
        private bool _scrollActive;
        private TimeSpan _scrollLastFrame;

        public FolderWindow(FolderConfig config, FolderController controller)
        {
            InitializeComponent();
            Config = config;
            _controller = controller;
            _currentFolderPath = config.Folder;

            ItemsList.ItemsSource = FileItems;
            _view = CollectionViewSource.GetDefaultView(FileItems);
            FileItems.CollectionChanged += (_, _) => UpdateIconPreviews();

            // Saved position as is; Window_Loaded makes it visible if the screen is smaller now
            Left = Config.PosX;
            Top = Config.PosY;
            Width = IconWidth;
            Height = IconHeight;
            // Show desktop (Win+D) and similar commands must never minimize a folder
            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                    KeepBehind();
                }
            };

            _expandedCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            _expandedCheckTimer.Tick += ExpandedCheckTimer_Tick;

            ApplyConfig();
            // Files load in Window_Loaded: thumbnails need the window's DPI to be sharp
        }

        public bool IsHandleAlive
        {
            get
            {
                var h = new WindowInteropHelper(this).Handle;
                return h != IntPtr.Zero && IsWindow(h);
            }
        }

        private IntPtr Handle => new WindowInteropHelper(this).Handle;

        private double DpiScale
        {
            get
            {
                var h = Handle;
                double s = h == IntPtr.Zero ? 0 : GetDpiForWindow(h) / 96.0;
                return s > 0 ? s : 1.0;
            }
        }

        #region Window plumbing

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var h = Handle;
            int exStyle = (int)GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(h)?.AddHook(WndProc);
            KeepBehind();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0046 && !_allowZOrderChange) // WM_WINDOWPOSCHANGING
            {
                // Freeze z-order: clicks and activation must not raise a folder above other apps.
                // Deliberate changes go through SetZOrder.
                var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
                wp.flags |= SWP_NOZORDER;
                System.Runtime.InteropServices.Marshal.StructureToPtr(wp, lParam, false);
            }
            else if (msg == 0x0112 && (wParam.ToInt64() & 0xFFF0) == 0xF020) // WM_SYSCOMMAND, SC_MINIMIZE
            {
                handled = true; // a desktop folder has nothing to minimize to
            }
            else if (msg == 0x02E0) // WM_DPICHANGED: display scale changed (or the icon reached another monitor)
            {
                // Let WPF handle it first, then fix size, position and thumbnails ourselves:
                // the collapsed icon kept its old size until it was opened and closed.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (_isExpanded || _isAnimating) Collapse(instant: true);
                    RestoreSavedPosition("display scale changed");
                });
            }
            return IntPtr.Zero;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ReloadFolder();
            KeepBehind();
            // Something can shift an icon while the desktop settles after start; put it back where
            // it was saved, now and again a little later.
            RestoreSavedPosition("loaded");
            foreach (int delayMs in new[] { 1500, 5000 })
            {
                var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
                check.Tick += (_, _) =>
                {
                    check.Stop();
                    RestoreSavedPosition($"{delayMs}ms after start");
                };
                check.Start();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _expandedCheckTimer.Stop();
            StopSmoothScroll();
            try { _fileWatcher?.Dispose(); } catch { }
            try { _loadCts.Cancel(); } catch { }
            base.OnClosed(e);
        }

        private static bool IsOurWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }

        /// <summary>Changes z-order, bypassing the WM_WINDOWPOSCHANGING freeze.</summary>
        private void SetZOrder(IntPtr insertAfter, int x = 0, int y = 0, int cx = 0, int cy = 0, bool moveAndSize = false, uint extraFlags = 0)
        {
            var h = Handle;
            if (h == IntPtr.Zero) return;
            uint flags = SWP_NOACTIVATE | extraFlags;
            if (!moveAndSize) flags |= SWP_NOMOVE | SWP_NOSIZE;
            _allowZOrderChange = true;
            try
            {
                SetWindowPos(h, insertAfter, x, y, cx, cy, flags);
            }
            finally
            {
                _allowZOrderChange = false;
            }
        }

        /// <summary>
        /// Puts the collapsed icon directly above the desktop: below every app window, but not below
        /// the desktop itself (HWND_BOTTOM would hide it behind the wallpaper).
        /// </summary>
        public void KeepBehind()
        {
            if (_isExpanded || _isAnimating) return;
            var h = Handle;
            if (h == IntPtr.Zero || !IsWindow(h)) return;

            Topmost = false;
            int exStyle = (int)GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, (exStyle & ~0x00000008) | WS_EX_NOACTIVATE); // drop WS_EX_TOPMOST
            SetZOrder(HWND_NOTOPMOST);

            IntPtr shellView = DesktopShellManager.GetDesktopShellView();
            IntPtr desktopRoot = shellView != IntPtr.Zero ? GetAncestor(shellView, GA_ROOT) : IntPtr.Zero;
            if (desktopRoot == IntPtr.Zero) desktopRoot = FindWindow("Progman", null!);
            if (desktopRoot == IntPtr.Zero || desktopRoot == h) return;

            IntPtr above = Interop.GetWindow(desktopRoot, GW_HWNDPREV);
            if (above == h) return;
            // Inserting after the window that sits above the desktop places us between the two
            SetZOrder(above);
        }

        #endregion

        #region Config, position and overlap

        /// <summary>Applies title, sizes and other visuals from <see cref="Config"/>.</summary>
        public void ApplyConfig()
        {
            string title = Config.DisplayTitle;
            IconCollapsedTitle.Text = title;
            IconExpandedTitle.Text = title;
            Resources["ItemIconSize"] = Math.Clamp(Config.IconSize, 24, 72);
            Resources["ItemWidth"] = Math.Max(76, Config.IconSize + 36);
        }

        /// <summary>Re-reads the folder (after settings changed it or its options).</summary>
        public void ReloadFolder()
        {
            _currentFolderPath = Config.Folder;
            FileItems.Clear();
            InitializeFileWatcher();
            LoadFiles(_currentFolderPath);
        }

        /// <summary>Window position in screen DIPs, from the real window rect.</summary>
        private (double x, double y) ScreenPositionDip()
        {
            var h = Handle;
            if (h == IntPtr.Zero || !GetWindowRect(h, out RECT r)) return (Left, Top);
            double scale = DpiScale;
            return (r.Left / scale, r.Top / scale);
        }

        private void Window_LocationChanged(object sender, EventArgs e)
        {
            // Only the user's drags change the saved position (DragIcon saves when the drag ends).
            // Windows also moves windows on its own when the screen changes size (remote desktop,
            // AnyDesk, Moonlight, docking): saving those moves lost the layout for good.
            if (_dragMoving || _isAnimating || _isExpanded || !IsLoaded || Handle == IntPtr.Zero) return;
            var (x, y) = ScreenPositionDip();
            if (Math.Abs(x - Config.PosX) > 0.5 || Math.Abs(y - Config.PosY) > 0.5)
            {
                AppLogger.Log($"[FolderWindow:{Config.DisplayTitle}] Moved by the system to ({x:0.#},{y:0.#}); saved position ({Config.PosX:0.#},{Config.PosY:0.#}) kept.");
            }
        }

        /// <summary>Saves where the icon really is. Only called after the user moved it.</summary>
        public void SavePositionNow()
        {
            try
            {
                var h = Handle;
                if (h == IntPtr.Zero) return;
                RECT r;
                if ((_isExpanded || _isAnimating) && _hasAnchorRectPx) r = _anchorRectPx;
                else if (!_isExpanded && !_isAnimating && GetWindowRect(h, out RECT current)) r = current;
                else return;

                double scale = DpiScale;
                Config.PosX = r.Left / scale;
                Config.PosY = r.Top / scale;
                Config.SavePosition();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[FolderWindow:{Config.DisplayTitle}] SavePositionNow failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Moves the collapsed icon back to its saved position. If that spot is off the current
        /// screen (smaller resolution in a remote session, unplugged monitor), it is shown at the
        /// nearest visible place instead, without touching the saved position, so the layout comes
        /// back when the screen does.
        /// </summary>
        public void RestoreSavedPosition(string reason)
        {
            try
            {
                if (_isExpanded || _isAnimating || _dragMoving || _isDragging) return;
                var h = Handle;
                if (h == IntPtr.Zero || !GetWindowRect(h, out RECT r)) return;
                double scale = DpiScale;
                int w = (int)Math.Round(IconWidth * scale), hgt = (int)Math.Round(IconHeight * scale);

                // After a display-scale change the window can keep its old pixel size
                if (Math.Abs(r.Right - r.Left - w) > 1 || Math.Abs(r.Bottom - r.Top - hgt) > 1)
                {
                    Width = IconWidth;
                    Height = IconHeight;
                    SetWindowPos(h, IntPtr.Zero, 0, 0, w, hgt, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                    AppLogger.Log($"[FolderWindow:{Config.DisplayTitle}] Resized icon to {w}x{hgt} px for scale {scale:0.##} ({reason}).");
                }
                // Thumbnails are rendered for a given scale; redo them if it changed
                if (_thumbnailScale > 0 && Math.Abs(scale - _thumbnailScale) > 0.01) ReloadFolder();
                int targetX = (int)Math.Round(Config.PosX * scale);
                int targetY = (int)Math.Round(Config.PosY * scale);

                var work = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(targetX, targetY, w, hgt)).WorkingArea;
                int visibleX = Math.Clamp(targetX, work.Left, Math.Max(work.Left, work.Right - w));
                int visibleY = Math.Clamp(targetY, work.Top, Math.Max(work.Top, work.Bottom - hgt));
                bool temporary = visibleX != targetX || visibleY != targetY;

                if (Math.Abs(r.Left - visibleX) > 1 || Math.Abs(r.Top - visibleY) > 1)
                {
                    AppLogger.Log($"[FolderWindow:{Config.DisplayTitle}] Icon at px ({r.Left},{r.Top}) -> ({visibleX},{visibleY}) ({reason}{(temporary ? "; saved spot is off this screen, kept for later" : "")}).");
                    SetWindowPos(h, IntPtr.Zero, visibleX, visibleY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
                KeepBehind();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[FolderWindow:{Config.DisplayTitle}] RestoreSavedPosition failed: {ex.Message}");
            }
        }

        private static bool RectsIntersect(RECT a, RECT b) =>
            a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

        /// <summary>Moves this collapsed icon to the nearest free spot if it overlaps another folder.</summary>
        public void ResolveOverlap()
        {
            if (_isExpanded || _isAnimating) return;
            var h = Handle;
            if (h == IntPtr.Zero || !GetWindowRect(h, out RECT self)) return;

            var obstacles = new List<RECT>();
            foreach (var other in _controller.Windows)
            {
                if (other == this || !other.IsVisible || other._isExpanded || other._isAnimating) continue;
                var oh = other.Handle;
                if (oh != IntPtr.Zero && GetWindowRect(oh, out RECT o)) obstacles.Add(o);
            }
            if (!obstacles.Any(o => RectsIntersect(self, o))) return;

            int w = self.Right - self.Left;
            int hgt = self.Bottom - self.Top;
            var work = System.Windows.Forms.Screen.FromHandle(h).WorkingArea;
            int gap = (int)Math.Round(6 * DpiScale);
            int step = Math.Max(8, w / 4);

            bool IsFree(int x, int y)
            {
                if (x < work.Left || y < work.Top || x + w > work.Right || y + hgt > work.Bottom) return false;
                var candidate = new RECT { Left = x - gap, Top = y - gap, Right = x + w + gap, Bottom = y + hgt + gap };
                return !obstacles.Any(o => RectsIntersect(candidate, o));
            }

            for (int ring = 1; ring <= 80; ring++)
            {
                int best = int.MaxValue, bestX = 0, bestY = 0;
                for (int dx = -ring; dx <= ring; dx++)
                {
                    for (int dy = -ring; dy <= ring; dy++)
                    {
                        if (Math.Abs(dx) != ring && Math.Abs(dy) != ring) continue; // only the ring's edge
                        int x = self.Left + dx * step, y = self.Top + dy * step;
                        if (!IsFree(x, y)) continue;
                        int dist = dx * dx + dy * dy;
                        if (dist < best) { best = dist; bestX = x; bestY = y; }
                    }
                }
                if (best != int.MaxValue)
                {
                    SetWindowPos(h, IntPtr.Zero, bestX, bestY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                    SavePositionNow();
                    return;
                }
            }
        }

        #endregion

        #region Hover / click detection

        /// <summary>True when the cursor is over this window and nothing else covers it there.</summary>
        public bool IsCursorInside()
        {
            if (_menuOpen) return true;
            if (_isDragging && (GetAsyncKeyState(0x01) & 0x8000) != 0) return true;

            var h = Handle;
            if (h == IntPtr.Zero || !IsWindow(h) || !GetWindowRect(h, out RECT r)) return false;
            var p = System.Windows.Forms.Cursor.Position;
            int pad = _isExpanded ? 0 : 4;
            if (p.X < r.Left - pad || p.X > r.Right + pad || p.Y < r.Top - pad || p.Y > r.Bottom + pad) return false;

            IntPtr top = WindowFromPoint(new POINT { X = p.X, Y = p.Y });
            if (top == IntPtr.Zero) return false;
            IntPtr topRoot = GetAncestor(top, GA_ROOT);

            if (_isExpanded || _isAnimating)
            {
                return topRoot == h || IsOurWindow(topRoot);
            }

            // Collapsed: an open card of another folder covering this spot owns the cursor
            var otherOpen = _controller.Windows.FirstOrDefault(w => w != this && (w._isExpanded || w._isAnimating));
            if (otherOpen != null && otherOpen.IsCursorInside()) return false;
            return topRoot == h;
        }

        private bool CanHoverOpen =>
            Config.OpenTrigger == OpenTrigger.Hover
            && !_suppressHoverUntilLeave
            && (DateTime.Now - _lastCollapseTime).TotalMilliseconds >= 250
            && !_isExpanded && !_isAnimating;

        private void TryOpenOnHover()
        {
            if (!CanHoverOpen) return;
            var otherOpen = _controller.Windows.FirstOrDefault(w => w != this && (w._isExpanded || w._isAnimating));
            if (otherOpen != null && otherOpen.IsCursorInside()) return;
            if (IsCursorInside() && (GetAsyncKeyState(0x01) & 0x8000) == 0)
            {
                Expand();
            }
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            // Entering means the cursor was outside, so a pending suppression has done its job
            _suppressHoverUntilLeave = false;
            if (!_isExpanded && !_isAnimating) TryOpenOnHover();
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            _suppressHoverUntilLeave = false;
            if ((_isExpanded || _isAnimating) && Config.OpenTrigger == OpenTrigger.Hover
                && !_menuOpen && !_isDragging && !_dragMoving && !IsCursorInside())
            {
                Collapse();
            }
        }

        private void IconCollapsed_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _iconDownPos = e.GetPosition(this);
            _iconDownClickCount = e.ClickCount;
            _mouseDownOnIcon = true;
            e.Handled = true;
        }

        private void IconCollapsed_MouseMove(object sender, MouseEventArgs e)
        {
            if (_mouseDownOnIcon && e.LeftButton == MouseButtonState.Pressed && !Config.IsLocked)
            {
                Vector diff = e.GetPosition(this) - _iconDownPos;
                if (Math.Abs(diff.X) > 6 || Math.Abs(diff.Y) > 6)
                {
                    _mouseDownOnIcon = false;
                    DragIcon(centerOnCursor: false);
                }
                return;
            }
            if (!_isExpanded && !_isAnimating && !_dragMoving) TryOpenOnHover();
        }

        private void IconCollapsed_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_mouseDownOnIcon) return;
            _mouseDownOnIcon = false;
            bool open = Config.OpenTrigger switch
            {
                OpenTrigger.DoubleClick => _iconDownClickCount >= 2,
                _ => true, // Click, and Hover as a fallback when hovering didn't open it
            };
            // Touch has no hover and double taps are awkward: a tap always opens
            if (e.StylusDevice != null) open = true;
            if (open) Expand();
        }

        #endregion

        #region Expand / collapse

        /// <summary>Opens the card in front of everything (asked for explicitly: tray, settings list).</summary>
        public void Open()
        {
            if (!IsVisible) Show();
            Expand();
            BringCardToFront();
        }

        /// <summary>
        /// The window just above the stack of FolderFrame windows that sits on the desktop, i.e.
        /// the lowest app window. Inserting after it puts a window above every folder icon but
        /// still below all apps. Zero means nothing is above (use HWND_TOP).
        /// </summary>
        private static IntPtr WindowAboveFolderStack()
        {
            IntPtr shellView = DesktopShellManager.GetDesktopShellView();
            IntPtr desktopRoot = shellView != IntPtr.Zero ? GetAncestor(shellView, GA_ROOT) : IntPtr.Zero;
            if (desktopRoot == IntPtr.Zero) desktopRoot = FindWindow("Progman", null!);
            if (desktopRoot == IntPtr.Zero) return IntPtr.Zero;
            IntPtr w = Interop.GetWindow(desktopRoot, GW_HWNDPREV);
            while (w != IntPtr.Zero && IsOurWindow(w)) w = Interop.GetWindow(w, GW_HWNDPREV);
            return w;
        }

        /// <summary>Raises the open card above all app windows and (normally) gives it the keyboard.</summary>
        private void BringCardToFront(bool activate = true)
        {
            if (!_isExpanded && !_isAnimating) return;
            var h = Handle;
            SetZOrder(HWND_TOP);
            if (!activate) return;
            SetForegroundWindow(h);
            Activate();
            // Our own window is now the foreground one; don't read that as "the user switched app"
            _foregroundAtExpand = GetForegroundWindow();
        }

        private void IconExpandedCard_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Clicking a card that opened behind your apps brings it in front of them
            BringCardToFront();
        }

        private void Expand()
        {
            if (_isExpanded || _isAnimating) return;
            _isAnimating = true;
            var h = Handle;
            _hasAnchorRectPx = GetWindowRect(h, out _anchorRectPx);

            int exStyle = (int)GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, exStyle & ~WS_EX_NOACTIVATE);

            foreach (var other in _controller.Windows)
            {
                if (other == this) continue;
                if (other._isExpanded || other._isAnimating) other.Collapse(instant: true);
                other.KeepBehind();
            }

            // Size and place the card in physical pixels on the icon's own monitor
            double scale = DpiScale;
            var workArea = System.Windows.Forms.Screen.FromHandle(h).WorkingArea;
            int margin = (int)Math.Round(12 * scale);
            double targetW = Math.Clamp(Config.CardWidth, 320, 720);
            double targetH = Math.Clamp(Config.CardHeight, 280, 720);
            targetW = Math.Min(targetW, (workArea.Width - 2 * margin) / scale);
            targetH = Math.Min(targetH, (workArea.Height - 2 * margin) / scale);
            int wPx = (int)Math.Round(targetW * scale);
            int hPx = (int)Math.Round(targetH * scale);

            int iconCenterX = _hasAnchorRectPx ? (_anchorRectPx.Left + _anchorRectPx.Right) / 2 : (int)Math.Round((Config.PosX + IconWidth / 2) * scale);
            int iconCenterY = _hasAnchorRectPx ? (_anchorRectPx.Top + _anchorRectPx.Bottom) / 2 : (int)Math.Round((Config.PosY + IconHeight / 2) * scale);

            int iconLeft = _hasAnchorRectPx ? _anchorRectPx.Left : (int)Math.Round(Config.PosX * scale);
            int iconTop = _hasAnchorRectPx ? _anchorRectPx.Top : (int)Math.Round(Config.PosY * scale);
            int iconRight = _hasAnchorRectPx ? _anchorRectPx.Right : iconLeft + (int)Math.Round(IconWidth * scale);
            int iconBottom = _hasAnchorRectPx ? _anchorRectPx.Bottom : iconTop + (int)Math.Round(IconHeight * scale);

            // Grow from the icon toward the side with more free space (it stays under the card,
            // so hovering keeps it open), then clamp to the icon's work area
            int leftPx = workArea.Right - iconLeft >= iconRight - workArea.Left ? iconLeft : iconRight - wPx;
            int topPx = workArea.Bottom - iconTop >= iconBottom - workArea.Top ? iconTop : iconBottom - hPx;
            leftPx = Math.Max(workArea.Left + margin, Math.Min(leftPx, workArea.Right - wPx - margin));
            topPx = Math.Max(workArea.Top + margin, Math.Min(topPx, workArea.Bottom - hPx - margin));

            Width = targetW;
            Height = targetH;
            _expandScale = scale;
            // Open in the folders' layer: above the desktop and every other folder icon, but below
            // all apps, and NOT activated (opening on hover must not take the keyboard from the app
            // you're typing in). Clicking the card raises and activates it (BringCardToFront).
            SetZOrder(WindowAboveFolderStack(), leftPx, topPx, wPx, hPx, moveAndSize: true, extraFlags: SWP_SHOWWINDOW);
            // Only a *change* of foreground window after this point means the user switched app
            _foregroundAtExpand = GetForegroundWindow();

            IconCollapsedContainer.Visibility = Visibility.Collapsed;
            IconExpandedScale.ScaleX = 0.97;
            IconExpandedScale.ScaleY = 0.97;
            IconExpandedCard.Opacity = 0;
            double originX = Math.Clamp((iconCenterX - leftPx) / (double)wPx, 0, 1);
            double originY = Math.Clamp((iconCenterY - topPx) / (double)hPx, 0, 1);
            IconExpandedCard.RenderTransformOrigin = new Point(originX, originY);
            IconExpandedCard.Visibility = Visibility.Visible;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var scaleAnim = new DoubleAnimation(0.97, 1.0, TimeSpan.FromMilliseconds(80)) { EasingFunction = ease };
            var opacityAnim = new DoubleAnimation(0.6, 1.0, TimeSpan.FromMilliseconds(60)) { EasingFunction = ease };
            int generation = ++_animGeneration;
            scaleAnim.Completed += (_, _) =>
            {
                if (generation != _animGeneration) return;
                _isExpanded = true;
                _isAnimating = false;
                _expandedCheckTimer.Start();
                if (Config.OpenTrigger == OpenTrigger.Hover && !IsCursorInside()) Collapse();
            };
            IconExpandedScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            IconExpandedScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            IconExpandedCard.BeginAnimation(OpacityProperty, opacityAnim);
        }

        private void ExpandedCheckTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isExpanded && !_isAnimating)
            {
                _expandedCheckTimer.Stop();
                return;
            }
            if (_menuOpen || _isDragging || _dragMoving) return;

            // Another app became active: get out of its way
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != _foregroundAtExpand && !IsOurWindow(fg))
            {
                Collapse(instant: true);
                return;
            }

            bool inside = IsCursorInside();
            if (Config.OpenTrigger == OpenTrigger.Hover)
            {
                if (!inside) Collapse();
            }
            else if (!inside && ((GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0))
            {
                // Click-to-open folders close when you click anywhere else
                Collapse();
            }
        }

        public void Collapse(bool instant = false)
        {
            if (!_isExpanded && !_isAnimating) return;
            _expandedCheckTimer.Stop();
            if (instant)
            {
                FinishCollapse();
                return;
            }
            if (_isAnimating) return;
            _isAnimating = true;

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var scaleAnim = new DoubleAnimation(1.0, 0.94, TimeSpan.FromMilliseconds(90)) { EasingFunction = ease };
            var opacityAnim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(90)) { EasingFunction = ease };
            int generation = ++_animGeneration;
            scaleAnim.Completed += (_, _) =>
            {
                // An instant collapse (or a new expand) may have happened meanwhile
                if (generation == _animGeneration) FinishCollapse();
            };
            IconExpandedScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            IconExpandedScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            IconExpandedCard.BeginAnimation(OpacityProperty, opacityAnim);
        }

        private void FinishCollapse()
        {
            _animGeneration++;
            IconExpandedScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            IconExpandedScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            IconExpandedCard.BeginAnimation(OpacityProperty, null);
            IconExpandedCard.Visibility = Visibility.Collapsed;
            IconExpandedCard.Opacity = 0;
            IconCollapsedContainer.Visibility = Visibility.Visible;
            CloseSearch();

            Topmost = false;
            Width = IconWidth;
            Height = IconHeight;

            var h = Handle;
            int exStyle = (int)GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, (exStyle & ~0x00000008) | WS_EX_NOACTIVATE);

            // Back to the exact physical position the icon had before opening, unless the display
            // scale changed meanwhile: then that pixel position is stale and the saved one is used
            if (_hasAnchorRectPx && Math.Abs(DpiScale - _expandScale) < 0.01)
            {
                SetWindowPos(h, IntPtr.Zero, _anchorRectPx.Left, _anchorRectPx.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            else
            {
                double scale = DpiScale;
                SetWindowPos(h, IntPtr.Zero, (int)Math.Round(Config.PosX * scale), (int)Math.Round(Config.PosY * scale),
                    (int)Math.Round(IconWidth * scale), (int)Math.Round(IconHeight * scale), SWP_NOZORDER | SWP_NOACTIVATE);
            }
            _hasAnchorRectPx = false;

            _isExpanded = false;
            _isAnimating = false;
            _lastCollapseTime = DateTime.Now;
            _foregroundAtExpand = IntPtr.Zero;
            KeepBehind();

            // If the cursor is still on the icon, don't let hover reopen it until the cursor leaves
            if (GetWindowRect(h, out RECT iconRect))
            {
                var c = System.Windows.Forms.Cursor.Position;
                if (c.X >= iconRect.Left && c.X < iconRect.Right && c.Y >= iconRect.Top && c.Y < iconRect.Bottom)
                {
                    _suppressHoverUntilLeave = true;
                }
            }
        }

        #endregion

        #region Moving the icon

        private void MoveButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (Config.IsLocked) return;
            DragIcon(centerOnCursor: true);
        }

        /// <summary>
        /// Drags the collapsed icon with the system move loop. From the open card it first turns back
        /// into the icon, centered under the cursor.
        /// </summary>
        private void DragIcon(bool centerOnCursor)
        {
            var h = Handle;
            if (centerOnCursor)
            {
                _expandedCheckTimer.Stop();
                _animGeneration++;
                IconExpandedCard.Visibility = Visibility.Collapsed;
                IconExpandedCard.Opacity = 0;
                IconCollapsedContainer.Visibility = Visibility.Visible;
                CloseSearch();
                _isExpanded = false;
                _isAnimating = false;
                _hasAnchorRectPx = false;
                Topmost = false;

                var cursor = System.Windows.Forms.Cursor.Position;
                double scale = DpiScale;
                int wPx = (int)Math.Round(IconWidth * scale), hPx = (int)Math.Round(IconHeight * scale);
                Width = IconWidth;
                Height = IconHeight;
                SetWindowPos(h, IntPtr.Zero, cursor.X - wPx / 2, cursor.Y - hPx / 2, wPx, hPx, SWP_NOZORDER | SWP_NOACTIVATE);
                UpdateLayout();
            }

            _dragMoving = true;
            _isDragging = true;
            try
            {
                ReleaseCapture();
                SendMessage(h, 0x0112, (IntPtr)0xF012, IntPtr.Zero); // WM_SYSCOMMAND, SC_MOVE | HTCAPTION
            }
            catch { }
            finally
            {
                _dragMoving = false;
                _isDragging = false;
            }

            // Keep the icon inside the work area of the monitor it was dropped on
            if (GetWindowRect(h, out RECT rect))
            {
                var workArea = System.Windows.Forms.Screen.FromHandle(h).WorkingArea;
                int pad = (int)Math.Round(4 * DpiScale);
                int w = rect.Right - rect.Left, hh = rect.Bottom - rect.Top;
                int x = Math.Max(workArea.Left + pad, Math.Min(rect.Left, workArea.Right - w - pad));
                int y = Math.Max(workArea.Top + pad, Math.Min(rect.Top, workArea.Bottom - hh - pad));
                if (x != rect.Left || y != rect.Top)
                {
                    SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
            }

            int exStyle = (int)GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
            SavePositionNow();
            ResolveOverlap();
            KeepBehind();
            _suppressHoverUntilLeave = true;
            _lastCollapseTime = DateTime.Now;
        }

        #endregion

        #region Loading files

        private void InitializeFileWatcher()
        {
            try { _fileWatcher?.Dispose(); } catch { }
            _fileWatcher = null;
            if (!Directory.Exists(_currentFolderPath)) return;
            try
            {
                _fileWatcher = new FileSystemWatcher(_currentFolderPath)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                _fileWatcher.Created += OnFolderChanged;
                _fileWatcher.Deleted += OnFolderChanged;
                _fileWatcher.Renamed += OnFolderChanged;
                _fileWatcher.Changed += OnFolderChanged;
                _fileWatcher.Error += (_, _) => Dispatcher.InvokeAsync(ReloadFolder);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[FolderWindow:{Config.DisplayTitle}] File watcher failed: {ex.Message}");
            }
        }

        private void OnFolderChanged(object sender, FileSystemEventArgs e)
        {
            // Debounce bursts of events (copying many files)
            _reloadDebounceCts?.Cancel();
            _reloadDebounceCts = new CancellationTokenSource();
            var token = _reloadDebounceCts.Token;
            Task.Delay(250, token).ContinueWith(t =>
            {
                if (!t.IsCanceled) Dispatcher.InvokeAsync(() => LoadFiles(_currentFolderPath));
            }, TaskScheduler.Default);
        }

        private async void LoadFiles(string path)
        {
            _loadCts.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;
            _thumbnailScale = DpiScale;

            BackButton.Visibility = string.Equals(path, Config.Folder, StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;
            if (!Directory.Exists(path))
            {
                FileItems.Clear();
                ShowEmptyState(missing: true);
                return;
            }

            try
            {
                var entries = await Task.Run(() =>
                {
                    var dir = new DirectoryInfo(path);
                    IEnumerable<FileSystemInfo> all = dir.GetFiles().Cast<FileSystemInfo>().Concat(dir.GetDirectories());
                    if (!Config.ShowHiddenFiles)
                    {
                        all = all.Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Attributes.HasFlag(FileAttributes.System));
                    }
                    return all.ToList();
                }, token);
                if (token.IsCancellationRequested) return;

                var items = new List<FileItem>();
                foreach (var entry in entries)
                {
                    if (token.IsCancellationRequested) return;
                    bool isFile = entry is FileInfo;
                    string ext = isFile ? Path.GetExtension(entry.Name) : "";
                    string name = !isFile || Config.ShowFileExtension || string.IsNullOrEmpty(ext)
                        ? entry.Name
                        : entry.Name[..^ext.Length];
                    var existing = FileItems.FirstOrDefault(i => string.Equals(i.FullPath, entry.FullName, StringComparison.OrdinalIgnoreCase));
                    var item = existing ?? new FileItem();
                    item.Name = name;
                    item.FullPath = entry.FullName;
                    item.IsFolder = !isFile;
                    item.DateModified = entry.LastWriteTime;
                    item.DateCreated = entry.CreationTime;
                    item.FileType = ext;
                    item.ItemSize = isFile ? ((FileInfo)entry).Length : 0;
                    if (existing == null || existing.Thumbnail == null || entry.LastWriteTime > DateTime.Now.AddSeconds(-5))
                    {
                        item.Thumbnail = await GetThumbnailAsync(entry.FullName);
                    }
                    items.Add(item);
                }
                if (token.IsCancellationRequested) return;

                FileItems.Clear();
                foreach (var item in SortItems(items)) FileItems.Add(item);
                ItemCountText.Text = FileItems.Count.ToString();
                ShowEmptyState(missing: false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Log($"[FolderWindow:{Config.DisplayTitle}] LoadFiles failed: {ex}");
            }
        }

        private IEnumerable<FileItem> SortItems(IEnumerable<FileItem> items)
        {
            static string NaturalKey(string? s) => Regex.Replace(s ?? "", @"\d+", m => m.Value.PadLeft(10, '0'));
            bool descending = Config.SortBy % 2 == 0;
            int field = descending ? Config.SortBy - 1 : Config.SortBy;

            IOrderedEnumerable<FileItem> sorted = Config.FolderOrder switch
            {
                1 => items.OrderBy(i => !i.IsFolder),
                2 => items.OrderBy(i => i.IsFolder),
                _ => items.OrderBy(_ => 0),
            };
            sorted = field switch
            {
                3 => descending ? sorted.ThenByDescending(i => i.DateModified) : sorted.ThenBy(i => i.DateModified),
                5 => descending ? sorted.ThenByDescending(i => i.DateCreated) : sorted.ThenBy(i => i.DateCreated),
                7 => descending ? sorted.ThenByDescending(i => i.FileType) : sorted.ThenBy(i => i.FileType),
                9 => descending ? sorted.ThenByDescending(i => i.ItemSize) : sorted.ThenBy(i => i.ItemSize),
                _ => descending
                    ? sorted.ThenByDescending(i => NaturalKey(i.Name), StringComparer.OrdinalIgnoreCase)
                    : sorted.ThenBy(i => NaturalKey(i.Name), StringComparer.OrdinalIgnoreCase),
            };
            return sorted;
        }

        private void ShowEmptyState(bool missing)
        {
            if (missing)
            {
                EmptyStateText.Text = $"No se encuentra la carpeta\n{Config.Folder}";
                PickFolderButton.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyStateText.Text = "Esta carpeta está vacía.\nArrastra aquí apps o accesos directos.";
                PickFolderButton.Visibility = Visibility.Collapsed;
            }
            EmptyStatePanel.Visibility = missing || FileItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ItemsScrollViewer.Visibility = EmptyStatePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            ItemCountText.Text = missing ? "" : FileItems.Count.ToString();
            UpdateIconPreviews();
        }

        private void UpdateIconPreviews()
        {
            var previews = new[] { MiniPreview0, MiniPreview1, MiniPreview2, MiniPreview3 };
            for (int i = 0; i < previews.Length; i++)
            {
                previews[i].Source = FileItems.Count > i ? FileItems[i].Thumbnail : null;
            }
            EmptyFolderIcon.Visibility = FileItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static BitmapSource? GetShellThumbnail(string path, int size)
        {
            try
            {
                using var shellObject = ShellObject.FromParsingName(path);
                var thumbnail = shellObject.Thumbnail;
                thumbnail.CurrentSize = new Size(size, size);
                BitmapSource bitmap = thumbnail.BitmapSource;
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private async Task<BitmapSource?> GetThumbnailAsync(string path)
        {
            int size = (int)Math.Round(Math.Clamp(Config.IconSize, 24, 72) * DpiScale);
            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".svg")
            {
                return await Task.Run(() => LoadSvgThumbnail(path, size));
            }

            // Shell thumbnails need an STA thread; the dispatcher thread is one
            var thumbnail = await Dispatcher.InvokeAsync(() => GetShellThumbnail(path, size), DispatcherPriority.Background);
            if (thumbnail != null && (ext == ".lnk" || ext == ".url") && Config.ShowShortcutArrow)
            {
                thumbnail = AddShortcutArrow(thumbnail, size) ?? thumbnail;
            }
            return thumbnail;
        }

        private static BitmapSource? LoadSvgThumbnail(string path, int size)
        {
            try
            {
                var svg = Svg.SvgDocument.Open(path);
                using var bitmap = svg.Draw(size, size);
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = ms;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SVG thumbnail failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Draws the system shortcut arrow over the bottom-left corner of an icon.</summary>
        private BitmapSource? AddShortcutArrow(BitmapSource icon, int size)
        {
            try
            {
                var overlays = new IntPtr[1];
                if (ExtractIconEx(Environment.SystemDirectory + "\\shell32.dll", 29, overlays, null, 1) <= 0 || overlays[0] == IntPtr.Zero) return null;
                BitmapSource overlay = Imaging.CreateBitmapSourceFromHIcon(overlays[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                DestroyIcon(overlays[0]);

                double scale = size / (double)Math.Max(icon.PixelWidth, icon.PixelHeight);
                double w = icon.PixelWidth * scale, h = icon.PixelHeight * scale;
                double x = (size - w) / 2, y = (size - h) / 2;
                double overlaySize = Math.Max(10, size * 0.4);

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawImage(icon, new Rect(x, y, w, h));
                    dc.DrawImage(overlay, new Rect(x, y + h - overlaySize, overlaySize, overlaySize));
                }
                var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);
                rtb.Freeze();
                return rtb;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Items

        private void AppItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border border || border.DataContext is not FileItem item) return;
            if (e.ClickCount == 2 && e.StylusDevice == null)
            {
                _itemDragCandidate = null;
                OpenItem(item);
                return;
            }
            _itemDragCandidate = item;
            _itemDownPos = e.GetPosition(this);
        }

        /// <summary>Touch: a tap (finger down and up without moving) opens the app.</summary>
        private void AppItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.StylusDevice == null || sender is not Border border || border.DataContext is not FileItem item) return;
            Vector diff = e.GetPosition(this) - _itemDownPos;
            if (_itemDragCandidate == item && Math.Abs(diff.X) < 8 && Math.Abs(diff.Y) < 8)
            {
                _itemDragCandidate = null;
                OpenItem(item);
            }
        }

        private void AppItem_MouseMove(object sender, MouseEventArgs e)
        {
            // With a finger, dragging scrolls the list instead of dragging the app out
            if (e.StylusDevice != null) return;
            if (_itemDragCandidate == null || e.LeftButton != MouseButtonState.Pressed || sender is not Border border) return;
            Vector diff = e.GetPosition(this) - _itemDownPos;
            if (Math.Abs(diff.X) < 8 && Math.Abs(diff.Y) < 8) return;

            var item = _itemDragCandidate;
            _itemDragCandidate = null;
            _isDragging = true;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { item.FullPath! });
                DragDrop.DoDragDrop(border, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[FolderWindow:{Config.DisplayTitle}] Item drag failed: {ex.Message}");
            }
            finally
            {
                _isDragging = false;
            }
        }

        private void OpenItem(FileItem item)
        {
            try
            {
                if (item.IsFolder && Config.OpenSubfoldersInside)
                {
                    NavigateTo(item.FullPath!);
                    return;
                }
                Process.Start(new ProcessStartInfo(item.FullPath!) { UseShellExecute = true });
                Collapse(instant: true);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[FolderWindow:{Config.DisplayTitle}] Could not open '{item.FullPath}': {ex.Message}");
            }
        }

        private void NavigateTo(string path)
        {
            _currentFolderPath = path;
            FileItems.Clear();
            InitializeFileWatcher();
            LoadFiles(path);
        }

        private void BackButton_Click(object sender, MouseButtonEventArgs e)
        {
            var parent = Path.GetDirectoryName(_currentFolderPath.TrimEnd('\\'));
            if (parent == null || _currentFolderPath.Equals(Config.Folder, StringComparison.OrdinalIgnoreCase)) return;
            NavigateTo(parent.Length < Config.Folder.Length ? Config.Folder : parent);
        }

        private void AppItem_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Border border) border.Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255));
        }

        private void AppItem_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Border border) border.Background = Brushes.Transparent;
        }

        private void AppItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is not Border border || border.DataContext is not FileItem item) return;
            var cursor = System.Windows.Forms.Cursor.Position;
            _menuOpen = true;
            void OnClosed()
            {
                _menuOpen = false;
                _shellMenu.ContextMenuClosed -= OnClosed;
            }
            _shellMenu.ContextMenuClosed += OnClosed;
            try
            {
                // FileInfo also for folders: the menu is built from the item's parent folder
                _shellMenu.ShowContextMenu(Handle, new[] { new FileInfo(item.FullPath!) }, cursor, false);
            }
            catch (Exception ex)
            {
                _menuOpen = false;
                AppLogger.Log($"[FolderWindow:{Config.DisplayTitle}] Shell menu failed: {ex.Message}");
            }
        }

        #endregion

        #region Drag and drop into the folder

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            // Open the card while dragging over the icon, so the drop target is visible
            if (!_isExpanded && !_isAnimating && !_isDragging)
            {
                Expand();
                // In front so the drop target is visible, without taking focus mid-drag
                BringCardToFront(activate: false);
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop) || !Directory.Exists(_currentFolderPath)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var file in files)
            {
                try
                {
                    // Dropped from this same folder (an item dragged inside the card): nothing to do
                    if (string.Equals(Path.GetDirectoryName(file), _currentFolderPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;

                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".lnk" || ext == ".url")
                    {
                        // Shortcuts are moved in: that's how you tidy desktop shortcuts into a folder
                        File.Move(file, UniquePath(Path.Combine(_currentFolderPath, Path.GetFileName(file))));
                    }
                    else
                    {
                        // Real files and folders stay where they are; the folder gets a shortcut to them
                        CreateShortcut(file, _currentFolderPath);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[FolderWindow:{Config.DisplayTitle}] Drop of '{file}' failed: {ex.Message}");
                }
            }
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        private static void CreateShortcut(string targetPath, string folder)
        {
            string name = Directory.Exists(targetPath) ? Path.GetFileName(targetPath.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(targetPath);
            string shortcutPath = UniquePath(Path.Combine(folder, name + ".lnk"));
            var shell = new WshShell();
            var shortcut = (IWshShortcut)shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath);
            shortcut.Save();
        }

        #endregion

        #region Header buttons, search and folder menu

        private void SearchButton_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (SearchBar.Visibility == Visibility.Visible)
            {
                CloseSearch(animate: true);
                return;
            }
            OpenSearch();
        }

        /// <summary>
        /// Slides the magnifier from its button slot to just after the item count and draws the
        /// underline field to its right, all within the header row.
        /// </summary>
        private void OpenSearch()
        {
            // Where the magnifier should land: the start of the field's left margin
            SearchButtonShift.BeginAnimation(TranslateTransform.XProperty, null);
            SearchButtonShift.X = 0;
            HeaderGrid.UpdateLayout();
            double fieldLeft = SearchBar.TranslatePoint(new Point(0, 0), HeaderGrid).X;
            double buttonLeft = SearchButton.TranslatePoint(new Point(0, 0), HeaderGrid).X;
            double targetX = fieldLeft - SearchBar.Margin.Left + 4 - buttonLeft;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(180);
            SearchButtonShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Math.Min(0, targetX), duration) { EasingFunction = ease });

            SearchBar.Visibility = Visibility.Visible;
            SearchBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            // Typing needs the keyboard: take it now (the card opens without activating)
            SetForegroundWindow(Handle);
            Activate();
            FilterTextBox.Focus();
        }

        private void CloseSearch(bool animate = false)
        {
            FilterTextBox.Text = "";
            if (!animate)
            {
                SearchButtonShift.BeginAnimation(TranslateTransform.XProperty, null);
                SearchButtonShift.X = 0;
                SearchBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                SearchBar.Visibility = Visibility.Hidden;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var duration = TimeSpan.FromMilliseconds(140);
            var shrink = new DoubleAnimation(0, duration) { EasingFunction = ease };
            shrink.Completed += (_, _) =>
            {
                // Only if it wasn't reopened meanwhile
                if (SearchBarScale.ScaleX < 0.01) SearchBar.Visibility = Visibility.Hidden;
            };
            SearchBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            SearchButtonShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
        }

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = FilterTextBox.Text.Trim();
            _view.Filter = string.IsNullOrEmpty(filter)
                ? null
                : o => o is FileItem fi && fi.DisplayName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
        }

        private void FilterTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) CloseSearch(animate: true);
            else if (e.Key == Key.Enter && _view.Cast<FileItem>().FirstOrDefault() is FileItem first) OpenItem(first);
        }

        private void CloseButton_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _suppressHoverUntilLeave = true;
            Collapse();
        }

        private void SettingsButton_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            OpenSettings();
        }

        public void OpenSettings()
        {
            Collapse(instant: true);
            var dialog = new FolderSettingsWindow(this);
            dialog.Show();
            dialog.Activate();
        }

        private void PickFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using var picker = new System.Windows.Forms.FolderBrowserDialog { Description = "Elige la carpeta", UseDescriptionForTitle = true };
            if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            Config.Folder = picker.SelectedPath;
            Config.Save();
            ApplyConfig();
            ReloadFolder();
        }

        private void FolderMenu_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var menu = new ContextMenu();
            menu.Opened += (_, _) => _menuOpen = true;
            menu.Closed += (_, _) => _menuOpen = false;

            MenuItem Item(string header, SymbolRegular icon, Action onClick)
            {
                var mi = new MenuItem { Header = header, Icon = new SymbolIcon(icon) };
                mi.Click += (_, _) => onClick();
                return mi;
            }
            MenuItem Check(string header, bool isChecked, Action onClick)
            {
                var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
                mi.Click += (_, _) => onClick();
                return mi;
            }

            if (!_isExpanded) menu.Items.Add(Item("Abrir", SymbolRegular.Open20, Expand));
            menu.Items.Add(Item("Abrir en el Explorador", SymbolRegular.FolderOpen20, () =>
            {
                // explorer.exe with the folder as an argument: the shell would *run* the path if the
                // stored setting ever pointed at a program instead of a folder
                if (!Directory.Exists(Config.Folder)) return;
                try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { ArgumentList = { Config.Folder } }); } catch { }
            }));
            menu.Items.Add(Item("Recargar", SymbolRegular.ArrowSync20, ReloadFolder));
            menu.Items.Add(new Separator());

            var sortMenu = new MenuItem { Header = "Ordenar por", Icon = new SymbolIcon(SymbolRegular.ArrowSort20) };
            void SortBy(int field)
            {
                Config.SortBy = (Config.SortBy == field) ? field + 1 : field; // same field again flips the order
                Config.Save();
                Resort();
            }
            int current = Config.SortBy % 2 == 0 ? Config.SortBy - 1 : Config.SortBy;
            sortMenu.Items.Add(Check("Nombre", current == 1, () => SortBy(1)));
            sortMenu.Items.Add(Check("Fecha de modificación", current == 3, () => SortBy(3)));
            sortMenu.Items.Add(Check("Tipo", current == 7, () => SortBy(7)));
            sortMenu.Items.Add(Check("Tamaño", current == 9, () => SortBy(9)));
            sortMenu.Items.Add(new Separator());
            sortMenu.Items.Add(Check("Carpetas primero", Config.FolderOrder == 1, () =>
            {
                Config.FolderOrder = Config.FolderOrder == 1 ? 0 : 1;
                Config.Save();
                Resort();
            }));
            menu.Items.Add(sortMenu);
            menu.Items.Add(Check("Bloquear posición", Config.IsLocked, () =>
            {
                Config.IsLocked = !Config.IsLocked;
                Config.Save();
            }));
            menu.Items.Add(Item("Configuración…", SymbolRegular.Settings20, OpenSettings));
            menu.Items.Add(new Separator());
            var remove = Item("Quitar del escritorio", SymbolRegular.Delete20, async () => await ConfirmRemoveAsync());
            remove.Foreground = new SolidColorBrush(Color.FromRgb(0xFC, 0x60, 0x60));
            menu.Items.Add(remove);

            menu.IsOpen = true;
        }

        private void Resort()
        {
            var sorted = SortItems(FileItems.ToList()).ToList();
            FileItems.Clear();
            foreach (var item in sorted) FileItems.Add(item);
        }

        private async Task ConfirmRemoveAsync()
        {
            Collapse(instant: true);
            var dialog = new MessageBox
            {
                Title = "Quitar carpeta",
                Content = $"¿Quitar \"{Config.DisplayTitle}\" del escritorio?\nLa carpeta y sus archivos no se borran.",
                PrimaryButtonText = "Quitar",
                CloseButtonText = "Cancelar",
            };
            if (await dialog.ShowDialogAsync() == MessageBoxResult.Primary)
            {
                _controller.RemoveFolder(this);
            }
        }

        #endregion

        #region Smooth scrolling

        // The wheel moves a target offset and the view eases towards it every frame,
        // instead of jumping 3 lines per notch.
        private void ItemsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = ItemsScrollViewer;
            if (sv.ScrollableHeight <= 0) return;
            e.Handled = true;
            if (!_scrollActive)
            {
                _scrollCurrent = sv.VerticalOffset;
                _scrollTarget = sv.VerticalOffset;
            }
            // ~100px per wheel notch (120); touchpads send smaller deltas and scroll proportionally
            _scrollTarget = Math.Clamp(_scrollTarget - e.Delta * 0.85, 0, sv.ScrollableHeight);
            if (!_scrollActive)
            {
                _scrollActive = true;
                _scrollLastFrame = TimeSpan.Zero;
                CompositionTarget.Rendering += SmoothScroll_Rendering;
            }
        }

        private void SmoothScroll_Rendering(object? sender, EventArgs e)
        {
            var sv = ItemsScrollViewer;
            if (!sv.IsVisible)
            {
                StopSmoothScroll();
                return;
            }
            TimeSpan now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
            double dt = _scrollLastFrame == TimeSpan.Zero ? 1.0 / 60 : (now - _scrollLastFrame).TotalSeconds;
            if (dt <= 0) return; // Rendering can fire more than once per frame
            _scrollLastFrame = now;
            dt = Math.Min(dt, 0.05);

            // Exponential ease-out, independent of the refresh rate (~90ms time constant)
            double diff = _scrollTarget - _scrollCurrent;
            if (Math.Abs(diff) < 0.5)
            {
                _scrollCurrent = _scrollTarget;
                sv.ScrollToVerticalOffset(_scrollCurrent);
                StopSmoothScroll();
                return;
            }
            _scrollCurrent += diff * (1 - Math.Exp(-dt / 0.09));
            sv.ScrollToVerticalOffset(_scrollCurrent);
        }

        private void StopSmoothScroll()
        {
            if (!_scrollActive) return;
            _scrollActive = false;
            CompositionTarget.Rendering -= SmoothScroll_Rendering;
        }

        #endregion
    }
}
