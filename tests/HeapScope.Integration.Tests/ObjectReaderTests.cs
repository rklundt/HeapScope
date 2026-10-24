using HeapScope.Core;
using HeapScope.Integration.Tests.Infrastructure;
using HeapScope.Loaders.ClrMd;

namespace HeapScope.Integration.Tests;

/// <summary>
/// Sprint 1.5 and 1.6: typed field values, fields by path, strings (one and in bulk) and reference paths read from a real
/// dump. Display text is checked against the known answer end to end in <see cref="InspectionTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ObjectReaderTests(AllLeaksDump shared) : IClassFixture<AllLeaksDump>, IDisposable
{
    private readonly ClrMdObjectReader _reader = new(shared.DumpPath);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ulong Sample => shared.Dump.Instances("Fixtures.Leaks.InspectionSample").Single().Address;

    [Fact]
    public void Fields_read_as_typed_values()
    {
        _reader.IsOpen.ShouldBeFalse("nothing is opened until something is read");

        var fields = Flatten(_reader.ReadFields(Sample, maxElements: 100, maxText: 1000, Ct));

        _reader.IsOpen.ShouldBeTrue();
        fields["Number"].Value.ShouldSatisfyAllConditions(v => v.Kind.ShouldBe(ObjectValueKind.Integer), v => v.Integer.ShouldBe(42));
        fields["Big"].Value.Integer.ShouldBe(9_000_000_000);
        fields["Ratio"].Value.ShouldSatisfyAllConditions(v => v.Kind.ShouldBe(ObjectValueKind.Floating), v => v.Floating.ShouldBe(0.25));
        fields["Flag"].Value.Boolean.ShouldBeTrue();
        fields["Letter"].Value.Char.ShouldBe('Q');
        fields["Small"].Value.ShouldSatisfyAllConditions(v => v.Kind.ShouldBe(ObjectValueKind.Unsigned), v => v.Unsigned.ShouldBe(7UL));
        fields["Color"].Value.EnumMembers.ShouldNotBeNull().Select(m => m.Name).ShouldBe(["Red", "Green", "Blue"], ignoreOrder: true);
        fields["Color"].Value.Bits.ShouldBe(2UL);
        fields["Text"].Value.ShouldSatisfyAllConditions(v => v.Kind.ShouldBe(ObjectValueKind.String), v => v.Text.ShouldBe("HeapScope inspection"), v => v.Address.ShouldNotBe(0UL));
        fields["Missing"].Value.Kind.ShouldBe(ObjectValueKind.Null);
        fields["Box"].Value.Kind.ShouldBe(ObjectValueKind.Struct);
        fields["Box.TopLeft"].Children.Count.ShouldBe(2, "a struct inside a struct is a nested row");
        fields["Child"].Value.ShouldSatisfyAllConditions(v => v.Kind.ShouldBe(ObjectValueKind.Reference), v => v.TypeName.ShouldBe("Fixtures.Leaks.InspectionChild"));
        fields["Grid"].Value.ShouldSatisfyAllConditions(v => v.Dimensions.ShouldBe([2, 3]), v => v.ElementTypeName.ShouldBe("System.Int32"));
        fields["Grid[1,2]"].Value.Integer.ShouldBe(6);
        fields["Boxed"].Value.ShouldSatisfyAllConditions(v => v.Kind.ShouldBe(ObjectValueKind.Boxed), v => v.Boxed!.Integer.ShouldBe(12345));
        fields["LongText"].Value.ShouldSatisfyAllConditions(v => v.Text!.Length.ShouldBe(1000), v => v.TextLength.ShouldBe(1500));
        fields["Colors[1]"].Value.Bits.ShouldBe(4UL);
    }

    [Fact]
    public void Arrays_list_their_elements_up_to_the_limit_then_a_more_row()
    {
        var numbers = Flatten(_reader.ReadFields(Sample, 100, 1000, Ct))["Numbers"];

        var elements = _reader.ReadFields(numbers.Value.Address, maxElements: 2, maxText: 1000, Ct);

        elements.Select(e => e.Value.Integer).Take(2).ShouldBe([3L, 1L]);
        elements[^1].Value.ShouldSatisfyAllConditions(v => v.Kind.ShouldBe(ObjectValueKind.More), v => v.Unsigned.ShouldBe(3UL));
    }

    [Fact]
    public void Fields_by_path_go_through_structs_and_references()
    {
        _reader.ReadField(Sample, "Number").ShouldNotBeNull().Integer.ShouldBe(42);
        _reader.ReadField(Sample, "<Number>k__BackingField").ShouldNotBeNull().Integer.ShouldBe(42, "raw names work too");
        _reader.ReadField(Sample, "Box.TopLeft.Y").ShouldNotBeNull().Integer.ShouldBe(2);
        _reader.ReadField(Sample, "Child.Id").ShouldNotBeNull().Integer.ShouldBe(99, "through a reference");
        _reader.ReadField(Sample, "Numbers").ShouldNotBeNull().ElementCount.ShouldBe(5);
        _reader.ReadField(Sample, "Missing.Length").ShouldBeNull("a null on the way");
        _reader.ReadField(Sample, "NoSuchField").ShouldBeNull();
    }

    [Fact]
    public void Strings_one_and_in_bulk_and_reference_paths_read_from_the_dump()
    {
        var fields = Flatten(_reader.ReadFields(Sample, 100, 1000, Ct));

        _reader.ReadString(fields["Text"].Value.Address, 9).ShouldBe("HeapScope");
        _reader.ReadString(Sample, 10).ShouldBeNull("not a string");
        var texts = new string?[3];
        _reader.ReadStrings([fields["Text"].Value.Address, Sample, fields["Box.Label"].Value.Address], 100, (i, text) => texts[i] = text, Ct);
        texts.ShouldBe(["HeapScope inspection", null, "box"]);

        var paths = _reader.ReadReferencePaths(Sample, 100, Ct);
        paths[fields["Child"].Value.Address].ShouldBe("Child");
        paths[fields["Box.Label"].Value.Address].ShouldBe("Box.Label");
        paths[fields["Words"].Value.Address].ShouldBe("Words");
        _reader.ReadReferencePaths(fields["Words"].Value.Address, 100, Ct)[fields["Words[1]"].Value.Address].ShouldBe("[1]");
        Should.Throw<ArgumentException>(() => _reader.ReadFields(0x10, 10, 1000, Ct));
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
