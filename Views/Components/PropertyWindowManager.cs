using System;
using System.Collections.Generic;
using System.Windows;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views.Components;

public class PropertyWindowManager
{
    static PropertyWindowManager? _instance;
    public static PropertyWindowManager Instance => _instance ??= new PropertyWindowManager();

    readonly Dictionary<object, PropertyWindow> _windows = new();

    public void OpenOrFocus(object target, ConfigService configService, ManagementWindow? main)
    {
        if (_windows.TryGetValue(target, out var existing))
        {
            existing.Activate();
            return;
        }
        var w = new PropertyWindow(target, configService) { Owner = main };
        var config = configService.Load();
        if (!double.IsNaN(config.PropertyWindowX)) w.Left = config.PropertyWindowX;
        if (!double.IsNaN(config.PropertyWindowY)) w.Top = config.PropertyWindowY;
        w.Width = config.PropertyWindowWidth > 0 ? config.PropertyWindowWidth : 360;
        w.Height = config.PropertyWindowHeight > 0 ? config.PropertyWindowHeight : 600;
        w.LocationChanged += (_, _) =>
        {
            config.PropertyWindowX = w.Left;
            config.PropertyWindowY = w.Top;
            configService.Save(config);
        };
        w.SizeChanged += (_, _) =>
        {
            config.PropertyWindowWidth = w.Width;
            config.PropertyWindowHeight = w.Height;
            configService.Save(config);
        };
        w.Closed += (_, _) => _windows.Remove(target);
        _windows[target] = w;
        w.Show();
    }

    public void CloseWindow(object target)
    {
        if (_windows.TryGetValue(target, out var w)) w.Close();
    }
}