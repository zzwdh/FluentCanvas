# FluentCanvas 26.11.22.0 构建与安装

从仓库根目录运行以下命令。商店提交包版本为 26.11.22.0。发布者可在 Partner Center 核对该产品的历史最高版本。

## 环境

- Windows x64 或 ARM64 开发机，PowerShell 7.2 及以上。安装正式 .NET SDK **11.0.100**，运行 `dotnet --version` 确认。也可将对应架构的官方 SDK 压缩包解压到 `artifacts/sdk/dotnet-11.0.100`。PATH 上的 .NET 10 或更新版本的宿主可以自动查找这个目录。也可用 `-DotnetPath` 指定解压后的 `dotnet.exe`。
- 安装 Windows SDK **10.0.26100 或更新**的 MakeAppx、MakePri、SignTool 和 mt.exe。可用 `-WindowsSdkBin` 指定含这四个工具的目录。本次使用 Windows SDK 10.0.28000.0。
- 原生核心使用仓库 `rust-toolchain.toml` 固定的 Rust MSVC 工具链。安装 rustup 及 Visual Studio C++ 构建工具，为每个目标安装对应的 MSVC 链接器和库。在仓库根目录执行 `rustup target add x86_64-pc-windows-msvc aarch64-pc-windows-msvc`。常规构建和测试会把对应架构的 `fluent_canvas_core.dll` 复制到应用旁。打包脚本会把对应的原生 PDB 保存在本次构建的 `artifacts/build/<编号>/symbols/<架构>/`。
- 命令行打包脚本复用现有打包工程的资产清单和 AppxManifest 模板。用 Visual Studio 打开解决方案时，.NET 11.0.100 对应 VS 2026 18.12，另需 Windows Application Packaging Project / Desktop Bridge 组件。[SDK 与运行时](https://dotnet.microsoft.com/download/dotnet/11.0) · [WPF 打包](https://learn.microsoft.com/windows/apps/desktop/modernize/dotnet/package-app)
- 编译引用 `net11.0-windows10.0.19041.0`，MSIX 最低安装版本为 Windows 10 1809（内部版本 17763，含 LTSC 2019）。Windows 10 和 11 使用同一份包，两个 RID 分别是 `win-x64` 和 `win-arm64`。微软的 .NET 支持范围随各 Windows 版本与版本类别的生命周期变化。[.NET 支持矩阵](https://github.com/dotnet/core/blob/main/release-notes/11.0/supported-os.md) · [Windows App SDK 支持矩阵](https://learn.microsoft.com/windows/apps/windows-app-sdk/support)

## 本地测试包

```powershell
pwsh ./eng/Build-Packages.ps1 -Mode Local
```

默认一次构建两种架构。可在 PowerShell 中运行 `./eng/Build-Packages.ps1 -Architecture x64` 单独构建 x64。每次成功后控制台给出独立的输出目录，默认位于 `artifacts/packages/Local/`。其中有两个 MSIX、一份合并架构的 MSIXBundle、一张公钥证书和 SHA256SUMS.txt。选择 MSIXBundle 时 Windows 自动选取相应架构。

使用 `-LockedRestore` 可按仓库中记录的依赖版本构建。SDK 版本由 `global.json` 指定，自包含产物的 .NET Desktop 补丁由 `Directory.Build.props` 指定。调整 `Directory.Packages.props` 后分别还原常规项目和带 `-p:FluentCanvasUseWindowsAppSdkComponents=true` 的项目，再检查两个锁文件中的 x64 和 ARM64 依赖。

使用 `-WindowsAppSdkComponents` 可构建按需引用 WinUI 组件的对照包。该选项沿用自包含部署、文件关联、包标识方案和 WinUI 资源检查。它有独立的依赖锁文件，可与常规包分别用 `-OutputDirectory` 指定输出位置。

脚本为本地身份 `FluentCanvas.LocalTest` 和 `CN=FluentCanvas Local Test` 签名。默认按次生成短期测试证书，签名后清理临时私钥文件。要跨多个构建复用签名，可在开发者的当前用户个人证书存储区生成代码签名证书，传入其指纹。

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=FluentCanvas Local Test' -CertStoreLocation Cert:\CurrentUser\My
./eng/Build-Packages.ps1 -Mode Local -TestCertificateThumbprint $cert.Thumbprint
```

选择**干净测试机**，先核对产物的 SHA-256，再在管理员 PowerShell 中将同一批次的公钥证书导入本机 TrustedPeople。切回普通用户安装、启动应用。在该批次的输出目录执行下列命令。

```powershell
Get-FileHash ./*.msix* -Algorithm SHA256
Import-Certificate -FilePath .\FluentCanvas-LocalTest.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage -Path (Get-ChildItem ./*.msixbundle).FullName
```

从开始菜单打开 FluentCanvas。测试 Windows 11 x64 时也可使用一次性 Windows 沙盒。在启用 Windows 沙盒功能的开发机运行下列命令；两个文件应取自同一批输出。客机仅信任测试证书，测试记录写回 `artifacts/sandbox/`。带 `-Wack` 时需要安装或缓存 WACK 所需组件，具体见脚本。

```powershell
./eng/Test-WindowsSandbox.ps1 -Msix $x64PackagePath -Certificate $publicCertificatePath -Wack
```

升级检查可先运行 `./eng/New-UpgradeFixture.ps1 -Msix $x64PackagePath`，再将输出的 MSIX 和 CER 路径分别传给沙盒脚本的 `-UpgradeFixture` 与 `-FixtureCertificate`。该样本将本地测试包版本设为 5.0.1.0，以验证 Windows 对同一包身份的就地升级。验收还应单独覆盖已有真实商店版本的迁移。

`main`、`master` 和 `release/**` 的推送及目标为这些分支的拉取请求会自动运行文档、生命周期、画布输入和 WPS 加载项回归，并检查 x64、ARM64 安装包。发布验收应从一次成功构建选取同批次的 x64 MSIX、双架构 MSIXBundle、公开测试证书和 `SHA256SUMS.txt`。

```powershell
./eng/Test-WindowsSandbox.ps1 -Msix $x64PackagePath -Certificate $publicCertificatePath `
    -UpgradeFixture $previousPackagePath -FixtureCertificate $previousCertificatePath -Wack -RequireReleasePass
```

验收同时核对来宾机安装记录的 SHA-256、就地升级结果和 WACK 总结果。已有同一测试身份的历史包时，可用 `-PreviousVersion` 指定其实际版本。WACK 报告警告时，发布验收返回失败，保留 `complete.json` 和 `wack.xml` 供复查。应用包束的安装和启动可另在干净来宾机运行 `-Bundle` 检查。同批次的 ARM64 MSIX 需要在原生 ARM64 环境重复安装及升级验收。

在 Windows 10 1809 LTSC（17763）、21H2 LTSC（19044）和 22H2（19045）的干净系统上，使用同批次 x64 MSIX 测试安装、开始菜单启动、书写、保存与重新打开 `.icart`、文件关联和设置窗口。保持设置窗口开启，切换系统高对比度和动画设置，检查配色与动画是否即时更新。17763 上检查桌面和白板截图。19044 和 19045 上加测 PowerPoint 放映窗口截图。记录系统内部版本、包 SHA-256 和操作结果。

## 商店提交包

1. 在 Partner Center 的产品身份页复制真实的 Name、Publisher、PublisherDisplayName 与已保留的显示名称。从 `eng/StoreIdentity.example.json` 复制一份 JSON，填写这四项。核对 26.11.22.0 高于该产品所有既有包。调整版本时同步清单和程序集。
2. 在根目录运行下列命令。脚本在缺少身份时终止。构建时只把真实身份写入本次的暂存清单，仓库中的本地清单便于继续做隔离测试。
3. 在 `artifacts/packages/Store/` 的该次输出中选择 **.msixupload**。上传容器内含应用包束，调试符号留在本地构建目录。本地安装使用签名的 **.msix** 或 **.msixbundle**。商店上传交由 Partner Center 处理签名。[MSIX 商店要求](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/app-package-requirements)

```powershell
pwsh ./eng/Build-Packages.ps1 -Mode Store -StoreIdentity ./store-identity.json
```

每个架构自包含 .NET / WindowsDesktop **11.0.0** 和 **Windows App SDK 2.7.0**。构建时检查最终包的最低 Windows 版本、运行时依赖、PE 文件架构、EXE 入口、文件关联、启动任务与资源。应用没有采用裁剪或 Native AOT。[Windows App SDK 部署](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)

## 回归与认证

```powershell
dotnet run --project tests/Document.Tests -c Release --artifacts-path artifacts/document-tests
dotnet run --project tests/Lifecycle.Tests -c Release --artifacts-path artifacts/lifecycle-tests
```

在已安装 WACK 的交互式测试机上，可在管理员终端执行以下命令。其中 `$x64PackagePath` 是该次签名包的绝对路径。

```powershell
$appcert = "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\appcert.exe"
& $appcert reset
& $appcert test -appxpackagepath $x64PackagePath -reportoutputpath "$PWD\wack-report.xml"
```

正式放行时检查 XML 中每项结果与总结果。Sentry 的显式验证入口为 `FluentCanvas.exe --verify-sentry`，仅在错误上报已开启时发送一个测试异常，退出码 0 对应接收端 HTTP 成功，事件编号可用于后台比对。日常启动不会触发此验证。[WACK 使用说明](https://learn.microsoft.com/windows/uwp/debug-test-perf/windows-app-certification-kit)
