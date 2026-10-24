using HeapScope.Core;
using HeapScope.Integration.Tests.Infrastructure;
using HeapScope.Loaders.ClrMd;

namespace HeapScope.Integration.Tests;

/// <summary>Sprint 1.5: field values, strings and reference paths read from a real dump against the known answers.</summary>
[Trait("Category", "Integration")]
public sealed class ObjectReaderTests(AllLeaksDump shared) : IClassFixture<AllLeaksDump>, IDisposable
{
    private readonly ClrMdObjectReader _reader = new(shared.DumpPath);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ulong Sample => shared.Dump.Instances("Fixtures.Leaks.InspectionSample").Single().Address;

    [Fact]
    public void Every_known_field_of_the_inspection_sample_reads_as_expected()
    {
        _reader.IsOpen.ShouldBeFalse("nothing is opened until something is read");

        var fields = Flatten(_reader.ReadFields(Sample, maxElements: 100, Ct));

        _reader.IsOpen.ShouldBeTrue();
        var answer = KnownAnswer.Load("Inspection");
        var mismatches = new List<string>();
        foreach (var expected in answer.Fields!)
        {
            if (!fields.TryGetValue(expected.Path, out var actual))
            {
                mismatches.Add($"{expected.Path}: missing (have {string.Join(", ", fields.Keys)})");
            }
            else if (expected.Value is { } value && actual.Value != value)
            {
                mismatches.Add($"{expected.Path}: {actual.Value}, expected {value}");
            }
            else if (expected.Type is { } type && (actual.Kind != ObjectFieldKind.Reference || actual.Value != type || actual.Address == 0))
            {
                mismatches.Add($"{expected.Path}: {actual.Kind} {actual.Value}, expected a reference to {type}");
            }
        }

        mismatches.ShouldBeEmpty();
        fields["Box"].Kind.ShouldBe(ObjectFieldKind.Struct);
        fields["Box.TopLeft"].Children.Count.ShouldBe(2, "a struct inside a struct is a nested row");
    }

    [Fact]
    public void Arrays_list_their_elements_up_to_the_limit_then_a_more_row()
    {
        var numbers = Flatten(_reader.ReadFields(Sample, 100, Ct))["Numbers"];

        var elements = _reader.ReadFields(numbers.Address, maxElements: 2, Ct);

        elements.Select(e => e.Value).ShouldBe(["3", "1", "3 more"]);
        elements[^1].Kind.ShouldBe(ObjectFieldKind.More);
    }

    [Fact]
    public void Strings_and_reference_paths_read_from_the_dump()
    {
        var fields = Flatten(_reader.ReadFields(Sample, 100, Ct));

        _reader.ReadString(fields["Text"].Address, 9).ShouldBe("HeapScope");
        _reader.ReadString(Sample, 10).ShouldBeNull("not a string");
        var paths = _reader.ReadReferencePaths(Sample, 100, Ct);
        paths[fields["Child"].Address].ShouldBe("Child");
        paths[fields["Box.Label"].Address].ShouldBe("Box.Label");
        paths[fields["Words"].Address].ShouldBe("Words");
        _reader.ReadReferencePaths(fields["Words"].Address, 100, Ct)[fields["Words[1]"].Address].ShouldBe("[1]");
        Should.Throw<ArgumentException>(() => _reader.ReadFields(0x10, 10, Ct));
    }

    [Theory]
    [InlineData("<Number>k__BackingField", "Number")]
    [InlineData("_count", "_count")]
    [InlineData("<>1__state", "<>1__state")]
    public void Backing_fields_are_named_by_their_property(string field, string expected)
    {
        ClrMdObjectReader.DisplayName(field).ShouldBe(expected);
    }

    public void Dispose() => _reader.Dispose();

    private static Dictionary<string, ObjectField> Flatten(IReadOnlyList<ObjectField> fields, string prefix = "")
    {
        var all = new Dictionary<string, ObjectField>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var path = field.Name.StartsWith('[') || prefix.Length == 0 ? prefix + field.Name : prefix + "." + field.Name;
            all[path] = field;
            foreach (var (childPath, child) in Flatten(field.Children, path))
            {
                all[childPath] = child;
            }
        }

        return all;
    }
}
