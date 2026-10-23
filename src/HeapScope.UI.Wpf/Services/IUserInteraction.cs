using System.Windows;
using Microsoft.Win32;

namespace HeapScope.UI.Wpf.Services;

/// <summary>Dialogs a view model needs, behind an interface so view models stay testable without a window.</summary>
public interface IUserInteraction
{
    /// <summary>Asks for a dump file; null when the user cancels.</summary>
    string? PickDumpFile();

    /// <summary>Asks a yes or no question; true for yes.</summary>
    bool Confirm(string title, string message);
}

/// <summary>Standard Windows dialogs. Modal dialogs pump messages, so they never block the dispatcher.</summary>
public sealed class WpfUserInteraction : IUserInteraction
{
    public string? PickDumpFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a memory dump",
            Filter = "Memory dumps (*.dmp)|*.dmp|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(System.Windows.Application.Current?.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}

/// <summary>Answers every question with yes and picks no file; used by smoke runs, which must never block.</summary>
public sealed class AutomaticUserInteraction : IUserInteraction
{
    public string? PickDumpFile() => null;

    public bool Confirm(string title, string message) => true;
}
