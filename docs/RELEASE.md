# FluentCanvas 5.0.2.0 构建与安装

从仓库根目录运行以下命令。此版从 `cd73065` 的 5.0.1.0 递增到 5.0.2.0。商店产品的历史最高版本须由发布者在 Partner Center 核实。

## 环境

- Windows x64 或 ARM64 开发机，PowerShell 7.2 及以上。安装正式 .NET SDK **11.0.100**，运行 `dotnet --version` 确认。使用并存 SDK 时，可用 `-DotnetPath` 指定相应的 dotnet.exe。
- 安装 Windows SDK **10.0.26100 或更新**的 MakeAppx、MakePri、SignTool 和 mt.exe。可用 `-WindowsSdkBin` 指定含这四个工具的目录。本次使用 Windows SDK 10.0.28000.0。
- 命令行打包脚本复用现有打包工程的资产清单和 AppxManifest 模板，可在只安装上述 SDK 的机器上构建。用 Visual Studio 打开解决方案时，.NET 11.0.100 对应 VS 2026 18.12，另需 Windows Application Packaging Project / Desktop Bridge 组件。本次验证的是命令行流程。[SDK 与运行时](https://dotnet.microsoft.com/download/dotnet/11.0) · [WPF 打包](https://learn.microsoft.com/windows/apps/desktop/modernize/dotnet/package-app)
- API 目标为 `net11.0-windows10.0.19041.0`。MSIX 最低版本为 Windows 10 内部版本 19044，涵盖适用的 21H2 LTSC 版本。Microsoft 对各 Windows 版本的 .NET 支持随系统生命周期变化。两个 RID 分别是 `win-x64` 和 `win-arm64`。[.NET 支持矩阵](https://github.com/dotnet/core/blob/main/release-notes/11.0/supported-os.md)

## 本地测试包

```powershell
pwsh ./eng/Build-Packages.ps1 -Mode Local
```

默认一次构建两种架构。可在 PowerShell 中运行 `./eng/Build-Packages.ps1 -Architecture x64` 单独构建 x64。每次成功后控制台给出独立的输出目录，默认位于 `artifacts/packages/Local/`。其中有两个 MSIX、一份合并架构的 MSIXBundle、一张公钥证书和 SHA256SUMS.txt。选择 MSIXBundle 时 Windows 自动选取相应架构。

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

## 商店提交包

1. 在 Partner Center 的产品身份页复制真实的 Name、Publisher、PublisherDisplayName 与已保留的显示名称。从 `eng/StoreIdentity.example.json` 复制一份 JSON，填写这四项。核对 5.0.2.0 高于该产品所有既有包；需要调版本时同步清单和程序集。
2. 在根目录运行下列命令。脚本在缺少身份时终止。构建时只把真实身份写入本次的暂存清单，仓库中的本地清单便于继续做隔离测试。
3. 在 `artifacts/packages/Store/` 的该次输出中选择 **.msixupload**。上传容器内有应用包束和调试符号；本地安装使用签名的 **.msix** 或 **.msixbundle**。商店上传交由 Partner Center 处理签名。提交前还需完成对应身份的安装、WACK、商品资料和权限审核。[MSIX 商店要求](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/app-package-requirements)

```powershell
pwsh ./eng/Build-Packages.ps1 -Mode Store -StoreIdentity ./store-identity.json
```

每个架构自包含 .NET / WindowsDesktop **11.0.0** 和 **Windows App SDK 1.8.260804001**。构建时检查最终包的 runtimeconfig、deps、PE 文件架构、EXE 入口、文件关联、启动任务与资源。应用没有采用裁剪或 Native AOT。[Windows App SDK 部署](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)

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
