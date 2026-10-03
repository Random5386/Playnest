using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Playnest.Models;
using Playnest.ViewModels;

namespace Playnest.Views;

public partial class LibraryView : UserControl
{
    private MainViewModel? _vm;

    public LibraryView()
    {
        InitializeComponent();
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Scroller.SizeChanged += (_, e) => UpdateViewport(e.NewSize.Width);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.LayoutChanged -= OnLayoutChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _vm.LayoutChanged += OnLayoutChanged;
            OnLayoutChanged();
        }
    }

    private void OnLayoutChanged()
    {
        if (_vm is null) return;
        Classes.Set("hover", _vm.HoverEffects);
        Classes.Set("qp-hover", _vm.Settings.QuickPlay == QuickPlayMode.OnHover);
        Classes.Set("qp-always", _vm.Settings.QuickPlay == QuickPlayMode.Always);
        UpdateViewport(Scroller.Bounds.Width);
    }

    private void UpdateViewport(double width)
    {
        if (_vm is null || width <= 0) return;
        // ScrollViewer padding (28 + 14) and room for the scroll bar.
        _vm.ViewportWidth = Math.Max(200, width - 28 - 14 - 14);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null) return;
        // A double-click on the quick play button is already a launch; don't start the game twice.
        if (e.Source is Control qp && (qp as Button ?? qp.FindAncestorOfType<Button>())?.Classes.Contains("quickplay") == true)
        {
            e.Handled = true;
            return;
        }
        if ((e.Source as Control)?.DataContext is not GameViewModel g) return;
        if (e.Source is Control c && c.FindAncestorOfType<Button>() is null) return;
        if (_vm.Settings.DoubleClick == DoubleClickAction.Play) _vm.Play(g);
        else
        {
            _vm.Select(g);
            if (!_vm.Settings.ShowDetailsPanel) _ = g.EditCommand.ExecuteAsync(null);
        }
        e.Handled = true;
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
