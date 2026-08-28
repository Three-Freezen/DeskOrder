using System.Windows;
using System.Windows.Controls;
using DesktopZones.Helpers;

namespace DesktopZones.Views.Pages;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        // 版本跟随程序集（csproj <Version>），不再硬编码。
        VersionText.Text = "v" + AppVersion.Current;
    }

    public void ApplyLoc() { /* AboutPage has no localized strings — placeholder for symmetry. */ }

    private void GitHubLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/Three-Freezen/DeskOrder") { UseShellExecute = true });
        }
        catch
        {
            // 浏览器打不开就静默——About 页的链接不是关键路径。
        }
    }
}
