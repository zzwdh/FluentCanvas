using System.Windows;

namespace FluentCanvas.Helpers;

public static class PaletteSwatch
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached(
        "Label", typeof(string), typeof(PaletteSwatch));

    public static string GetLabel(DependencyObject element) => (string)element.GetValue(LabelProperty);
    public static void SetLabel(DependencyObject element, string value) => element.SetValue(LabelProperty, value);
}
