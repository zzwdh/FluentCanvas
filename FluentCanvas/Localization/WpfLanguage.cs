using System.Windows;
using System.Windows.Markup;

namespace FluentCanvas.Localization;

public static class WpfLanguage
{
    public static XmlLanguage Language => XmlLanguage.GetLanguage(UiText.ActiveCulture.IetfLanguageTag);

    public static FlowDirection TextDirection => UiText.ActiveCulture.TextInfo.IsRightToLeft
        ? FlowDirection.RightToLeft
        : FlowDirection.LeftToRight;
}
