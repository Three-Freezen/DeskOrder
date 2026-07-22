# DesktopZones - 项目路线图 / Project Roadmap

## 当前版本 / Current Version: v1.0

### ✅ 已完成 / Completed
- [x] 透明悬浮分区窗口 / Transparent overlay zone windows
- [x] 四角缩放 / Four-corner resize
- [x] 标题栏拖动 / Title bar drag
- [x] 图标网格/自由排列 / Item grid/free layout
- [x] 拖放导入文件/文件夹 / Drag-drop file/folder import
- [x] 点击导入（文件/文件夹） / Click import (files/folders)
- [x] 右键菜单（打开/位置/重命名/删除） / Context menu (open/location/rename/delete)
- [x] 系统托盘 / System tray
- [x] 管理分区界面 / Zone management window
- [x] 三态开关（显示/最小化/彻底隐藏） / 3-state switch (show/minimize/full hide)
- [x] 边框颜色+透明度 / Border color + opacity
- [x] 内部填充颜色+透明度 / Fill color + opacity
- [x] 标题栏填充颜色+透明度 / Title bar fill + opacity
- [x] 按钮透明度 / Button opacity
- [x] 背景图片+透明度+裁剪+偏移+缩放 / Background image + opacity + crop + offset + zoom
- [x] Emoji 图标 / Emoji icon
- [x] 图标颜色自定义 / Icon color
- [x] 分区名称颜色 / Zone name color
- [x] 九色统一预设 / 9-color unified presets
- [x] 内置取色器 / Built-in color picker
- [x] 尺寸变化自动重排 / Auto-rearrange on resize
- [x] 配置持久化 / Config persistence
- [x] 开机自启 / Start with Windows
- [x] 多显示器支持 / Multi-monitor support

### 🔜 计划中 / Planned

#### v1.1 - 中英双语完善 / Bilingual Completion
- [ ] **全局语言切换实时生效** / Global language switch with real-time UI updates
- [ ] XAML 静态绑定改为动态绑定 / Dynamic bindings for context menus
- [ ] 所有弹窗跟随语言 / All dialogs follow language changes
- [ ] 语言偏好持久化 / Language preference persistence

#### v1.2 - UI统一 / UI Unification
- [ ] **管理分区界面UI统一** / Management window UI unification (dark theme consistency)
- [ ] 分区设置界面精简 / Settings dialog simplification
- [ ] 托盘菜单交互优化 / Tray menu interaction optimization
- [ ] 所有ComboBox暗色统一 / All ComboBox dark theme unification
- [ ] 色彩体系文档化 / Color system documentation

#### v1.3 - 用户体验优化 / UX Improvements
- [ ] **图标自动换行优化** / Item auto-wrap optimization (fix overlap)
- [ ] 分区模板（预设样式） / Zone templates (preset styles)
- [ ] 批量导入（扫描开始菜单） / Batch import (scan Start Menu)
- [ ] 分区吸附对齐（磁吸） / Zone snap alignment
- [ ] 撤销/重做 / Undo/redo
- [ ] 键盘快捷键 / Keyboard shortcuts

#### v1.3 - 高级功能 / Advanced Features
- [ ] 分区分组管理 / Zone group management
- [ ] 分区布局方案（一键切换） / Layout profiles (one-click switch)
- [ ] 时钟/天气/系统监控小组件 / Clock/weather/system monitor widgets
- [ ] 分区分享/导出 / Zone export/share
- [ ] 云同步配置 / Cloud sync config

#### v2.0 - 未来展望 / Future
- [ ] 插件系统 / Plugin system
- [ ] 脚本自动化 / Script automation
- [ ] 跨平台（Linux/macOS） / Cross-platform (Linux/macOS)

---

## 技术栈 / Tech Stack
- **语言**: C# (.NET 10)
- **框架**: WPF (Windows Presentation Foundation)
- **存储**: JSON (%APPDATA%\DesktopZones\config.json)
- **系统集成**: P/Invoke Win32 API
