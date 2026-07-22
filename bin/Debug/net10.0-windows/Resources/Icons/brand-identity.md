# DesktopZones 品牌标识系统

## 品牌定位
**产品名称：** DesktopZones
**核心功能：** 桌面分区管理 — 将混乱的桌面划分为有序的工作区域
**目标用户：** 追求效率的 Windows 用户、多任务工作者、内容创作者
**品牌个性：** 专业、有序、高效、现代工具感

---

## 品牌标识方向

### 方向 A：几何分区 (Geometric Zones)
**概念：** 用几何网格直接表达"分区"功能
**关键词：** 秩序、网格、模块化、精确
**符号：** 被线条分割的矩形/正方形
**适用场景：** 工具类产品的标准标识

### 方向 B：窗口层叠 (Window Stack)
**概念：** 多个窗口层叠表达多区域管理
**关键词：** 多任务、层叠、组织、管理
**符号：** 重叠的半透明矩形
**适用场景：** 强调"多窗口管理"功能

### 方向 C：网格矩阵 (Grid Matrix)
**概念：** 规则的网格矩阵，象征有序排列
**关键词：** 矩阵、对齐、整齐、系统化
**符号：** 4×4 或 3×3 的规则网格
**适用场景：** 强调"整理/排列"功能

---

## 配色方案

### 主色板 (Primary Palette)
| 角色 | 色值 | 用途 |
|------|------|------|
| 主色 | `#1E88E5` | 品牌标识、主要按钮 |
| 辅色 | `#00ACC1` | 渐变、次级元素 |
| 强调 | `#039BE5` | 高亮、激活状态 |

### 中性色 (Neutral Palette)
| 角色 | 色值 | 用途 |
|------|------|------|
| 深色 | `#1A1A1A` | 主要文字 |
| 中性 | `#666666` | 次要文字 |
| 浅灰 | `#F5F5F5` | 背景 |
| 白色 | `#FFFFFF` | 卡片、内容区 |

### 语义色 (Semantic Colors)
| 角色 | 色值 | 用途 |
|------|------|------|
| 成功 | `#4CAF50` | 完成、激活分区 |
| 警告 | `#FF9800` | 注意、可调整 |
| 错误 | `#F44336` | 删除、不可用 |

---

## 字体系统

### 展示字体 (Display)
**首选：** Segoe UI Display / SF Pro Display
**备选：** Inter, system-ui
**用途：** 标题、Logo 文字、大号数字

### 正文字体 (Body)
**首选：** Segoe UI / SF Pro Text
**备选：** Inter, system-ui
**用途：** 正文、标签、说明文字

### 等宽字体 (Mono)
**首选：** JetBrains Mono / Cascadia Code
**用途：** 代码、数据、尺寸标注

---

## Logo 构成元素

### 1. 符号 (Symbol)
纯图形标识，无文字，用于：
- 应用图标 (16×16, 32×32, 48×48, 256×256)
- 状态栏/托盘图标
- Favicon
- 小尺寸应用

### 2. 字标 (Wordmark)
纯文字标识 "DesktopZones"，用于：
- 文档标题
- 网站头部
- 大尺寸展示

### 3. 组合标 (Combination)
符号 + 字标，用于：
- 应用启动画面
- 官方文档
- 品牌宣传

---

## 使用规则

### 最小尺寸
- 符号：16×16px (托盘) / 32×32px (标准)
- 字标：80px 宽度以上
- 组合标：120px 宽度以上

### 安全区域
符号周围保留 20% 的空白区域

### 禁止操作
- ❌ 拉伸变形
- ❌ 添加阴影/发光
- ❌ 改变颜色比例
- ❌ 在复杂背景上使用（需加底板）

---

## 图像生成 Prompt 模板

### 符号生成 Prompt
```
A modern minimalist app icon for "DesktopZones", a desktop partition management tool. 
The icon shows [VARIANT DESCRIPTION] in blue-cyan gradient (#1E88E5 to #00ACC1).
Clean geometric design, flat style, no shadows, white background.
Professional utility app aesthetic. 256x256px, square format.
```

### 字标生成 Prompt
```
Typography logo for "DesktopZones" desktop organization tool.
Clean sans-serif font (Inter or Segoe UI), bold weight.
Color: #1E88E5 (blue). Minimalist, professional, modern tech aesthetic.
Horizontal layout, white background.
```

### 组合标生成 Prompt
```
Logo combination mark for "DesktopZones" desktop partition management app.
Left side: geometric symbol showing [VARIANT].
Right side: "DesktopZones" text in clean sans-serif.
Blue-cyan gradient color scheme. Professional, modern, tool aesthetic.
White background, balanced composition.
```
