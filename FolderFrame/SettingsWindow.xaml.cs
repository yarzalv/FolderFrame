using FolderFrame.Core;
using System.Diagnostics;
using System.Windows;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace FolderFrame
{
    /// <summary>App settings: the list of folders, default behaviour and autostart.</summary>
    public partial class SettingsWindow : FluentWindow
    {
        private readonly FolderController _controller;
        private readonly MainWindow _main;
        private bool _loading;

        /// <summary>Row shown in the folder list.</summary>
        public record FolderRow(FolderWindow Window, string Title, string Detail);

        public SettingsWindow(FolderController controller, MainWindow main)
        {
            InitializeComponent();
            _controller = controller;
            _main = main;

            _loading = true;
            DefaultTriggerBox.SelectedIndex = (int)_controller.DefaultOpenTrigger;
            DoubleClickToHideSwitch.IsChecked = main.DoubleClickToHide;
            StartOnLoginSwitch.IsChecked = main.StartOnLogin;
            _loading = false;

            VersionText.Text = $"FolderFrame {Process.GetCurrentProcess().MainModule?.FileVersionInfo.FileVersion}";
            RefreshList();
        }

        public void RefreshList()
        {
            string[] triggers = { "al pasar el cursor", "con un clic", "con doble clic" };
            var rows = _controller.Windows
                .Select(w => new FolderRow(w, w.Config.DisplayTitle,
                    $"{w.Config.Folder} · se abre {triggers[(int)w.Config.OpenTrigger]}{(w.Config.IsLocked ? " · bloqueada" : "")}"))
                .ToList();
            FoldersList.ItemsSource = rows;
            NoFoldersText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            _main.PickAndAddFolder();
            RefreshList();
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is FolderRow row) row.Window.Open();
        }

        private void ConfigureFolder_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not FolderRow row) return;
            var dialog = new FolderSettingsWindow(row.Window) { Owner = this };
            dialog.Closed += (_, _) => RefreshList();
            dialog.Show();
        }

        private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not FolderRow row) return;
            var dialog = new MessageBox
            {
                Title = "Quitar carpeta",
                Content = $"¿Quitar \"{row.Title}\" del escritorio?\nLa carpeta y sus archivos no se borran.",
                PrimaryButtonText = "Quitar",
                CloseButtonText = "Cancelar",
            };
            if (await dialog.ShowDialogAsync() != MessageBoxResult.Primary) return;
            _controller.RemoveFolder(row.Window);
            RefreshList();
        }

        private void DefaultTriggerBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading || DefaultTriggerBox.SelectedIndex < 0) return;
            _controller.DefaultOpenTrigger = (OpenTrigger)DefaultTriggerBox.SelectedIndex;
        }

        private void ApplyTriggerToAll_Click(object sender, RoutedEventArgs e)
        {
            _controller.ApplyOpenTriggerToAll((OpenTrigger)Math.Max(0, DefaultTriggerBox.SelectedIndex));
            RefreshList();
        }

        private void DoubleClickToHide_Click(object sender, RoutedEventArgs e)
        {
            _main.DoubleClickToHide = DoubleClickToHideSwitch.IsChecked == true;
        }

        private void StartOnLogin_Click(object sender, RoutedEventArgs e)
        {
            _main.StartOnLogin = StartOnLoginSwitch.IsChecked == true;
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Archivo de registro (*.reg)|*.reg",
                Title = "Exportar ajustes",
                FileName = $"FolderFrame_ajustes_{DateTime.Now:yyyy-MM-dd_HHmm}",
            };
            if (saveDialog.ShowDialog() != true) return;
            _controller.SaveAll();
            // Full path (no lookup in the app folder) and separate arguments (no quoting by hand)
            var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "reg.exe"))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            foreach (string arg in new[] { "export", $@"HKCU\{FolderConfig.RootKey}", saveDialog.FileName, "/y" }) psi.ArgumentList.Add(arg);
            Process.Start(psi);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _controller.RefreshWindows();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow { Owner = this }.ShowDialog();
        }
    }
}
