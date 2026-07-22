using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DesktopZones.Models;

namespace DesktopZones.ViewModels;

public class ClockViewModel : INotifyPropertyChanged
{
    private DesktopClock _clock;
    public DesktopClock Clock
    {
        get => _clock;
        set { _clock = value; OnPropertyChanged(); }
    }

    private string _displayText = "";
    public string DisplayText { get => _displayText; set { _displayText = value; OnPropertyChanged(); } }

    private string _dateText = "";
    public string DateText { get => _dateText; set { _dateText = value; OnPropertyChanged(); } }

    private double _opacity = 1.0;
    public double Opacity { get => _opacity; set { _opacity = value; OnPropertyChanged(); } }

    private ClockDisplayMode _mode;
    public ClockDisplayMode Mode { get => _mode; set { _mode = value; OnPropertyChanged(); } }

    private bool _showSeconds = true;
    public bool ShowSeconds { get => _showSeconds; set { _showSeconds = value; OnPropertyChanged(); } }

    private bool _use24Hour = true;
    public bool Use24Hour { get => _use24Hour; set { _use24Hour = value; OnPropertyChanged(); } }

    // Analog clock angles (0-360 degrees)
    private double _hourAngle;
    public double HourAngle { get => _hourAngle; set { _hourAngle = value; OnPropertyChanged(); } }

    private double _minuteAngle;
    public double MinuteAngle { get => _minuteAngle; set { _minuteAngle = value; OnPropertyChanged(); } }

    private double _secondAngle;
    public double SecondAngle { get => _secondAngle; set { _secondAngle = value; OnPropertyChanged(); } }

    public ClockViewModel(DesktopClock clock)
    {
        _clock = clock;
        Opacity = clock.Opacity;
        Mode = clock.Mode;
        ShowSeconds = clock.ShowSeconds;
        Use24Hour = clock.Use24Hour;
    }

    public void UpdateTime(DateTime now)
    {
        // Digital display
        string fmt = Use24Hour ? (ShowSeconds ? "HH:mm:ss" : "HH:mm") : (ShowSeconds ? "hh:mm:ss tt" : "hh:mm tt");
        DisplayText = now.ToString(fmt);
        DateText = now.ToString("yyyy-MM-dd dddd");

        // Analog angles
        double sec = now.Second + now.Millisecond / 1000.0;
        double min = now.Minute + sec / 60.0;
        double hour = (now.Hour % 12) + min / 60.0;

        HourAngle = hour * 30.0;
        MinuteAngle = min * 6.0;
        SecondAngle = sec * 6.0;

        Opacity = _clock.Opacity;
        Mode = _clock.Mode;
        ShowSeconds = _clock.ShowSeconds;
        Use24Hour = _clock.Use24Hour;
    }

    public void ApplyToModel()
    {
        _clock.Opacity = Opacity;
        _clock.Mode = Mode;
        _clock.ShowSeconds = ShowSeconds;
        _clock.Use24Hour = Use24Hour;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
