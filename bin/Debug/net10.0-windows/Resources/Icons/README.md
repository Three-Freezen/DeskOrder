# DesktopZones 新图标

## 文件说明

| 文件 | 尺寸 | 用途 |
|------|------|------|
| `DesktopZones.ico` | 多尺寸 | 主图标文件（16/32/48/256px） |
| `icon-16.png` | 16×16 | 托盘小图标 |
| `icon-32.png` | 32×32 | 托盘标准图标 |
| `icon-48.png` | 48×48 | 高 DPI 托盘 |
| `icon-256.png` | 256×256 | 应用图标 |
| `logo-symbol-a.svg` | 矢量 | Logo 源文件 |

## 设计说明

**方案 A：几何分区**

- 四个不规则矩形区域，白色分割线
- 蓝青渐变配色（#1565C0 → #00BCD4）
- 直接表达"将桌面划分为多个区域"的核心功能
- 简洁、专业、易识别

## 使用方法

### 1. 已自动修改

`App.xaml.cs` 中的 `CreateAppIcon()` 方法已更新为从文件加载图标。

### 2. 确保文件在正确位置

```
DesktopZones/
└── Resources/
    └── Icons/
        ├── DesktopZones.ico
        ├── icon-16.png
        ├── icon-32.png
        ├── icon-48.png
        ├── icon-256.png
        └── logo-symbol-a.svg
```

### 3. 重新编译项目

```bash
dotnet build
```

### 4. 可选：嵌入为资源

如果希望将图标嵌入到程序集中，可以在 `.csproj` 文件中添加：

```xml
<ItemGroup>
  <Resource Include="Resources\Icons\DesktopZones.ico" />
</ItemGroup>
```

## 配色方案

- **主色**: `#1E88E5` (蓝色)
- **辅色**: `#00ACC1` (青色)
- **深色**: `#1565C0` (深蓝)
- **浅色**: `#42A5F5` (亮蓝)

## 字体

- **展示字体**: Segoe UI Display / SF Pro Display
- **正文字体**: Segoe UI / SF Pro Text
- **等宽字体**: JetBrains Mono / Cascadia Code
