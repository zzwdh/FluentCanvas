<div align="center">

<img src="./Images/FluentCanvas-Logo.png" width="96" alt="FluentCanvas"/>

# FluentCanvas
[![LICENSE](https://img.shields.io/badge/License-GPL--3.0-red.svg "LICENSE")](./LICENSE)
[![GitHub issues](https://img.shields.io/github/issues/zzwdh/FluentCanvas?logo=github)](https://github.com/zzwdh/FluentCanvas/issues)

</div>

FluentCanvas 是面向 Windows 的桌面批注和课堂白板软件，支持数位笔、白板分页、批注文件保存，以及 PowerPoint 和实验性 WPS 放映联动。

工程基于 [zhh135/FluentCanvas](https://github.com/zhh135/FluentCanvas)，保留 [Ink Canvas](https://github.com/WXRIW/Ink-Canvas) 和 [Ink Canvas Artistry](https://github.com/InkCanvas/Ink-Canvas-Artistry) 的上游署名。许可见 [GPL-3.0](LICENSE) 和[第三方声明](docs/store/THIRD-PARTY.md)。

使用 [FluentCanvas.sln](FluentCanvas.sln) 打开工程。Windows x64 和 ARM64 自包含 MSIX 的构建、签名和安装步骤见[构建与发布](docs/RELEASE.md)。历史验收与截图见 [5.0.2.0 验证记录](docs/VERIFICATION.md)。

## 工程结构

| 范围 | 维护入口 |
| --- | --- |
| 启动与窗口 | [Program](FluentCanvas/Program.cs) 分派画板和 WinUI 工具进程。[App](FluentCanvas/App.xaml.cs) 负责文件激活、托盘及主窗口的服务装配。通知和翻页视图绑定到各自窗口拥有的模型。 |
| 画板核心 | [Rust 核心](native/fluent-canvas-core/) 持有页面、笔迹、图片和媒体的标识与场景布局。笔迹和图片可以一起变换、撤销及重做。空间索引用于擦除命中，局部擦除保留切点压感。椭圆、圆弧、三角形和坐标轴以控制点保存，重开后可继续编辑。 |
| Windows 画布与放映批注 | [CanvasCore](FluentCanvas.CanvasCore/) 将 WPF 输入转换为核心命令，将笔迹投影回画板，并处理图片、媒体与作品文件。桌面、白板和放映批注通过页面快照切换。PowerPoint 与 WPS 连接由 [Services](FluentCanvas/Services/) 管理。 |
| 设置 | WinUI 设置窗口通过 [SettingsPipe](FluentCanvas/Services/SettingsPipe.cs) 向画板进程提交请求。[SettingsPreferenceEditor](FluentCanvas/Services/SettingsPreferenceEditor.cs) 校验并保存普通偏好，主窗口应用对应的界面变化。 |
| 课堂工具 | [Classroom](FluentCanvas/Classroom/) 管理倒计时状态、名单抽取及文本文件读写。[WinUI 窗口](FluentCanvas/Views/) 负责输入、显示和窗口生命周期。 |

[文档回归](tests/Document.Tests/)、[画布输入回归](tests/CanvasInput.Tests/)和[生命周期回归](tests/Lifecycle.Tests/)各有独立入口，可在 Windows 上用 `dotnet run --project tests/<项目名> -c Release` 运行。WPS 加载项使用 `node --test tests/wps-addin.test.cjs`。

新保存的 `.icart` 使用 FCINK v2 分块无损保存笔迹，包括坐标、压感和倾角。作品内相同内容的图片或媒体共用一份资源，每个对象分别保存位置与变换。放映缓存按文稿及幻灯片保存同格式的 `.fcink` 文件。每个笔迹流带长度与 SHA-256 校验，保存期间可以继续书写。旧版 FluentCanvas 无法打开新版作品。

## 🔧 特性
界面语言默认跟随 Windows。也可以在画板工具栏打开「工具 → 设置 → 外观 → 界面语言」，选择语言，保存当前作品后重新打开 FluentCanvas。画板与课堂工具使用相同的语言。

Support Active Pen (支持压感)

## ⚠️ 提示
- 对新功能的有效意见和合理建议，开发者会适时回复并进行开发。本软件并非商业性质软件，请勿催促开发者，耐心才能让功能更少 BUG、更加稳定。

> 等待是人类的一种智慧

## 📗 FAQ

### 点击放映后一翻页就闪退？
考虑是由于`Microsoft Office`未激活导致的，请自行激活

### 放映后画板程序不会切换到PPT模式？
请在桌面版 Microsoft PowerPoint 中打开演示文稿并开始放映，再检查 FluentCanvas 的放映设置。诊断时请记录 Office 版本、文稿是否处于受保护视图，以及白板能否独立书写。本版已检查 PowerPoint 未运行和无可用演示窗口时的行为。实际放映和翻页的验收范围见[验证记录](docs/VERIFICATION.md)。

### 如何试用 WPS 演示联动？
在支持桌面 JavaScript 加载项的 WPS 中部署 [Fluent Canvas 演示加载项](integrations/wps/README.md)，再在 Fluent Canvas 设置中开启“WPS 放映”。实验功能可跟随页码并控制翻页和结束，批注保留到本次放映结束。

### **安装后**程序无法正常启动？
本版 MSIX 清单声明的最低安装版本为 Windows 10 1809（内部版本 17763，含 LTSC 2019）。同一份包面向 Windows 10 和 Windows 11，内置 .NET Desktop Runtime 与 Windows App SDK。Windows 10 的实机安装与运行仍待验收。PowerPoint 放映集成需要桌面版 Office，其中放映窗口截图需要 Windows 10 1903 或更新版本。日常白板及批注可以独立使用。

桌面控件采用 [iNKORE.UI.WPF.Modern](https://github.com/iNKORE-NET/UI.WPF.Modern)，由 iNKORE Studios 和该项目全体贡献者开发。完整归属说明见[第三方声明](docs/store/THIRD-PARTY.md)。

### 我该在何处提出功能需求和错误报告？

请在 [FluentCanvas Issues](https://github.com/zzwdh/FluentCanvas/issues) 提交功能需求或问题。报告问题时请附应用版本、Windows 内部版本、系统架构和复现步骤。
