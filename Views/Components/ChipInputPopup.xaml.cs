using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DesktopZones.Services;

namespace DesktopZones.Views.Components;

/// <summary>chip 输入类型：扩展名（.xxx）或文件名要素（任意子串）。</summary>
public enum ChipInputKind
{
    Extension,
    Token,
}

/// <summary>小型输入弹窗，用于新增 chip（扩展名 / 名称要素复用）。
/// 构造时传入标题、占位提示、错误信息、当前已存在列表（用于查重）。</summary>
public partial class ChipInputPopup : Window
{
    readonly string _invalidMsg;
    readonly string _duplicateMsg;
    readonly IReadOnlyCollection<string> _existing;
    readonly ChipInputKind _kind;
    readonly LocalizationService _loc = LocalizationService.Instance;

    public string Value => InputBox.Text.Trim();

    public ChipInputPopup(string title, string placeholder, string invalidMsg,
        string duplicateMsg, IReadOnlyCollection<string> existing,
        ChipInputKind kind = ChipInputKind.Extension)
    {
        InitializeComponent();
        TitleText.Text = title;
        InputBox.ToolTip = placeholder;
        _invalidMsg = invalidMsg;
        _duplicateMsg = duplicateMsg;
        _existing = existing;
        _kind = kind;
        OkBtn.Content = _loc["ZoneProp.AutoOrganize.Picker.Confirm"];
        CancelBtn.Content = _loc["ZoneProp.AutoOrganize.Picker.Cancel"];
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    bool ValidateForExtension()
    {
        var v = Value.ToLowerInvariant();
        if (!v.StartsWith(".") || v.Length < 2 || v.Length > 10)
        {
            ShowError(_invalidMsg);
            return false;
        }
        if (_existing.Any(e => string.Equals(e, v, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError(_duplicateMsg);
            return false;
        }
        return true;
    }

    bool ValidateForToken()
    {
        var v = Value;
        if (v.Length < 1 || v.Length > 50)
        {
            ShowError(_invalidMsg);
            return false;
        }
        if (_existing.Any(e => string.Equals(e, v, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError(_duplicateMsg);
            return false;
        }
        return true;
    }

    bool Validate() => _kind == ChipInputKind.Extension ? ValidateForExtension() : ValidateForToken();

    void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void TitleBar_Down(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }
}
