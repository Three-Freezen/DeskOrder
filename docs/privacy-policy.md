# DeskOrder 隐私政策 / Privacy Policy

**生效日期 / Effective Date: 2026-08-30**

DeskOrder（秩序桌面）由 Three-Freeze 开发。我们设计的核心原则是：**所有数据都留在你的电脑上**。本政策说明 DeskOrder 存储哪些数据、是否有联网行为，以及你如何完全掌控自己的数据。

DeskOrder (秩序桌面) is developed by Three-Freeze. Our core design principle: **all of your data stays on your computer**. This policy explains what data DeskOrder stores, when it uses the network, and how you stay in full control of your data.

## 1. 我们不收集任何数据 / We Collect Nothing

DeskOrder **没有账号系统，没有遥测，没有统计埋点，没有广告 SDK**。我们不会收集、上传或出售你的任何个人信息、文件信息或使用习惯。

DeskOrder has **no accounts, no telemetry, no analytics, and no ad SDKs**. We never collect, upload, or sell your personal information, file information, or usage habits.

## 2. 留在本地的数据 / Data Stored Locally

以下数据只保存在你自己的电脑上。默认（标准模式）路径：

The following data is stored only on your computer. Default (standard mode) locations:

- **配置、预设、便签与提醒**：`%APPDATA%\DesktopZones`
- **本地日志**：`%LOCALAPPDATA%\DeskOrder\logs`，仅用于排查问题的调试日志，只写本地文件

- **Configuration, presets, notes and reminders**: `%APPDATA%\DesktopZones`
- **Local logs**: `%LOCALAPPDATA%\DeskOrder\logs` — debug logs for troubleshooting, written to local files only

其中：配置文件是分区布局、样式设置与偏好选项；预设是你保存的样式预设卡；便签与提醒是便签内容和日历待办。

Specifically: configuration covers zone layouts, style settings and preferences; presets are the style preset cards you save; notes and reminders are sticky note contents and calendar to-dos.

如果你在安装时选择了「便携模式」（数据保存在软件安装文件夹），以上所有数据（含日志）统一存放在安装目录的 `Data` 文件夹中。

If you chose "Portable mode" (data kept in the app's installation folder) during setup, all of the above (including logs) lives in the `Data` folder inside the installation directory.

分区里的图标只是指向原文件的引用。DeskOrder **不复制、不移动、不上传**你的任何文件。

Icons in a zone are only references to the original files. DeskOrder **never copies, moves, or uploads** any of your files.

## 3. 联网行为因发行渠道而异 / Network Use Depends on the Distribution Channel

DeskOrder 应用本身**不主动连接任何服务器**。唯一涉及网络的是「获取新版本」，具体行为取决于你从哪个渠道获取应用：

The DeskOrder application **does not connect to any server on its own**. The only network-related matter is obtaining new versions, and it depends on where you got the app:

**Microsoft Store 版（MSIX 包）**

- 更新完全由 Microsoft Store 负责下载和安装，走微软自己的更新通道
- 应用自身不执行更新检查，**不连接 GitHub，也不连接任何其他服务器**
- 商店相关的下载与安装事务由微软处理，适用[微软隐私声明](https://privacy.microsoft.com/privacystatement)

**Microsoft Store version (MSIX package)**

- Updates are downloaded and installed entirely by the Microsoft Store through Microsoft's own update channel
- The app itself performs no update checks and **connects to neither GitHub nor any other server**
- Store-related download and installation matters are handled by Microsoft under the [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement)

**GitHub 版（安装包 / 便携版）**

- 唯一的联网场景是**检查和下载新版本**：你可以在「设置」里手动检查，应用也会每隔约 24 小时在后台检查一次，有新版本时仅通过系统托盘提示
- 版本信息通过 GitHub Releases API 获取，更新包从 GitHub 的服务器下载
- 这个过程**不包含你的任何个人数据**——请求中只有版本号等应用自身信息
- 你也可以在设置中关闭自动检查更新，关闭后仅在手动点击时联网

**GitHub version (installer / portable)**

- The only network use is **checking for and downloading updates**: check manually in Settings, or the app checks in the background about every 24 hours and only notifies you via the system tray when a new version exists
- Version information is fetched from the GitHub Releases API, and the update package is downloaded from GitHub's servers
- This traffic **contains none of your personal data** — requests carry only the app's own version information
- You can also turn off automatic update checks in Settings; after that, the network is used only when you check manually

## 4. 第三方服务 / Third-Party Services

- **Microsoft Store 版**：Microsoft Store（更新与安装），由微软运营
- **GitHub 版**：GitHub Releases（仅用于获取版本信息和下载更新包）
- 除此之外，DeskOrder 不连接任何第三方服务，不内嵌任何统计或广告组件

- **Microsoft Store version**: the Microsoft Store (updates and installation), operated by Microsoft
- **GitHub version**: GitHub Releases (used only to fetch version information and download updates)
- Beyond this, DeskOrder connects to no third-party services and embeds no analytics or advertising components

## 5. 数据的去留 / Keeping or Deleting Your Data

- 所有本地数据随时可以查看、备份或删除：标准模式管理 `%APPDATA%\DesktopZones` 与 `%LOCALAPPDATA%\DeskOrder\logs` 即可；便携模式下所有数据都在安装目录的 `Data` 文件夹里
- 卸载应用不会自动删除这些数据；如果你想彻底清除，手动删除对应文件夹即可
- 通过 Microsoft Store 卸载应用时，微软可能同时清理应用的商店数据，适用微软隐私声明

- You can view, back up, or delete all local data at any time: in standard mode, manage `%APPDATA%\DesktopZones` and `%LOCALAPPDATA%\DeskOrder\logs`; in portable mode, everything is in the `Data` folder inside the installation directory
- Uninstalling the app does not remove this data; delete the corresponding folders manually if you want a complete cleanup
- When you uninstall through the Microsoft Store, Microsoft may also clean up store-related app data, subject to the Microsoft Privacy Statement

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

## 9. 免责声明 / Disclaimer

DeskOrder 是一个开源项目，源代码与说明文档均公开在项目仓库中。**使用前请仔细阅读项目仓库**（<https://github.com/Three-Freezen/DeskOrder>），充分了解软件的功能与行为后再决定是否使用。

DeskOrder is an open-source project; its source code and documentation are publicly available in the repository. **Please read the repository carefully before use** (<https://github.com/Three-Freezen/DeskOrder>) and make sure you understand what the software does before deciding to use it.

本软件按「现状」提供，不附带任何明示或暗示的担保。**在法律允许的最大范围内，对于因使用本软件而产生的任何直接或间接损失，开发者概不负责**，包括但不限于数据丢失、利润损失或业务中断。使用本软件即表示你已阅读并同意本政策及随软件分发的许可条款（MIT License）。

The software is provided "as is", without warranty of any kind, express or implied. **To the maximum extent permitted by applicable law, the developer shall not be liable for any direct or indirect loss arising from the use of this software**, including but not limited to data loss, lost profits, or business interruption. By using the software, you acknowledge that you have read and agreed to this policy and the license terms distributed with it (MIT License).

