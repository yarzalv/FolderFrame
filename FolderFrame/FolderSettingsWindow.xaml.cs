using FolderFrame.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace FolderFrame
{
    /// <summary>Settings of a single folder. Changes apply when "Guardar" is pressed.</summary>
    public partial class FolderSettingsWindow : FluentWindow
    {
        private readonly FolderWindow _folder;

        public FolderSettingsWindow(FolderWindow folder)
        {
            InitializeComponent();
            _folder = folder;
            var c = folder.Config;
            TitleBar.Title = $"Configuración · {c.DisplayTitle}";

            TitleBox.Text = c.DisplayTitle;
            FolderBox.Text = c.Folder;
            OpenTriggerBox.SelectedIndex = (int)c.OpenTrigger;
            CardWidthSlider.Value = Math.Clamp(c.CardWidth, CardWidthSlider.Minimum, CardWidthSlider.Maximum);
            CardHeightSlider.Value = Math.Clamp(c.CardHeight, CardHeightSlider.Minimum, CardHeightSlider.Maximum);
            IconSizeSlider.Value = Math.Clamp(c.IconSize, IconSizeSlider.Minimum, IconSizeSlider.Maximum);

            int field = c.SortBy % 2 == 0 ? c.SortBy - 1 : c.SortBy;
            SortFieldBox.SelectedItem = SortFieldBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == field.ToString()) ?? SortFieldBox.Items[0];
            SortDirectionBox.SelectedIndex = c.SortBy % 2 == 0 ? 1 : 0;
            FolderOrderBox.SelectedIndex = Math.Clamp(c.FolderOrder, 0, 2);

            OpenSubfoldersInsideSwitch.IsChecked = c.OpenSubfoldersInside;
            ShowShortcutArrowSwitch.IsChecked = c.ShowShortcutArrow;
            ShowFileExtensionSwitch.IsChecked = c.ShowFileExtension;
            ShowHiddenFilesSwitch.IsChecked = c.ShowHiddenFiles;
            LockedSwitch.IsChecked = c.IsLocked;

            CardWidthSlider.ValueChanged += (_, _) => UpdateLabels();
            CardHeightSlider.ValueChanged += (_, _) => UpdateLabels();
            IconSizeSlider.ValueChanged += (_, _) => UpdateLabels();
            OpenTriggerBox.SelectionChanged += (_, _) => UpdateLabels();
            UpdateLabels();
        }

        private void UpdateLabels()
        {
            CardWidthValue.Text = $"{CardWidthSlider.Value:0} px";
            CardHeightValue.Text = $"{CardHeightSlider.Value:0} px";
            IconSizeValue.Text = $"{IconSizeSlider.Value:0} px";
            OpenTriggerHint.Text = OpenTriggerBox.SelectedIndex switch
            {
                0 => "Se abre al pasar el cursor y se cierra al salir de la tarjeta.",
                1 => "Se abre con un clic y se cierra al hacer clic fuera.",
                _ => "Se abre con doble clic y se cierra al hacer clic fuera.",
            };
        }

        private void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            using var picker = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Elige la carpeta",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(FolderBox.Text) ? FolderBox.Text : "",
            };
            if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                // Keep a custom name; follow the folder name if it was the default
                if (TitleBox.Text == Path.GetFileName(FolderBox.Text.TrimEnd('\\')))
                {
                    TitleBox.Text = Path.GetFileName(picker.SelectedPath.TrimEnd('\\'));
                }
                FolderBox.Text = picker.SelectedPath;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var c = _folder.Config;
            string folderName = Path.GetFileName(FolderBox.Text.TrimEnd('\\'));
            c.Title = TitleBox.Text.Trim() == folderName ? "" : TitleBox.Text.Trim();
            c.Folder = FolderBox.Text;
            c.OpenTrigger = (OpenTrigger)Math.Max(0, OpenTriggerBox.SelectedIndex);
            c.CardWidth = CardWidthSlider.Value;
            c.CardHeight = CardHeightSlider.Value;
            c.IconSize = IconSizeSlider.Value;
            int field = int.Parse((string)((ComboBoxItem)SortFieldBox.SelectedItem).Tag);
            c.SortBy = field + (SortDirectionBox.SelectedIndex == 1 ? 1 : 0);
            c.FolderOrder = Math.Max(0, FolderOrderBox.SelectedIndex);
            c.OpenSubfoldersInside = OpenSubfoldersInsideSwitch.IsChecked == true;
            c.ShowShortcutArrow = ShowShortcutArrowSwitch.IsChecked == true;
            c.ShowFileExtension = ShowFileExtensionSwitch.IsChecked == true;
            c.ShowHiddenFiles = ShowHiddenFilesSwitch.IsChecked == true;
            c.IsLocked = LockedSwitch.IsChecked == true;
            c.Save();

            _folder.ApplyConfig();
            _folder.ReloadFolder(); // picks up folder, filters, sorting and icon size
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
