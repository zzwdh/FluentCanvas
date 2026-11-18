<div align="center">

<!--
[![LOGO](Images/Ink%20Canvas%20Artistry.png?raw=true "LOGO")](# "LOGO")
-->

<img src="./Images/FluentCanvas.png" style="width:96px;"/>

# FluentCanvas
[![UPSTREAM](https://img.shields.io/badge/UpStream-WXRIW%2FInk--Canvas-red.svg "LICENSE")](https://github.com/WXRIW/Ink-Canvas)
[![LICENSE](https://img.shields.io/badge/License-GPL--3.0-red.svg "LICENSE")](./LICENSE)
[![Latest release](https://img.shields.io/github/release/InkCanvas/Ink-Canvas-Artistry.svg?style=shield)](https://github.com/InkCanvas/Ink-Canvas-Artistry/releases/latest)
[![GitHub issues](https://img.shields.io/github/issues/InkCanvas/Ink-Canvas-Artistry?logo=github)](https://github.com/InkCanvas/Ink-Canvas-Artistry/issues)
[![交流群](https://img.shields.io/badge/-%E4%BA%A4%E6%B5%81%E7%BE%A4%20617707731-blue?style=flat&logo=TencentQQ)](https://qm.qq.com/q/AC777tzmEw)

![Screenshot-1](./Images/Screenshot1.png)
![Screenshot-2](./Images/Screenshot2.png)
![Screenshot-2](./Images/Screenshot3.png)

</div>

## 👀 前言
使用和分发本软件（原名 Ink-Canvas-For-Annotation）前，请您应当且务必知晓相关开源协议，本软件基于 https://github.com/WXRIW/Ink-Canvas 修改而成。对于墨迹功能的相关 issue 提出，可以优先查阅 https://github.com/WXRIW/Ink-Canvas/issues 。

当前工程提供 Windows x64 和 ARM64 的自包含 MSIX。安装测试、签名、商店身份和构建命令见[构建与发布](docs/RELEASE.md)。上方截图来自项目历史版本。本次验收和截图见[验证记录](docs/VERIFICATION.md)。

## 🔧 特性
Support Active Pen (支持压感)

## ⚠️ 提示
- 对新功能的有效意见和合理建议，开发者会适时回复并进行开发。本软件并非商业性质软件，请勿催促开发者，耐心才能让功能更少 BUG、更加稳定。

> 等待是人类的一种智慧

## 📗 FAQ

### 点击放映后一翻页就闪退？
考虑是由于`Microsoft Office`未激活导致的，请自行激活

### 放映后画板程序不会切换到PPT模式？
如果你曾经安装过`WPS`且在卸载后发现此问题则是由于暂时未确定的问题所导致，可以尝试重新安装WPS
> “您好，关于您反馈的情况我们已经反馈技术同学进一步分析哈，辛苦您可以留意后续WPS版本更新哈~” --回复自WPS客服

另外，处在保护（只读）模式的PPT不会被识别

若因安装了最新版本的 WPS 而导致无法在 WPS 软件内进入 PPT 模式，可以尝试卸载 WPS 后，并清除电脑垃圾、注册表垃圾、删除电脑上所有带 "kingsoft" 名称的文件夹，重新安装 WPS 2021 后，（以上步骤可能有多余步骤），经测试在 WPS 内可以正常进入 PPT 模式。

### **安装后**程序无法正常启动？
本版 MSIX 最低安装版本为 Windows 10 内部版本 19044，包含 .NET Desktop Runtime 和 Windows App SDK。PowerPoint 放映集成需要桌面版 Office，日常白板及批注可以独立使用。

桌面控件采用 [iNKORE.UI.WPF.Modern](https://github.com/iNKORE-NET/UI.WPF.Modern)，由 iNKORE Studios 和该项目全体贡献者开发。完整归属说明见[第三方声明](docs/store/THIRD-PARTY.md)。

### 我该在何处提出功能需求和错误报告？

1. GitHub Issues

    功能需求：https://github.com/InkCanvas/Ink-Canvas-Artistry/labels/enhancement/new 

    错误报告：https://github.com/InkCanvas/Ink-Canvas-Artistry/labels/bug/new

2. Tencent QQ

    [![交流群](https://img.shields.io/badge/-%E4%BA%A4%E6%B5%81%E7%BE%A4%20617707731-blue?style=flat&logo=TencentQQ)](https://qm.qq.com/q/AC777tzmEw)
