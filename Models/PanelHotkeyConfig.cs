namespace DesktopZones.Models;

/// <summary>
/// Panel keyboard shortcut state. Extracted from <see cref="AppConfig"/> so
/// the God class is smaller and the hotkey can be preserved by reference in
/// ConfigSaver without listing each field.
/// Note: <c>PanelCustomHotkeys</c> intentionally stays on AppConfig (orphan)
/// — it's the user-added extras list, not the primary toggle.
/// </summary>
public class PanelHotkeyConfig
{
    public bool PanelHotkeyEnabled { get; set; } = false;
    public int PanelHotkeyModifiers { get; set; } = 0x0006; // MOD_CONTROL | MOD_SHIFT
    public int PanelHotkeyKey { get; set; } = 0x50; // 'P'
}
