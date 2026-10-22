using System.Reflection;
using System.Xml.Linq;

namespace HeapScope.Architecture.Tests;

public class LayeringTests
{
    public static TheoryData<string> SourceProjects()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            data.Add(Path.GetRelativePath(RepoPaths.Root, path));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Project_file_follows_the_dependency_table(string relativePath)
    {
        var project = ProjectInfo.LoadEvaluated(Path.Combine(RepoPaths.Root, relativePath));

        LayerRules.Check(project).ShouldBeEmpty();
    }

    [Fact]
    public void Every_source_project_is_scanned()
    {
        SourceProjects().Count.ShouldBeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void Compiled_assemblies_only_use_allowed_layers()
    {
        var violations = new List<string>();
        var checkedAssemblies = 0;
        foreach (var project in LayerRules.KnownProjects)
        {
            var assemblyName = LayerRules.AssemblyToProject.FirstOrDefault(p => p.Value == project).Key ?? project;
            var path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            if (!File.Exists(path))
            {
                continue; // Not created yet (for example Loaders.GcDump before sprint 1.8).
            }

            checkedAssemblies++;
            foreach (var reference in Assembly.LoadFrom(path).GetReferencedAssemblies())
            {
                var violation = LayerRules.CheckAssemblyReference(project, reference.Name ?? string.Empty);
                if (violation is not null)
                {
                    violations.Add(violation);
                }
            }
        }

        checkedAssemblies.ShouldBeGreaterThanOrEqualTo(10);
        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Checker_reports_ui_referencing_analysis()
    {
        var bad = XDocument.Parse("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\HeapScope.Application\HeapScope.Application.csproj" />
                <ProjectReference Include="..\HeapScope.Analysis\HeapScope.Analysis.csproj" />
                <PackageReference Include="Microsoft.Diagnostics.Runtime" />
              </ItemGroup>
            </Project>
            """);

        var violations = LayerRules.Check(ProjectInfo.Parse("HeapScope.UI.Wpf", bad));

        violations.Count.ShouldBe(2);
        violations.ShouldContain(v => v.Contains("HeapScope.Analysis", StringComparison.Ordinal));
        violations.ShouldContain(v => v.Contains("Microsoft.Diagnostics.Runtime", StringComparison.Ordinal));
    }

    [Fact]
    public void Checker_reports_core_referencing_anything()
    {
        var bad = XDocument.Parse("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
              </ItemGroup>
            </Project>
            """);

        LayerRules.Check(ProjectInfo.Parse("HeapScope.Core", bad)).ShouldHaveSingleItem();
    }

    [Fact]
    public void Checker_reports_disallowed_build_order_reference_and_unknown_project()
    {
        var bad = XDocument.Parse("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\HeapScope.Analysis\HeapScope.Analysis.csproj" ReferenceOutputAssembly="false" />
              </ItemGroup>
            </Project>
            """);

        LayerRules.Check(ProjectInfo.Parse("HeapScope.Cli", bad)).ShouldHaveSingleItem();
        LayerRules.Check(ProjectInfo.Parse("HeapScope.Unknown", bad)).ShouldHaveSingleItem();
    }

    [Fact]
    public void Assembly_check_reports_ui_using_a_loader_or_diagnostics_library()
    {
        LayerRules.CheckAssemblyReference("HeapScope.UI.Wpf", "HeapScope.Loaders.ClrMd").ShouldNotBeNull();
        LayerRules.CheckAssemblyReference("HeapScope.UI.Wpf", "Microsoft.Diagnostics.Runtime").ShouldNotBeNull();
        LayerRules.CheckAssemblyReference("HeapScope.UI.Wpf", "HeapScope.Contracts").ShouldBeNull();
        LayerRules.CheckAssemblyReference("HeapScope.Loaders.ClrMd", "Microsoft.Diagnostics.Runtime").ShouldBeNull();
    }

    [Fact]
    public void Assembly_check_reports_disallowed_package_assemblies()
    {
        LayerRules.CheckAssemblyReference("HeapScope.UI.Wpf", "StreamJsonRpc").ShouldNotBeNull();
        LayerRules.CheckAssemblyReference("HeapScope.Core", "Microsoft.Extensions.Logging.Abstractions").ShouldNotBeNull();
        LayerRules.CheckAssemblyReference("HeapScope.Analysis", "System.IO.Hashing").ShouldNotBeNull();
        LayerRules.CheckAssemblyReference("HeapScope.Storage", "System.IO.Hashing").ShouldBeNull();
    }

    [Fact]
    public void Imported_worker_reference_is_seen_as_build_order_only()
    {
        var ui = ProjectInfo.LoadEvaluated(Path.Combine(RepoPaths.Root, "src", "HeapScope.UI.Wpf", "HeapScope.UI.Wpf.csproj"));

        ui.BuildOrderOnlyReferences.ShouldContain("HeapScope.Worker");
        ui.ProjectReferences.ShouldNotContain("HeapScope.Worker");
    }
}
