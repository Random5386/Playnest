using Avalonia.Controls;
using Playnest.ViewModels;

namespace Playnest.Views;

public partial class EditGameWindow : Window
{
    public EditGameWindow()
    {
        InitializeComponent();
        SaveButton.Click += (_, _) =>
        {
            if (DataContext is EditGameViewModel vm && string.IsNullOrWhiteSpace(vm.Name)) return;
            Close(true);
        };
        CancelButton.Click += (_, _) => Close(false);
    }
}
