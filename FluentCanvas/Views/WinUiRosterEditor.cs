using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentCanvas.Classroom;
using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace FluentCanvas.Views;

internal sealed class WinUiRosterEditor(Window window, RosterStore store, CancellationToken lifetime)
{
    internal async Task<ClassroomRoster> ShowAsync()
    {
        var original = await Task.Run(store.Read);
        if (lifetime.IsCancellationRequested || window.Content?.XamlRoot == null) return null;
        var names = Editor(UiText.Get("Actions.NamesPerLine"), original.NamesText);
        var replacements = Editor(UiText.Get("Actions.RulesPerLine"), original.ReplacementsText);
        var error = WinUiClassroomTools.Label("", 13);
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        var sections = new Pivot();
        sections.Items.Add(new PivotItem { Header = UiText.Get("Actions.Roster"), Content = EditorPanel(names, UiText.Get("Actions.ImportRoster"), null, error) });
        sections.Items.Add(new PivotItem { Header = UiText.Get("Actions.ReplacementRules"), Content = EditorPanel(replacements, UiText.Get("Actions.ImportReplacementRules"),
            UiText.Get("Actions.ReplacementRuleExample"), error) });
        var dialogContent = new StackPanel { Spacing = 6 };
        dialogContent.Children.Add(sections);
        dialogContent.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = window.Content.XamlRoot, Title = UiText.Get("Actions.RosterAndRules"), Content = dialogContent, PrimaryButtonText = UiText.Get("Actions.Save"),
            DefaultButton = ContentDialogButton.Primary, CloseButtonText = UiText.Get("Actions.Cancel")
        };
        WinUiClassroomTools.SetLanguage(dialog);
        ClassroomRoster saved = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            string updatedNames = names.Text, updatedReplacements = replacements.Text;
            try
            {
                saved = await Task.Run(() => store.Save(original, updatedNames, updatedReplacements));
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                args.Cancel = true;
                WinUiClassroomTools.Announce(error, failure.Message);
            }
            finally { deferral.Complete(); }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? saved : null;
    }

    private static TextBox Editor(string label, string text) => new()
    {
        Text = text, Header = label, AcceptsReturn = true, Height = 184, TextWrapping = TextWrapping.Wrap
    };

    private StackPanel EditorPanel(TextBox input, string importLabel, string guidance, TextBlock error)
    {
        var panel = new StackPanel { Spacing = 8 };
        if (guidance != null) panel.Children.Add(WinUiClassroomTools.Label(guidance, 13));
        ScrollViewer.SetVerticalScrollBarVisibility(input, ScrollBarVisibility.Auto);
        panel.Children.Add(input);
        var import = WinUiClassroomTools.Button(importLabel);
        import.Click += async (_, _) =>
        {
            import.IsEnabled = false;
            try
            {
                string path = await WinUiPickers.PickOpenFileAsync(WindowNative.GetWindowHandle(window),
                    "FluentCanvas.ImportRoster", new[] { ".txt" }, store.DirectoryPath);
                if (!string.IsNullOrWhiteSpace(path) && !lifetime.IsCancellationRequested)
                {
                    string text = await Task.Run(() => RosterStore.ReadText(path, allowMissing: false));
                    if (!lifetime.IsCancellationRequested)
                    {
                        input.Text = text;
                        error.Text = "";
                    }
                }
            }
            catch (Exception failure)
            {
                if (!lifetime.IsCancellationRequested) WinUiClassroomTools.Announce(error, UiText.Format("Actions.RosterImportFailed", failure.Message));
            }
            finally
            {
                if (!lifetime.IsCancellationRequested) import.IsEnabled = true;
            }
        };
        panel.Children.Add(import);
        return panel;
    }
}
