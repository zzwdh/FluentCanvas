using System.Windows;
using System.Windows.Automation;
using FluentCanvas.Localization;

namespace FluentCanvas.Helpers;

public static class ToolbarSelection
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(ToolbarSelection), new PropertyMetadata(false, OnSelectionChanged));

    public static bool GetIsActive(DependencyObject tool) => (bool)tool.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject tool, bool active) => tool.SetValue(IsActiveProperty, active);

    private static void OnSelectionChanged(DependencyObject tool, DependencyPropertyChangedEventArgs change) =>
        AutomationProperties.SetItemStatus(tool, (bool)change.NewValue ? UiText.Get("Canvas.CurrentTool") : string.Empty);
}
