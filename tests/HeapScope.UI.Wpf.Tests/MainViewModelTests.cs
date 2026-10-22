using HeapScope.Contracts;
using HeapScope.UI.Wpf.ViewModels;

namespace HeapScope.UI.Wpf.Tests;

public class MainViewModelTests
{
    [Fact]
    public void Title_names_the_product_and_version()
    {
        var viewModel = new MainViewModel();

        viewModel.Title.ShouldBe($"HeapScope {ProductInfo.Version}");
    }
}
