using System.Reflection;

namespace HeapScope.Analysis.Tests;

public class AssemblyTests
{
    [Fact]
    public void Project_under_test_loads()
    {
        Assembly.Load("HeapScope.Analysis").GetName().Name.ShouldBe("HeapScope.Analysis");
    }
}
