using System.Reflection;

namespace HeapScope.Storage.Tests;

public class AssemblyTests
{
    [Fact]
    public void Project_under_test_loads()
    {
        Assembly.Load("HeapScope.Storage").GetName().Name.ShouldBe("HeapScope.Storage");
    }
}
