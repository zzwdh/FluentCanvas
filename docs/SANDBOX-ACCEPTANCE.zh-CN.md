# 在干净 Windows 环境验证 MSIX

以管理员启用 Windows Sandbox 可选功能并按系统要求重启。第一次运行可能需要 Windows 下载沙盒客户端。使用 PowerShell 7.2 或更高版本。测试脚本在仓库的 `artifacts/sandbox/<时间>-<随机编号>` 建立本次运行目录。

安装包和证书应来自同一组本地构建结果。以下命令在仓库根目录运行，将变量指向实际输出目录。

```powershell
$packageDir = (Get-ChildItem .\artifacts\packages\Local -Directory |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$msix = (Get-ChildItem -LiteralPath $packageDir -Filter 'FluentCanvas.LocalTest_*_x64.msix' -File).FullName
$cert = Join-Path $packageDir 'FluentCanvas-LocalTest.cer'
.\eng\Test-WindowsSandbox.ps1 -Msix $msix -Certificate $cert -Wack
```

单独核对交付给用户的双架构包束时，先关闭上一台来宾机，再运行下面的命令。脚本检查 Windows 在 x64 环境中选择的架构、安装版本、开始菜单启动与实际画板窗口的 PerMonitorV2 DPI 感知。它通过辅助功能接口切换设置中的 Sentry 复选框，分别核对关闭后、进程重启后及重新启用后的界面状态和 LocalState 标记。

```powershell
$bundle = (Get-ChildItem -LiteralPath $packageDir -Filter '*.msixbundle' -File).FullName
.\eng\Test-WindowsSandbox.ps1 -Bundle $bundle -Certificate $cert
```

`-Wack` 需要在宿主机安装 Windows SDK 的 Windows App Certification Kit、Windows SDK for Windows Store Apps Contracts 和 Metadata，保留 MSI 安装缓存。脚本从已安装 SDK 复制认证组件及系统 WinMD 合约。完成应用启动和交互检查后，脚本在来宾机安装 WACK 并将平台元数据放入原有的 `Windows Kits/10/References/<版本>` 路径。沙盒关闭网络和剪贴板重定向，读取独立的只读输入目录，将结果写入本次运行的 `evidence` 目录。测试证书只导入来宾机的「受信任人」。原生 ARM64 的设备验收需要真实 ARM64 设备或对应架构的测试环境。

## 升级测试

以下命令从本地测试包生成包版本为 `5.0.1.0` 的专用样本。它保留当前应用二进制、包名和发布者，使用独立的临时测试签名。样本用于验证 Windows 按包身份原地升级时的数据保留，不能代替对某个历史商店版本的迁移测试。

```powershell
.\eng\New-UpgradeFixture.ps1 -Msix $msix
# 使用上一步输出的目录。
$older = Join-Path $PWD 'artifacts\upgrade-fixtures\<本次编号>'
.\eng\Test-WindowsSandbox.ps1 -Msix $msix -Certificate $cert -Wack `
    -UpgradeFixture (Join-Path $older 'upgrade-fixture.msix') `
    -FixtureCertificate (Join-Path $older 'upgrade-fixture.cer')
```

## 核对结果

- `progress.txt` 按时间记录安装、真实文件关联、书写、擦除、撤销重做、保存、重新启动、启动项、独立 WinUI 窗口、卸载与升级的结果。关闭单独的诊断窗口后主画板应能正常关闭。
- `Whiteboard.png` 来自在沙盒内打开数学课堂作品后的实际画面。样例包含一个自绘讲义图片和六条可编辑的 WPF 笔迹。`Diagnostics.png` 来自同一来宾中的诊断界面。
- `FluentCanvas.log` 可核对从操作系统文件关联进入应用后实际导入的笔迹和元素数。冷启动检查之前正常关闭进程，热启动检查同时核对主窗口进程编号。
- `wack.xml` 保留 Microsoft 认证工具的报告。汇总结论在 `REPORT/@OVERALL_RESULT`，各测试的明细位于 `REQUIREMENTS/REQUIREMENT/TEST`。工具退出码为 0 时报告仍可能有警告或失败。`complete.json` 区分 PASS、WARNING 和 FAIL。任一步失败时宿主脚本返回非零状态，有认证警告时脚本输出警告。另见 `wack-binary-reader.json`，它记录同一 WACK 组件检查安装路径和临时副本的 EXE 时返回的操作及结果。
- `tests/Document.Tests` 另行验证损坏文件、校验和、资源冲突与保存失败等边界。运行 `dotnet run --project tests/Document.Tests/Document.Tests.csproj -c Release --artifacts-path artifacts/document-tests`。

参考 [Windows Sandbox 配置](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file) 及 [Windows App Certification Kit](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit)。
