# 浮窗功能修复计划

**日期**: 2026-08-22
**目标**: 修复浮窗相关的 5 个问题

---

## 问题清单

| # | 问题 | 优先级 | 类型 |
|---|------|--------|------|
| 1 | 完善未做的尾巴 | P2 | 收尾 |
| 2 | 完善拖动动画 | P2 | 动效 |
| 3 | 窗口弹出后标签栏上的对应标签不会消失 | P1 | Bug |
| 4 | 拖动标签栏的标签，窗口弹出且停留在鼠标拖出位置（浏览器式） | P1 | 新功能 |
| 5 | 弹出的窗口无法拖动，拖一下就程序卡住 | P0 | Bug |

---

## 修复方案

### Bug 5: 拖动卡住 (P0)

**根因分析**:
- `PropertyWindow.TitleBar_PreviewMouseMove` 中的 `DragMove()` 调用可能有问题
- 当鼠标移出窗口边界触发 `DockBackRequested` 后，如果未处理，会调用 `DragMove()`
- `DragMove()` 是阻塞调用，可能导致卡住

**修复方案**:
```csharp
// PropertyWindow.xaml.cs
// 在 TitleBar_PreviewMouseMove 中，移除 DragMove 调用
// 改为手动计算窗口位置（已在 inside 分支实现）
// 当鼠标移出窗口且 dock-back 未处理时，继续手动移动窗口
```

**改动文件**: `Views/Components/PropertyWindow.xaml.cs`

---

### Bug 3: 标签栏标签不消失 (P1)

**根因分析**:
- `PropertyWindowManager.PopOutTarget` 只清除了 `DockedPanel.Target`
- 没有清除 `DockedTabs` 中对应的标签

**修复方案**:
```csharp
// PropertyWindowManager.cs - PopOutTarget 方法
// 在清除 DockedPanel.Target 后，也关闭对应的标签
if (main.DockedTabs != null)
    main.DockedTabs.CloseTab(TargetKey(target));
```

**改动文件**: `Views/Components/PropertyWindowManager.cs`

---

### Feature 4: 浏览器式拖标签 (P1)

**需求**: 拖动 PropertyTabStrip 中的标签，可以拖出来变成独立的 PropertyWindow

**实现方案**:
1. 在 `PropertyTabStrip` 中检测拖拽距离 > 40px
2. 创建临时的拖拽视觉反馈（半透明窗口跟随鼠标）
3. 松手时：
   - 如果在 ManagementWindow 右侧 200px 内 → 吸附回去
   - 否则 → 调用 `PropertyWindowManager.PopOutTarget` 打开浮窗

**改动文件**:
- `Views/Components/PropertyTabStrip.xaml.cs` (添加拖拽脱离逻辑)
- `Views/Components/PropertyWindowManager.cs` (可能需要调整位置计算)

---

### P2: 完善拖动动画

**当前状态**:
- PropertyWindow 有入场动画（淡入 + 缩放）
- PropertyPanel 有切换动画（Opacity + TranslateX）
- Dock/Undock 按钮有翻转动画

**可优化**:
1. 浮窗拖动时添加平滑跟手效果
2. 关闭时添加淡出动画
3. Dock-back 时的动画过渡

**改动文件**:
- `Views/Components/PropertyWindow.xaml` (添加关闭动画)
- `Views/Components/PropertyWindow.xaml.cs` (优化拖动跟手)

---

### P2: 完善未做尾巴

**待检查项**:
1. 浮窗位置持久化是否正常工作
2. 多浮窗 Cascade 防重叠是否正常
3. TabStrip 预览标签 vs 固定标签逻辑
4. 关闭最后一个标签时的行为

---

## 执行顺序

1. **Phase 1**: 修复 Bug 5（拖动卡住）— 影响基本使用
2. **Phase 2**: 修复 Bug 3（标签不消失）— 简单修复
3. **Phase 3**: 实现 Feature 4（浏览器式拖标签）— 新功能
4. **Phase 4**: 完善动画和收尾工作

---

## 验证清单

- [ ] 浮窗可以正常拖动，不会卡住
- [ ] 从 ZoneWindow 打开浮窗后，DockedTabs 中的标签消失
- [ ] 可以从 TabStrip 拖拽标签出来变成独立浮窗
- [ ] 拖拽到 ManagementWindow 右侧可以吸附回去
- [ ] 浮窗关闭时有淡出动画
- [ ] 多个浮窗不会完全重叠
