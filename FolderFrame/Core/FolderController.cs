using FolderFrame.Util;
using System.IO;
using System.Windows;

namespace FolderFrame.Core
{
    /// <summary>Owns the folder windows: creates them at start, adds/removes folders, keeps them alive.</summary>
    public class FolderController
    {
        public readonly List<FolderWindow> Windows = new();
        public readonly RegistryHelper Settings = new("FolderFrame");
        private bool _visible = true;

        public OpenTrigger DefaultOpenTrigger
        {
            get => int.TryParse(Settings.ReadKeyValueRoot("DefaultOpenTrigger")?.ToString(), out int v) ? (OpenTrigger)Math.Clamp(v, 0, 2) : OpenTrigger.Hover;
            set => Settings.WriteToRegistryRoot("DefaultOpenTrigger", ((int)value).ToString());
        }

        public void LoadAll()
        {
            // The same folder saved twice would show two stacked icons: keep the most recently
            // saved entry and drop the rest
            foreach (var group in FolderConfig.LoadAll().GroupBy(c => c.FolderKey))
            {
                var ordered = group.OrderByDescending(c => c.SavedAt).ToList();
                foreach (var duplicate in ordered.Skip(1))
                {
                    AppLogger.Log($"[FolderController] Removing duplicate entry {duplicate.Id} for '{duplicate.Folder}'.");
                    duplicate.Delete();
                }
                CreateWindow(ordered[0]);
            }
            AppLogger.Log($"[FolderController] Loaded {Windows.Count} folder(s).");
        }

        private FolderWindow? CreateWindow(FolderConfig config)
        {
            try
            {
                var window = new FolderWindow(config, this);
                Windows.Add(window);
                if (_visible) window.Show();
                return window;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[FolderController] Could not create window for '{config.Folder}': {ex}");
                return null;
            }
        }

        /// <summary>Adds a folder icon for <paramref name="folderPath"/> near the centre of the primary screen.</summary>
        public void AddFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

            var existing = Windows.FirstOrDefault(w => string.Equals(w.Config.Folder.TrimEnd('\\'), folderPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Open();
                return;
            }

            var area = SystemParameters.WorkArea;
            var config = new FolderConfig
            {
                Folder = folderPath,
                OpenTrigger = DefaultOpenTrigger,
                PosX = area.Left + area.Width / 2 - FolderWindow.IconWidth / 2,
                PosY = area.Top + area.Height / 3,
            };
            config.Save();
            var window = CreateWindow(config);
            window?.ResolveOverlap();
        }

        public void RemoveFolder(FolderWindow window)
        {
            window.Config.Delete();
            Windows.Remove(window);
            window.Close();
        }

        public void ApplyOpenTriggerToAll(OpenTrigger trigger)
        {
            foreach (var w in Windows)
            {
                w.Config.OpenTrigger = trigger;
                w.Config.Save();
            }
        }

        public void SaveAll()
        {
            // Config holds the position the user chose; where the window happens to be now may be a
            // temporary spot Windows picked after a screen change
            foreach (var w in Windows.ToList())
            {
                w.Config.Save();
            }
        }

        public void CollapseAll()
        {
            foreach (var w in Windows) w.Collapse(instant: true);
        }

        public void ToggleVisibility()
        {
            _visible = !_visible;
            foreach (var w in Windows)
            {
                if (_visible) w.Show();
                else
                {
                    w.Collapse(instant: true);
                    w.Hide();
                }
            }
        }

        /// <summary>
        /// Puts every icon back at its saved place and below other apps (after display or session
        /// changes); recreates windows whose handle died.
        /// </summary>
        public void RefreshWindows()
        {
            foreach (var w in Windows.ToList())
            {
                if (!w.IsHandleAlive)
                {
                    AppLogger.Log($"[FolderController] Window for '{w.Config.DisplayTitle}' is gone; recreating.");
                    Windows.Remove(w);
                    try { w.Close(); } catch { }
                    CreateWindow(w.Config);
                    continue;
                }
                if (_visible && !w.IsVisible) w.Show();
                w.RestoreSavedPosition("refresh");
            }
        }

        /// <summary>
        /// Re-places every collapsed icon just above the desktop. Called whenever another window
        /// becomes the foreground one: after Show desktop (Win+D) the desktop is raised over normal
        /// windows, and without this the folders stayed hidden under it.
        /// </summary>
        public void KeepAllAboveDesktop()
        {
            if (!_visible) return;
            foreach (var w in Windows) w.KeepBehind();
        }

    }
}
