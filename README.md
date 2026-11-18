# 屏幕扩展

在 Windows 电脑上把安卓手机或平板用作独立副屏。电脑端通过局域网扫描设备，按需启用虚拟屏，并在断开后停用该路虚拟屏。

## 安装和连接

1. 在 Windows 11 x64 电脑运行 `TabletDisplay-Setup.exe`。安装虚拟显示驱动可能需要管理员权限和重启。
2. 在安卓设备安装 `TabletDisplay-Android-12.8.apk`。
3. 选择网络连接方式。无线连接时，在 Windows 开启移动热点，让手机或平板加入该热点，然后点「扫描网络上的设备」。有线连接时，接上数据线，在安卓系统开启「USB 共享网络」，然后点「扫描 USB 设备」。
4. 让安卓应用停在首页。在 Windows「屏幕扩展控制台」的「设备」页选择设备并点「连接」。首次请在安卓端确认，再把它显示的配对码输入电脑控制台。
5. 连接后可在设备卡调整桌面分辨率、桌面刷新率、串流帧率、声音和摄像头。在「屏幕排列」页调整屏幕位置。

两端首页均提供「首次连接指引」。

两端都升级后，安卓首次接受控制台发起的连接时会保存该电脑的邀请凭据。已配对的设备升级后会确认一次，接下来的连接可直接在 Windows 点击「连接」。安卓应用需要停在首页。

有线连接可用支持「USB 共享网络」的安卓设备。用数据线连到电脑，在安卓系统打开「USB 共享网络」，让「屏幕扩展」停在首页，然后在 Windows 控制台点「扫描 USB 设备」。控制通道使用 USB 网卡时，设备卡显示「USB 已连接」。串流走 USB 网卡，无需 Windows 移动热点。GT6 已完成有线独立副屏实测。只提供充电、文件传输或 USB 调试的设备暂不能走此方式。

对于宽屏手机，安卓端可以选择「视频分辨率 → 原生全屏」，Windows 对应副屏可选「2200×1000 · 手机宽屏」。此组合在 GT6 上通过显示验证。

## 构建

克隆仓库后运行 `git submodule update --init --recursive`，获取安卓串流组件的固定版本。

- Windows 安装包需要 Windows PowerShell 5.1、.NET Framework 的 C# 编译器以及 .NET 8 SDK。运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\installer\Build-Setup.ps1 -Destination .\TabletDisplay-Setup.exe`。脚本编译控制台和虚拟摄像头，并下载及验证固定版本的依赖。
- Android 需要 JDK 17、Android SDK 和 `tablet-client/app/build.gradle` 指定的 NDK。运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-AndroidRelease.ps1 -Destination .\TabletDisplay-Android-12.8.apk`。首次构建会为当前 Windows 用户生成本地签名密钥。覆盖更新需要沿用原有签名。

## 验证范围

- 已在 Windows 11 与 MatePad、GT6 上验证热点扫描、配对、双独立副屏、按设备断开、单路复制后切回扩展，以及手机宽屏。
- 双副屏的独立停用依赖主机提供两条可单独管理的虚拟显示路径。全新安装如果两块副屏共用一个 MTT 适配器，Windows 可能仍列出已停用的逻辑目标。完整双路冷安装、双设备同时从复制恢复扩展仍待验证。
- 桌面控制台是已验收的连接入口。直接从安卓端点主机的流程尚无独立副屏准备检查。
- 虚拟摄像头第三方许可和依赖版本见 [第三方组件](installer/ThirdParty.md)。Android 源码基于 Moonlight Android，许可见 [GPL-3.0](tablet-client/LICENSE.txt)。
