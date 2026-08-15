namespace DesktopZones.Models;

public class CalendarPreset : PresetRecord
{
    public DesktopCalendar Calendar { get; set; } = new();

    public CalendarPreset() { Kind = PresetKind.Calendar; }
}