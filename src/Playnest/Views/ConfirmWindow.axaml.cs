using Avalonia.Controls;

namespace Playnest.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow() : this("", "", "OK", false) { }

    public ConfirmWindow(string title, string message, string confirmText, bool danger)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = confirmText;
        if (danger) OkButton.Classes.Add("danger");
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
