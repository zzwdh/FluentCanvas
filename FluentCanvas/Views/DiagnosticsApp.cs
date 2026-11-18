using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace FluentCanvas.Views;

// Run WinUI on its own STA and message loop so closing this window cannot end an ink session.
internal sealed partial class DiagnosticsApp : Application, IXamlMetadataProvider
{
    private readonly XamlControlsXamlMetaDataProvider provider = new();
    private Window window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        window = new Window { Title = "FluentCanvas · 诊断信息" };
        var details = $"FluentCanvas  {Assembly.GetExecutingAssembly().GetName().Version}\n" +
            $"{RuntimeInformation.FrameworkDescription}  ·  {RuntimeInformation.ProcessArchitecture}\n" +
            $"Windows  {Environment.OSVersion.Version}\n" +
            $"安装方式  {(App.IsPackaged ? "MSIX" : "独立运行")}\n" +
            "画布  WPF\n诊断窗口  WinUI 3";
        var panel = new StackPanel { Padding = new Thickness(24), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = "诊断信息", FontSize = 26 });
        panel.Children.Add(new TextBlock { Text = details, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap });
        var copy = new Button { Content = "复制诊断信息" };
        copy.Click += (_, _) =>
        {
            try
            {
                var data = new DataPackage();
                data.SetText(details);
                Clipboard.SetContent(data);
                Clipboard.Flush();
                copy.Content = "已复制";
            }
            catch (Exception) { copy.Content = "复制失败，请重试"; }
        };
        panel.Children.Add(copy);
        window.Content = new ScrollViewer { Content = panel };
        window.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(520, 410));
        window.Activate();
    }

    public IXamlType GetXamlType(Type type) => provider.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => provider.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => provider.GetXmlnsDefinitions();
}
