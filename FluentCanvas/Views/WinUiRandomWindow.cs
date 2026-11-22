using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentCanvas.Classroom;
using FluentCanvas.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentCanvas.Views;

internal sealed class WinUiRandomWindow
{
    private const int DefaultPeopleCount = 60;
    private const int MaximumNumericPeople = 10_000;
    private readonly bool single;
    private readonly WinUiAccessibility accessibility;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock rosterStatus = WinUiClassroomTools.Label(UiText.Get("Actions.RosterLoading"), 14);
    private readonly TextBlock status = WinUiClassroomTools.Label(UiText.Get("Actions.Ready"), 13);
    private readonly TextBlock featured = new() { Text = "—", FontSize = 58, TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly NumberBox population = Counter(UiText.Get("Actions.ParticipantCount"), DefaultPeopleCount, MaximumNumericPeople);
    private readonly NumberBox drawCount = Counter(UiText.Get("Actions.DrawCount"), 1, DefaultPeopleCount);
    private readonly Button draw = WinUiClassroomTools.Button(UiText.Get("Actions.StartDraw"), accent: true);
    private readonly Button edit = WinUiClassroomTools.Button(UiText.Get("Actions.EditRoster"));
    private readonly ListView output = new() { SelectionMode = ListViewSelectionMode.None, Visibility = Visibility.Collapsed };
    private readonly RosterStore store;
    private readonly WinUiRosterEditor editor;
    private ClassroomRoster roster = ClassroomRoster.Empty;
    private int numericPopulation = DefaultPeopleCount;
    private bool loaded;
    private bool busy;

    internal Window Window { get; }

    internal WinUiRandomWindow(bool single, string directory)
    {
        this.single = single;
        string title = single ? UiText.Get("Actions.DrawOne") : UiText.Get("Canvas.RandomPicker");
        Window = WinUiClassroomTools.CreateWindow(title);
        accessibility = new WinUiAccessibility(Window);
        var heading = WinUiClassroomTools.Label(title, 28);
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(rosterStatus, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(output, UiText.Get("Actions.DrawResult"));

        var top = new StackPanel { Spacing = 6 };
        top.Children.Add(heading);
        top.Children.Add(rosterStatus);
        var result = new Grid { MinHeight = single ? 104 : 140 };
        featured.VerticalAlignment = VerticalAlignment.Center;
        result.Children.Add(featured);
        result.Children.Add(output);
        var resultCard = accessibility.CreateCard(result, new Thickness(20, 14, 20, 14));

        var root = new Grid { Padding = new Thickness(single ? 20 : 26), RowSpacing = 12, MaxWidth = 880 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(top);
        Grid.SetRow(resultCard, 1);
        root.Children.Add(resultCard);
        Grid.SetRow(status, 2);
        root.Children.Add(status);

        if (!single)
        {
            var controls = new StackPanel { Spacing = 10 };
            var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            counts.Children.Add(population);
            counts.Children.Add(drawCount);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            buttons.Children.Add(draw);
            buttons.Children.Add(edit);
            controls.Children.Add(counts);
            controls.Children.Add(buttons);
            Grid.SetRow(controls, 3);
            root.Children.Add(controls);
        }

        store = new RosterStore(directory);
        editor = new WinUiRosterEditor(Window, store, lifetime.Token);
        draw.IsEnabled = false;
        edit.IsEnabled = false;
        draw.Click += async (_, _) => await PickAsync();
        edit.Click += async (_, _) => await EditRosterAsync();
        population.ValueChanged += (_, _) =>
        {
            if (roster.Names.Length == 0 && double.IsFinite(population.Value))
                numericPopulation = ReadCount(population, MaximumNumericPeople);
            UpdateCountLimit();
        };
        root.Loaded += async (_, _) => await LoadAsync();
        Window.Content = root;
        Window.Closed += (_, _) => lifetime.Cancel();
        WinUiClassroomTools.Resize(Window, single ? 480 : 750, single ? 320 : 560, center: true);
    }

    private int PoolSize => roster.Population(numericPopulation);

    private static NumberBox Counter(string label, int value, int maximum) => new()
    {
        Header = WinUiClassroomTools.Label(label, 14), Minimum = 1, Maximum = maximum,
        Value = value, MinWidth = 126, MaxWidth = 180,
        SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten
    };

    private static int ReadCount(NumberBox box, int max) => double.IsFinite(box.Value) ? (int)Math.Clamp(Math.Round(box.Value), 1, max) : 1;

    private async Task LoadAsync()
    {
        if (loaded) return;
        loaded = true;
        try { roster = await Task.Run(store.Read); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (lifetime.IsCancellationRequested) return;
            WinUiClassroomTools.Announce(status, UiText.Get("Actions.RosterReadFailed"));
        }
        if (lifetime.IsCancellationRequested) return;
        ApplyRoster();
        draw.IsEnabled = true;
        edit.IsEnabled = true;
        if (single) await PickAsync();
    }

    private void ApplyRoster()
    {
        bool names = roster.Names.Length > 0;
        population.IsEnabled = !names;
        population.Maximum = Math.Max(MaximumNumericPeople, PoolSize);
        population.Value = PoolSize;
        UpdateCountLimit();
        string source = names ? UiText.Format("Actions.RosterLoaded", roster.Names.Length) : UiText.Get("Actions.RosterUsingNumbers");
        if (roster.InvalidRules > 0) source += UiText.Format("Actions.InvalidRulesSkipped", roster.InvalidRules);
        WinUiClassroomTools.Announce(rosterStatus, source);
    }

    private void UpdateCountLimit()
    {
        drawCount.Maximum = PoolSize;
        if (drawCount.Value > PoolSize) drawCount.Value = PoolSize;
    }

    private async Task PickAsync()
    {
        if (busy || lifetime.IsCancellationRequested) return;
        SetBusy(true);
        draw.Content = UiText.Get("Actions.Drawing");
        int size = PoolSize;
        int count = single ? 1 : ReadCount(drawCount, size);
        var token = lifetime.Token;
        try
        {
            output.Visibility = Visibility.Collapsed;
            featured.Visibility = Visibility.Visible;
            featured.FontSize = 58;
            for (int i = 0; i < 5 && accessibility.AnimationsEnabled; i++)
            {
                featured.Text = roster.Label(Random.Shared.Next(size));
                await Task.Delay(135, token);
            }
            string[] results = roster.Draw(numericPopulation, count, Random.Shared);
            if (count == 1)
            {
                featured.Text = results[0];
                accessibility.Reveal(featured);
                WinUiClassroomTools.Announce(status, UiText.Format("Actions.SingleDrawResult", results[0]));
            }
            else
            {
                featured.Visibility = Visibility.Collapsed;
                output.ItemsSource = results;
                output.Visibility = Visibility.Visible;
                accessibility.Reveal(output);
                WinUiClassroomTools.Announce(status, UiText.Format("Actions.MultipleDrawResult", count));
            }
            draw.Content = UiText.Get("Actions.DrawAgain");
            if (single)
            {
                await Task.Delay(1500, token);
                Window.Close();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (!token.IsCancellationRequested) SetBusy(false);
        }
    }

    private async Task EditRosterAsync()
    {
        if (busy || lifetime.IsCancellationRequested) return;
        SetBusy(true);
        try
        {
            var saved = await editor.ShowAsync();
            if (saved != null && !lifetime.IsCancellationRequested)
            {
                roster = saved;
                ApplyRoster();
                WinUiClassroomTools.Announce(status, UiText.Get("Actions.RosterSaved"));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (!lifetime.IsCancellationRequested) WinUiClassroomTools.Announce(status, UiText.Format("Actions.RosterEditFailed", error.Message));
        }
        finally
        {
            if (!lifetime.IsCancellationRequested) SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        draw.IsEnabled = edit.IsEnabled = drawCount.IsEnabled = !value;
        population.IsEnabled = !value && roster.Names.Length == 0;
    }
}
