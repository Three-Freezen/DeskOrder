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
}
