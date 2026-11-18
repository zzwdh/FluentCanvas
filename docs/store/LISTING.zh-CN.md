# Microsoft Store 提交资料

## 简介

在 Windows 桌面上为讲义和幻灯片做批注，或切换到多页白板，保存后再接着讲。

## 详细介绍

FluentCanvas 面向课堂板书、屏幕演示和习题讲解。通过浮动工具栏选择画笔，在屏幕上标出重点；切换到白板后可以分多页书写。支持笔画擦除、选择、撤销和重做。作品可以保存在本地，并通过文件关联重新打开。

## 本版更新

- 提供 Windows x64 和 ARM64 安装包，随包提供 .NET 11 桌面运行时。
- 改进本地设置、作品保存及旧文档导入，保护正在编辑的画布。
- 加入可关闭的应用错误报告，以及可复制版本和架构信息的 WinUI 3 诊断窗口。

## 系统与素材

面向桌面 Windows，安装下限为内部版本 19044；使用受支持的 Windows 版本。普通鼠标可用于板书，压感功能需要兼容数位笔与驱动。无账号登录要求。分类建议为教育；搜索词可选「白板、板书、桌面批注、墨迹、课堂、演示」。

| 商品素材 | 文件与说明 |
| --- | --- |
| 商店图标 | [300 × 300 PNG](../../FluentCanvas.Package/Assets/Square150x150Logo.scale-200.png) |
| 桌面截图 | [3050 × 1668 PNG，几何课堂白板](screenshots/whiteboard-x64.png)。配图说明可填写「在白板中打开几何讲义，用可编辑的笔迹标注图形。」 |
| 审核参考 | [WinUI 3 诊断窗口实图](screenshots/diagnostics-x64.jpg)。单窗截图供审核人员识别诊断入口。 |

开始菜单图标、启动画面和宽幅图块随 MSIX 提供。填写商品名称时使用 Partner Center 已保留的名称。[Microsoft Store 图片要求](https://learn.microsoft.com/windows/apps/publish/publish-your-app/screenshots-and-images)

## 受限能力说明

`runFullTrust` 用于启动普通用户权限的 Windows 桌面进程。该进程通过 WPF 和 Win32 提供透明置顶的批注窗口、系统快捷键、键笔输入、用户选择的本地文件读写及可选的 PowerPoint COM 集成。默认关闭的桌面启动任务供用户自行启用。应用无驱动或常驻系统服务。

## 审核人员操作

1. 在开始菜单启动应用。打开浮动工具栏，切到白板，写两笔，擦除一笔，依次执行撤销与重做。切回桌面继续批注。
2. 从工具菜单保存作品，记录通知中显示的路径。退出后用 FluentCanvas 打开该作品。在同一实例运行时再次打开另一份文件。再打开一份损坏的测试副本，观察错误提示和原画布。
3. 在设置中修改画笔或主题，关闭并重新启动。到「关于 FluentCanvas」打开诊断信息，复制其中的版本和架构。可取消勾选 Sentry 错误上传。
4. 在启动设置中启用、关闭开机启动，核对 Windows 启动应用页。操作系统已禁止时，检查界面提示。
5. 有桌面 PowerPoint 时打开本地测试演示文稿，启动放映、翻页、批注并结束。无 Office 的设备上直接检查白板及桌面批注。

## 账号及提交待填项

Partner Center 的 Name、Publisher、PublisherDisplayName、保留的显示名称及已有最大包版本；实际开发者支持联系方式、公开隐私政策 URL、诊断保留期限、对应源代码 URL、分发权审查、内容评级问卷、目标市场和定价。填写时参照该版本真实截图和验证记录。[Microsoft Store 提交清单](https://learn.microsoft.com/windows/apps/publish/store-readiness-checklist)
