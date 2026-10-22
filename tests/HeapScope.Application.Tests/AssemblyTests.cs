using System.Reflection;

namespace HeapScope.Application.Tests;

public class AssemblyTests
{
    [Fact]
    public void Project_under_test_loads()
    {
        Assembly.Load("HeapScope.Application").GetName().Name.ShouldBe("HeapScope.Application");
    }
}
