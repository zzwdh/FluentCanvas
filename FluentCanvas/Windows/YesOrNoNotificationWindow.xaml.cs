using iNKORE.UI.WPF.Modern;
using System.Windows;

namespace FluentCanvas
{
    public partial class YesOrNoNotificationWindow : Window
    {
        internal YesOrNoNotificationWindow(string text, bool lightTheme)
        {
            InitializeComponent();
            Label.Text = text;
            SetResourceReference(ThemeManager.RequestedThemeProperty,
                lightTheme ? "LightConfirmationTheme" : "DarkConfirmationTheme");
        }

        private void ButtonYes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void ButtonNo_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
