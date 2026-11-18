using Avalonia.Controls;
using Playnest.ViewModels;

namespace Playnest.Views;

public partial class ArtPickerWindow : Window
{
    public ArtPickerWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter || DataContext is not ArtPickerViewModel vm) return;
            e.Handled = true;
            vm.SearchCommand.Execute(null);
        };
        Opened += async (_, _) =>
        {
            try { if (DataContext is ArtPickerViewModel vm) await vm.StartAsync(); }
            catch (Exception ex) { Playnest.Services.Log.Error("artwork picker", ex); }
        };
    }
}
