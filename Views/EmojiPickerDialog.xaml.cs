using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Services;

namespace DesktopZones.Views;

public partial class EmojiPickerDialog : Window
{
    public string? SelectedEmoji { get; private set; }
    private readonly LocalizationService _loc = LocalizationService.Instance;

    private static readonly string[] Emojis =
    {
        "😀","😃","😄","😁","😅","😂","🤣","😊","😇","🙂","😉","😌","😍","🥰","😘","😗",
        "😋","😛","😜","🤪","😝","🤑","🤗","🤭","🤫","🤔","🤐","🤨","😐","😑","😶","😏",
        "😒","🙄","😬","😮","😯","😲","😳","🥺","😢","😭","😤","😡","🤬","😈","👿","💀",
        "👍","👎","👏","🙌","🤝","💪","✌️","🤞","👆","👇","👈","👉","🖐️","✋","👋","🤚",
        "❤️","🧡","💛","💚","💙","💜","🖤","🤍","🤎","💔","💕","💖","💗","💓","💝","💘",
        "⭐","🌟","✨","🔥","💧","💡","💎","🎉","🎊","🎈","🎁","🏆","🥇","🥈","🥉","🎖️",
        "📁","📂","📝","📌","📎","🔗","✂️","🔍","🔎","📐","📏","📕","📗","📘","📙","📚",
        "🎮","🎯","🎲","🎸","🎵","🎶","🎤","🎧","📷","🎥","🎬","📺","💻","🖥️","⌨️","🖱️",
        "🏠","🏢","🏫","🏥","🏦","🏪","🏬","🏭","🏯","🏰","🗼","🗽","🌍","🌎","🌏","🗺️",
        "🍕","🍔","🍟","🌭","🍿","🥤","☕","🍵","🍺","🍷","🥂","🍾","🎂","🍰","🍪","🍩",
        "🚗","🚕","🚌","🚎","🏎️","🚓","🚑","🚒","✈️","🚀","🛸","🚁","⛵","🚢","🚲","🏍️",
        "⚡","☀️","🌙","⭐","☁️","⛈️","🌈","❄️","☃️","⛄","🌊","🔥","💧","🌀","🌪️","🌍",
        "🕐","🕑","🕒","🕓","🕔","🕕","🕖","🕗","🕘","🕙","🕚","🕛","⏰","⏳","⌛","📅",
        "✅","❌","⚠️","🚫","➕","➖","➗","✖️","♾️","‼️","⁉️","❓","❔","❕","❗","〰️",
        "🔴","🟠","🟡","🟢","🔵","🟣","🟤","⚫","⚪","🟥","🟧","🟨","🟩","🟦","🟪","🟫",
    };

    public EmojiPickerDialog()
    {
        InitializeComponent();
        BuildGrid();
        ApplyLoc();
    }

    void BuildGrid()
    {
        foreach (var e in Emojis)
        {
            var btn = new Button
            {
                Content = e,
                Width = 34, Height = 34,
                FontSize = 18,
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = e
            };
            btn.Click += (s, _) =>
            {
                SelectedEmoji = ((Button)s!).Tag as string;
                DialogResult = true;
                Close();
            };
            EmojiGrid.Children.Add(btn);
        }
    }

    void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        TitleLabel.Text = cn ? "选择图标" : "Pick Emoji";
        CancelBtn.Content = cn ? "取消" : "Cancel";
    }

    void Cancel_Click(object s, RoutedEventArgs e) { DialogResult = false; Close(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonDown(e); try { DragMove(); } catch { } }
}
