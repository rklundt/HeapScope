using System.Windows.Input;
using HeapScope.UI.Wpf.ViewModels;

namespace HeapScope.UI.Wpf.Views;

/// <summary>Shell window. View-only code; behavior lives in <see cref="ShellViewModel"/>.</summary>
public partial class MainWindow : System.Windows.Window
{
    private readonly ShellViewModel _viewModel;

    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    // The mouse's back and forward buttons and Alt+Left and Alt+Right move through the Snapshot tab's history (sprint 1.5).
    // They are routed here because no WPF gesture covers the extra mouse buttons.
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewMouseDown(e);
        if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
        {
            e.Handled = Navigate(back: e.ChangedButton == MouseButton.XButton1);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewKeyDown(e);
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Right)
        {
            e.Handled = Navigate(back: e.SystemKey == Key.Left);
        }
    }

    private bool Navigate(bool back)
    {
        var navigator = _viewModel.Snapshots.Navigator;
        var command = back ? navigator.BackCommand : navigator.ForwardCommand;
        if (_viewModel.SelectedItem?.Page != _viewModel.Snapshots || !command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }
}
