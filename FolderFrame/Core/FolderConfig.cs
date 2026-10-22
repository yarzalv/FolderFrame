using FolderFrame.Util;
using Microsoft.Win32;
using System.Globalization;
using System.IO;

namespace FolderFrame.Core
{
    /// <summary>How a collapsed folder icon opens its card.</summary>
    public enum OpenTrigger
    {
        Hover = 0,
        Click = 1,
        DoubleClick = 2,
    }

    /// <summary>
    /// Settings of one folder on the desktop, stored under HKCU\SOFTWARE\FolderFrame\Folders\{Id}.
    /// Numbers are written culture-invariant so a change of Windows locale can't misread positions.
    /// </summary>
    public class FolderConfig
    {
        public const string RootKey = @"SOFTWARE\FolderFrame";
        public const string FoldersKey = RootKey + @"\Folders";

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Folder { get; set; } = "";
        public string Title { get; set; } = "";

        /// <summary>Position of the collapsed icon in screen DIPs.</summary>
        public double PosX { get; set; } = 100;
        public double PosY { get; set; } = 100;

        public bool IsLocked { get; set; }
        public OpenTrigger OpenTrigger { get; set; } = OpenTrigger.Hover;

        public double CardWidth { get; set; } = 400;
        public double CardHeight { get; set; } = 440;
        public double IconSize { get; set; } = 40;

        public bool ShowHiddenFiles { get; set; }
        public bool ShowFileExtension { get; set; }
        public bool ShowShortcutArrow { get; set; } = true;
        public bool OpenSubfoldersInside { get; set; }

        /// <summary>1 name, 3 date modified, 5 date created, 7 type, 9 size; +1 for descending.</summary>
        public int SortBy { get; set; } = 1;
        /// <summary>0 mixed, 1 folders first, 2 folders last.</summary>
        public int FolderOrder { get; set; }

        /// <summary>When this folder's settings were last written (UTC ticks); used to pick the live one among duplicates.</summary>
        public long SavedAt { get; private set; }

        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Path.GetFileName(Folder.TrimEnd('\\')) : Title;
        public string FolderKey => Folder.TrimEnd('\\').ToUpperInvariant();
        public bool FolderExists => !string.IsNullOrEmpty(Folder) && Directory.Exists(Folder);
        private string KeyPath => $@"{FoldersKey}\{Id}";

        public void Save()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key.SetValue("Folder", Folder);
                key.SetValue("Title", Title);
                key.SetValue("PosX", Num(PosX));
                key.SetValue("PosY", Num(PosY));
                key.SetValue("IsLocked", IsLocked.ToString());
                key.SetValue("OpenTrigger", ((int)OpenTrigger).ToString());
                key.SetValue("CardWidth", Num(CardWidth));
                key.SetValue("CardHeight", Num(CardHeight));
                key.SetValue("IconSize", Num(IconSize));
                key.SetValue("ShowHiddenFiles", ShowHiddenFiles.ToString());
                key.SetValue("ShowFileExtension", ShowFileExtension.ToString());
                key.SetValue("ShowShortcutArrow", ShowShortcutArrow.ToString());
                key.SetValue("OpenSubfoldersInside", OpenSubfoldersInside.ToString());
                key.SetValue("SortBy", SortBy.ToString());
                key.SetValue("FolderOrder", FolderOrder.ToString());
                StampSaved(key);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[FolderConfig:{DisplayTitle}] Save failed: {ex.Message}");
            }
        }

        /// <summary>Saves only the icon position (called on every move, so keep it cheap).</summary>
        public void SavePosition()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key.SetValue("PosX", Num(PosX));
                key.SetValue("PosY", Num(PosY));
                StampSaved(key);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[FolderConfig:{DisplayTitle}] SavePosition failed: {ex.Message}");
            }
        }

        private void StampSaved(RegistryKey key)
        {
            SavedAt = DateTime.UtcNow.Ticks;
            key.SetValue("SavedAt", SavedAt.ToString(CultureInfo.InvariantCulture));
        }

        public void Delete()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[FolderConfig:{DisplayTitle}] Delete failed: {ex.Message}");
            }
        }

        public static List<FolderConfig> LoadAll()
        {
            var result = new List<FolderConfig>();
            using var foldersKey = Registry.CurrentUser.OpenSubKey(FoldersKey);
            if (foldersKey == null) return result;

            foreach (string id in foldersKey.GetSubKeyNames())
            {
                using var key = foldersKey.OpenSubKey(id);
                if (key == null) continue;
                var c = new FolderConfig { Id = id };
                c.Folder = Str(key, "Folder", "");
                c.Title = Str(key, "Title", "");
                c.PosX = Dbl(key, "PosX", c.PosX);
                c.PosY = Dbl(key, "PosY", c.PosY);
                c.IsLocked = Bool(key, "IsLocked", false);
                c.OpenTrigger = (OpenTrigger)Math.Clamp((int)Dbl(key, "OpenTrigger", 0), 0, 2);
                c.CardWidth = Dbl(key, "CardWidth", c.CardWidth);
                c.CardHeight = Dbl(key, "CardHeight", c.CardHeight);
                c.IconSize = Dbl(key, "IconSize", c.IconSize);
                c.ShowHiddenFiles = Bool(key, "ShowHiddenFiles", false);
                c.ShowFileExtension = Bool(key, "ShowFileExtension", false);
                c.ShowShortcutArrow = Bool(key, "ShowShortcutArrow", true);
                c.OpenSubfoldersInside = Bool(key, "OpenSubfoldersInside", false);
                c.SortBy = (int)Dbl(key, "SortBy", 1);
                c.FolderOrder = (int)Dbl(key, "FolderOrder", 0);
                c.SavedAt = long.TryParse(Str(key, "SavedAt", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) ? t : 0;
                if (!string.IsNullOrEmpty(c.Folder)) result.Add(c);
            }
            return result;
        }

        private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Str(RegistryKey key, string name, string fallback) => key.GetValue(name)?.ToString() ?? fallback;

        private static bool Bool(RegistryKey key, string name, bool fallback) =>
            bool.TryParse(key.GetValue(name)?.ToString(), out bool b) ? b : fallback;

        /// <summary>Accepts invariant values and, as a fallback, comma decimals written with a Spanish-style locale.</summary>
        private static double Dbl(RegistryKey key, string name, double fallback)
        {
            string? s = key.GetValue(name)?.ToString();
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && !s.Contains(',')) return v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v)) return v;
            if (double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }
    }
}
