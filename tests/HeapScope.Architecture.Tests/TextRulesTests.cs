namespace HeapScope.Architecture.Tests;

public class TextRulesTests
{
    private const char EnDash = (char)0x2013;
    private const char EmDash = (char)0x2014;

    private static readonly string[] s_extensions =
        [".cs", ".md", ".xaml", ".csproj", ".props", ".targets", ".json", ".ps1", ".editorconfig", ".gitignore"];

    [Fact]
    public void No_em_or_en_dashes_in_repository_text()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoPaths.Root, file);
            if (IsIgnored(relative) || !IsText(file))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (text.Contains(EnDash, StringComparison.Ordinal) || text.Contains(EmDash, StringComparison.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        offenders.ShouldBeEmpty();
    }

    private static bool IsText(string file) =>
        s_extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
        || s_extensions.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);

    private static bool IsIgnored(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is "bin" or "obj" or ".git" or "artifacts" or ".vs" or "TestResults");
    }
}
