# DeskOrder 隐私政策 / Privacy Policy

**生效日期 / Effective Date: 2026-08-30**

DeskOrder（秩序桌面）由 Three-Freeze 开发。我们设计的核心原则是：**所有数据都留在你的电脑上**。本政策说明 DeskOrder 存储哪些数据、是否有联网行为，以及你如何完全掌控自己的数据。

DeskOrder (秩序桌面) is developed by Three-Freeze. Our core design principle: **all of your data stays on your computer**. This policy explains what data DeskOrder stores, when it uses the network, and how you stay in full control of your data.

## 1. 我们不收集任何数据 / We Collect Nothing

DeskOrder **没有账号系统，没有遥测，没有统计埋点，没有广告 SDK**。我们不会收集、上传或出售你的任何个人信息、文件信息或使用习惯。

DeskOrder has **no accounts, no telemetry, no analytics, and no ad SDKs**. We never collect, upload, or sell your personal information, file information, or usage habits.

## 2. 留在本地的数据 / Data Stored Locally

以下数据只保存在你自己的电脑上，路径为 `%APPDATA%\DesktopZones`：

The following data is stored only on your computer, under `%APPDATA%\DesktopZones`:

- **配置文件**：分区布局、样式设置、偏好选项
- **预设**：你保存的样式预设卡
- **便签与提醒**：便签内容、日历待办
- **本地日志**：仅用于排查问题的调试日志，只写本地文件

- **Configuration**: zone layouts, style settings, preferences
- **Presets**: the style preset cards you save
- **Notes and reminders**: sticky note contents, calendar to-dos
- **Local logs**: debug logs for troubleshooting, written to local files only

分区里的图标只是指向原文件的引用。DeskOrder **不复制、不移动、不上传**你的任何文件。

Icons in a zone are only references to the original files. DeskOrder **never copies, moves, or uploads** any of your files.

## 3. 唯一的联网行为：更新检查 / The Only Network Use: Update Checks

DeskOrder 只在一个场景联网：**检查和下载新版本**。

DeskOrder uses the network in exactly one scenario: **checking for and downloading updates**.

- 你可以在「设置」里手动检查更新；应用也会每隔约 24 小时在后台检查一次，有新版本时仅通过系统托盘提示
- 更新信息通过 GitHub Releases API 获取；下载更新包时连接 GitHub 的服务器
- 这个过程**不包含你的任何个人数据**——请求中只有版本号等应用自身信息

- You can check manually in Settings; the app also checks in the background about every 24 hours and only notifies you via the system tray when a new version exists
- Update information is fetched from the GitHub Releases API; the update package is downloaded from GitHub's servers
- This traffic **contains none of your personal data** — requests carry only the app's own version information

你也可以在设置中关闭自动检查更新，关闭后仅在手动点击时联网。

You can also turn off automatic update checks in Settings; after that, the network is used only when you check manually.

## 4. 第三方服务 / Third-Party Services

- **GitHub Releases**：仅用于获取版本信息和下载更新包，详见上文
- 除此之外，DeskOrder 不连接任何第三方服务，不内嵌任何统计或广告组件

- **GitHub Releases**: used only to fetch version information and download updates, as described above
- Beyond this, DeskOrder connects to no third-party services and embeds no analytics or advertising components

如果你通过 Microsoft Store 获取 DeskOrder，安装与商店相关的事务由微软处理，适用[微软隐私声明](https://privacy.microsoft.com/privacystatement)。

If you get DeskOrder from the Microsoft Store, installation and store-related matters are handled by Microsoft under the [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement).

## 5. 数据的去留 / Keeping or Deleting Your Data

- 所有本地数据随时可以查看、备份或删除：直接管理 `%APPDATA%\DesktopZones` 文件夹即可
- 卸载应用不会自动删除该文件夹；如果你想彻底清除，手动删除它即可

- You can view, back up, or delete all local data at any time by managing the `%APPDATA%\DesktopZones` folder
- Uninstalling the app does not remove this folder; delete it manually if you want a complete cleanup

## 6. 儿童隐私 / Children's Privacy

DeskOrder 不收集任何数据，因此也不存在收集儿童个人信息的问题。

DeskOrder collects no data, so there is no collection of children's personal information.

## 7. 政策更新 / Changes to This Policy

政策如有更新，会随新版本发布并更新本页面的生效日期。重大变更会在版本说明中提示。

If this policy changes, updates ship with new versions and the effective date above is revised. Significant changes are called out in release notes.

## 8. 联系我们 / Contact

对隐私政策有任何疑问，欢迎在 GitHub 仓库提 Issue：

If you have any questions about this policy, open an issue on GitHub:

<https://github.com/Three-Freezen/DeskOrder/issues>
