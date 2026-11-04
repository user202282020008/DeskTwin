# DeskTwin · 桌面分身

在同一个 Windows 上开出一个**真正独立的第二个桌面**：程序跑在独立的 Child Session 里，主桌面继续正常使用，两个桌面互不抢焦点。不同于虚拟机或窗口截图，它用的是 Windows 自带的 Child Session + 本机回环 RDP。

> 本项目派生自 [BetterGI](https://github.com/babalae/better-genshin-impact) 的桌面分身实现（RDP ActiveX 封装与会话启动方式参考并修改自该项目），按 **GPL-3.0** 发布。

## 功能

- **独立会话**：基于 `WTSEnableChildSessions` + `MsRdpClient10`（`ConnectToChildSession`），不是截图、不是虚拟桌面。
- **后台运行**：「断开」只关闭画面，分身里的程序继续跑；「结束分身」才注销会话。
- **挂托盘**：分身还在时关闭窗口，可以选择挂到系统托盘继续后台运行（不断开会话），托盘菜单可恢复窗口、断开画面、结束分身或退出。
- **两种分辨率模式**
  - 自适应：分身分辨率跟随窗口/全屏的物理像素，点对点显示、不缩放（窗口缩放停手后自动重连一次）。
  - 缩放：保持固定分辨率，窗口比例自动锁成该分辨率，画面铺满、不变形、无黑边。
- **全屏**：按显示器物理边界覆盖（含任务栏），系统的最大化按钮 / 双击标题栏 / `Win+↑` 都进入全屏；右上角悬浮「退出全屏」按钮，鼠标移到屏幕右上角才出现，移开自动隐藏。
- **截图**：`PrintScreen` 由本机接管（RDP 会把它转发给子会话），截图存到「图片\DeskTwin」并复制到剪贴板。
- **单栏界面**：自绘标题栏，一条栏同时是状态栏、标题栏与设置入口（右键或「选项」打开全部设置）。
- **单实例**：整机只允许一个 DeskTwin（`Global\` 命名互斥体，跨会话生效）；重复启动会把已开窗口唤到前台后自己退出。在桌面分身内部启动会被拒绝，避免嵌套分身。
- **登录凭据**：可选记住密码，用 Windows DPAPI 按当前用户加密保存在本地。
- **高 DPI**：按显示器 DPI 设置远程会话缩放，4K/2K 屏文字不会过小。
- **其它**：观看模式（本机键鼠不进入分身）、静音、窗口置顶、系统组合键发送到分身、在分身中以管理员权限启动程序、环境自检。

## 系统要求

- Windows 10 1809+ / Windows 11（需要 Child Session API，`wtsapi32.dll`）
- 以**管理员权限**运行（启用 Child Session、注销会话需要）
- 系统自带的 RDP ActiveX（`mstscax.dll`，`MsRdpClient10`），正常 Windows 均自带
- 需要远程桌面服务处于可用状态（`TermService`）

## 使用

1. 运行 `DeskTwin.exe`（需管理员）。
2. 首次可先在右键菜单的「登录凭据」里填当前 Windows 账户与密码（**密码，不是 PIN**），之后切换分辨率不再弹登录框。
3. 点「连接」。若弹出 Windows 登录框，输入本机账户密码。
4. 全屏、分辨率模式、截图、结束分身等都在顶部连接条右键菜单里。
5. 关机/重启前请先「结束分身」。

## 构建

需要 .NET 8 SDK：

```bash
# 普通构建
dotnet build -c Release

# 单文件、自包含发布（win-x64）
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o artifacts
```

构建产物固定输出到 `artifacts/`（已在 `.gitignore` 中，永远不入库），**只保留一份最新的 `DeskTwin.exe`**，不按版本建目录；源码历史交给 git 管理。

自检（不需要管理员，检查 XAML、DPAPI、全屏边界、分辨率换算等）：

```bash
dotnet bin/Release/net8.0-windows10.0.17763.0/DeskTwin.dll --self-test self-test.json
```

## 已知限制

- 一个 Windows 最多一个 Child Session。
- 部分软件（尤其是带反作弊或独占全屏的程序）不支持在两个会话同时运行。
- 不包含 BetterGI 针对原神定制的 Raw Input 相对鼠标转发，使用普通 RDP 鼠标。
- 像素级独占的界面（如某些游戏全屏）在分身里可能显示异常。

## 许可

本项目按 **GPL-3.0-only** 发布，无担保。许可证全文见 [LICENSE.txt](LICENSE.txt)。

核心 RDP 封装与会话启动方式派生自 BetterGI（GPL-3.0），参考源码提交 `9e3a0b008b063e144982818d46f00c00081ffbd2`。第三方说明见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
