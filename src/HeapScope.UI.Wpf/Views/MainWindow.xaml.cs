using HeapScope.UI.Wpf.ViewModels;

namespace HeapScope.UI.Wpf.Views;

/// <summary>Shell window. View-only code; behavior lives in <see cref="ShellViewModel"/>.</summary>
public partial class MainWindow : System.Windows.Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
