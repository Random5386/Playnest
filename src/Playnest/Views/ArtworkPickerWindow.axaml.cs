using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Playnest.ViewModels;

namespace Playnest.Views;

/// <summary>
/// Lets the user pick a cover, icon or background from SteamGridDB. Closes with the downloaded image bytes,
/// or null when cancelled.
/// </summary>
public partial class ArtworkPickerWindow : Window
{
    public ArtworkPickerWindow()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close(null);
        UseButton.Click += async (_, _) => await UseChoice();
        // Clicking a picture selects it; double-clicking uses it straight away.
        AddHandler(Button.ClickEvent, OnPictureClick, RoutingStrategies.Bubble);
        AddHandler(DoubleTappedEvent, OnPictureDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || DataContext is not ArtworkPickerViewModel vm) return;
            vm.SearchCommand.Execute(null);
            e.Handled = true; // Enter searches here instead of pressing "Use this"
        };
        Opened += async (_, _) =>
        {
            if (DataContext is ArtworkPickerViewModel vm) await vm.StartAsync();
        };
    }

    private static ArtworkOption? OptionFrom(object? source) =>
        source is Control c && (c as Button ?? c.FindAncestorOfType<Button>()) is { } b && b.Classes.Contains("pick")
            ? b.DataContext as ArtworkOption
            : null;

    private void OnPictureClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ArtworkPickerViewModel vm && OptionFrom(e.Source) is { } option) vm.Choice = option;
    }

    private async void OnPictureDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ArtworkPickerViewModel vm || OptionFrom(e.Source) is not { } option) return;
        vm.Choice = option;
        e.Handled = true;
        await UseChoice();
    }

    private async Task UseChoice()
    {
        if (DataContext is not ArtworkPickerViewModel vm || !vm.HasChoice || vm.IsBusy) return;
        var bytes = await vm.DownloadChoiceAsync();
        if (bytes is not null) Close(bytes);
    }
}
