namespace DesktopZones.Models;

/// <summary>
/// 播放器预设（律动条 / 唱片机）。
///
/// 两种形态**分开存放**：<see cref="PresetKind.MediaPlayerBars"/> 落
/// <c>Presets/MediaPlayersBars</c>，<see cref="PresetKind.MediaPlayerVinyl"/> 落
/// <c>Presets/MediaPlayersVinyl</c>。形态创建后不可改，所以把两种形态的字段混在一起
/// 存没有意义 —— 混着放会让用户在唱片机的对话框里看到一堆律动条预设。
/// </summary>
public class MediaPlayerPreset : PresetRecord
{
    public DesktopMediaPlayer Player { get; set; } = new();
}
