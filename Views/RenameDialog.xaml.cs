using DesktopZones.Services;
using System.Windows;
using System.Windows.Input;

namespace DesktopZones.Views;

public partial class RenameDialog : Window
{
    public string NewName { get; private set; }
    private readonly LocalizationService _loc = LocalizationService.Instance;

    public RenameDialog(string currentName)
    {
        InitializeComponent();
        NameInput.Text = currentName;
        NameInput.SelectAll();
        NameInput.Focus();
        NewName = currentName;
        ApplyLoc();
    }

    void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        TitleLabel.Text = cn ? "重命名" : "Rename";
        OkBtn.Content = cn ? "确定" : "OK";
        CancelBtn.Content = cn ? "取消" : "Cancel";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    { NewName = NameInput.Text.Trim(); DialogResult = true; Close(); }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void NameInput_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) Ok_Click(sender, e); else if (e.Key == Key.Escape) Cancel_Click(sender, e); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonDown(e); try { DragMove(); } catch { } }
}
