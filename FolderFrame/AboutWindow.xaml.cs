using System.Reflection;
using Wpf.Ui.Controls;

namespace FolderFrame
{
    public partial class AboutWindow : FluentWindow
    {
        public AboutWindow()
        {
            InitializeComponent();
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            VersionTextBlock.Text = $"Versión {v?.Major}.{v?.Minor}.{v?.Build}";
        }
    }
}
