using CommunityToolkit.Mvvm.ComponentModel;
using HeapScope.Contracts;

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>View model of the shell window.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = $"{ProductInfo.Name} {ProductInfo.Version}";
}
