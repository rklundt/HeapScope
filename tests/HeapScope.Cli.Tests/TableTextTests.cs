// HeapScope
// Copyright (C) 2026 Ray Klundt
// https://github.com/rklundt/HeapScope
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3 only, with additional terms
// under Section 7 of that license.
//
// Additional terms, including attribution requirements, apply.
// See the ADDITIONAL-TERMS.md file distributed with this program.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program (see the LICENSE file). If not, see
// <https://www.gnu.org/licenses/>.
//
// Commercial licenses are available. See COMMERCIAL.md.

using HeapScope.Contracts;
using static HeapScope.Contracts.ResultColumnKind;

namespace HeapScope.Cli.Tests;

/// <summary>
/// The CLI's text tables, character for character, from rows with the worker's columns in its current order (sprint 3.8,
/// peer review P10: these held before the cells were read by name and must hold after). A table reads cells by column
/// name, so the same rows with their columns in another order print the same text.
/// </summary>
public sealed class TableTextTests
{
    private static readonly DominatorIndexInfo s_dominators = new(FromSidecar: false, SnapshotMemoryMode.InMemory, Seconds: 1.5, Bytes: 1000, Saved: true, ReachableObjects: 56_000);

    private static readonly string s_dominatorsLine = "Retained sizes: dominators computed in 1.5 s, 56,000 objects reachable from the roots.";

    private static readonly ResultColumn[] s_generations =
    [
        new("Gen0", Bytes), new("Gen1", Bytes), new("Gen2", Bytes), new("LargeObjectHeap", Bytes), new("PinnedObjectHeap", Bytes), new("Frozen", Bytes),
    ];

    private static readonly ResultColumn[] s_typeKeys = [new("TypeName", Text, IsHidden: true), new("Module", Text, IsHidden: true)];

    private static readonly ResultColumn[] s_summaryBase = [new("Name", Text, IsKey: true), new("Objects", Integer), new("Bytes", Bytes), new("Percent", Percent)];

    private static ResultColumn[] SummaryColumns(bool retained, bool generations) =>
        [.. s_summaryBase, .. retained ? [new ResultColumn("Retained", Bytes, Bound: ValueBound.LowerBound)] : Array.Empty<ResultColumn>(), .. generations ? s_generations : [], .. s_typeKeys];

    private static List<IReadOnlyList<string?>> SummaryRows(bool retained, bool generations) =>
    [
        [
            "System.Byte[]", "10001", "560024", "20.92", .. retained ? ["600000"] : Array.Empty<string?>(),
            .. generations ? ["0", "0", "300000", "260024", "0", "0"] : Array.Empty<string?>(), "System.Byte[]", "System.Private.CoreLib.dll",
        ],
        [
            "App.Entry", "2000", "64000", "2.39", .. retained ? ["64000"] : Array.Empty<string?>(),
            .. generations ? ["0", "0", "64000", "0", "0", "0"] : Array.Empty<string?>(), "App.Entry", "App.dll",
        ],
    ];

    private static TypeSummaryResult Summary(IReadOnlyList<ResultColumn> columns, bool retained, bool generations) => new(
        new ResultSetInfo(Guid.Empty, 2, columns),
        TotalObjects: 57_022,
        TotalBytes: 2_676_784,
        TotalTypes: 212,
        HasGenerations: generations,
        HeapKinds: generations ? [new(HeapKindDto.Gen0, 10, 1024), new(HeapKindDto.Gen2, 50_000, 2_400_000)] : [],
        ElapsedSeconds: 0.1,
        UnfilteredRows: 212,
        Dominators: retained ? s_dominators : null);

    [Fact]
    public async Task The_summary_table_of_a_full_dump()
    {
        var columns = SummaryColumns(retained: false, generations: true);

        var lines = await LinesAsync(o => CliCommands.WriteSummaryTableAsync(o, Summary(columns, false, true), SnapshotKindDto.FullDump, columns, SummaryRows(false, true)));

        lines.ShouldBe(
        [
            "57,022 objects, 2.6 MB, 212 types (all objects in the dump, including unreachable ones not yet collected); 2 of 212 rows match, showing 2",
            "Heap kinds: Gen0 0.0 MB, Gen2 2.3 MB",
            "       Objects            Bytes       %  Name",
            "        10,001          560,024   20.92  System.Byte[]",
            "         2,000           64,000    2.39  App.Entry",
        ]);
    }

    [Fact]
    public async Task The_summary_table_with_retained_bytes()
    {
        var columns = SummaryColumns(retained: true, generations: true);

        var lines = await LinesAsync(o => CliCommands.WriteSummaryTableAsync(o, Summary(columns, true, true), SnapshotKindDto.FullDump, columns, SummaryRows(true, true)));

        lines.ShouldBe(
        [
            "57,022 objects, 2.6 MB, 212 types (all objects in the dump, including unreachable ones not yet collected); 2 of 212 rows match, showing 2",
            "Heap kinds: Gen0 0.0 MB, Gen2 2.3 MB",
            s_dominatorsLine + " Retained per row: " + ValueBoundTexts.LowerBound,
            "       Objects            Bytes       %         Retained  Name",
            "        10,001          560,024   20.92          600,000  System.Byte[]",
            "         2,000           64,000    2.39           64,000  App.Entry",
        ]);
    }

    [Fact]
    public async Task The_summary_table_of_a_gcdump_counts_the_survivors_of_its_collection()
    {
        // The one intended change (sprint 3.8 decision 3): a gcdump's header no longer claims unreachable objects.
        var columns = SummaryColumns(retained: false, generations: false);

        var lines = await LinesAsync(o => CliCommands.WriteSummaryTableAsync(o, Summary(columns, false, false), SnapshotKindDto.GcDump, columns, SummaryRows(false, false)));

        lines[0].ShouldBe("57,022 objects, 2.6 MB, 212 types (the objects that survived the gcdump's collection); 2 of 212 rows match, showing 2");
        lines.Length.ShouldBe(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_summary_table_reads_cells_by_column_name(bool retained)
    {
        var columns = SummaryColumns(retained, generations: true);
        var rows = SummaryRows(retained, generations: true);
        var (shuffled, shuffledRows) = Shuffle(columns, rows);

        var expected = await LinesAsync(o => CliCommands.WriteSummaryTableAsync(o, Summary(columns, retained, true), SnapshotKindDto.FullDump, columns, rows));
        var actual = await LinesAsync(o => CliCommands.WriteSummaryTableAsync(o, Summary(shuffled, retained, true), SnapshotKindDto.FullDump, shuffled, shuffledRows));

        actual.ShouldBe(expected);
    }

    [Fact]
    public async Task A_blank_count_cell_throws_as_the_positional_read_did()
    {
        // An empty Objects cell is a worker fault; the table refuses it rather than printing zero.
        var columns = SummaryColumns(retained: false, generations: true);
        var rows = SummaryRows(false, true);
        rows[0] = [.. rows[0].Select((cell, i) => i == 1 ? null : cell)];

        await Should.ThrowAsync<ArgumentNullException>(() => LinesAsync(o => CliCommands.WriteSummaryTableAsync(o, Summary(columns, false, true), SnapshotKindDto.FullDump, columns, rows)));
    }

    private static readonly ResultColumn[] s_diffBase =
    [
        new("Name", Text, IsKey: true), new("Change", Text), new("BaselineObjects", Integer), new("CurrentObjects", Integer),
        new("ObjectsDelta", SignedInteger), new("BaselineBytes", Bytes), new("CurrentBytes", Bytes), new("BytesDelta", SignedInteger),
        new("BaselineTypes", Integer), new("CurrentTypes", Integer),
    ];

    private static readonly ResultColumn[] s_diffExtras =
    [
        new("NewObjects", Integer), new("DeadObjects", Integer), new("BaselineRetained", Bytes, Bound: ValueBound.LowerBound),
        new("CurrentRetained", Bytes, Bound: ValueBound.LowerBound), new("RetainedDelta", SignedInteger, Bound: ValueBound.Estimate),
    ];

    private static ResultColumn[] DiffColumns(bool extras) => [.. s_diffBase, .. extras ? s_diffExtras : [], .. s_typeKeys];

    private static List<IReadOnlyList<string?>> DiffRows(bool extras) =>
    [
        ["App.Entry", "Grew", "1000", "1500", "500", "1000000", "1500000", "500000", "1", "1", .. extras ? ["600", "100", "900000", "1400000", "500000"] : Array.Empty<string?>(), "App.Entry", "App.dll"],
        ["App.Gone", "Gone", "200", "0", "-200", "6400", "0", "-6400", "1", "0", .. extras ? ["0", "200", "6400", "0", "-6400"] : Array.Empty<string?>(), "App.Gone", "App.dll"],
    ];

    private static TypeDiffResult Diff(IReadOnlyList<ResultColumn> columns) => new(
        new ResultSetInfo(Guid.Empty, 2, columns),
        BaselineObjects: 1000,
        BaselineBytes: 2_000_000,
        CurrentObjects: 1500,
        CurrentBytes: 3_000_000,
        NewRows: 1,
        GoneRows: 0,
        UnfilteredRows: 10,
        MatchingBaselineBytes: 2_000_000,
        MatchingBytesDelta: 1_000_000,
        ElapsedSeconds: 0.1,
        ExactCounts: true);

    private static readonly string[] s_diffHeader =
    [
        "Baseline 1,000 objects, 1.9 MB; current 1,500 objects, 2.9 MB; delta +500 objects, +1,000,000 bytes (every object each snapshot holds: a full dump includes unreachable ones, a gcdump only survivors of its collection)",
        "2 of 10 rows match (1 new, 0 gone overall), showing 2; matching rows grew by +1,000,000 bytes",
    ];

    [Fact]
    public async Task The_diff_table()
    {
        var columns = DiffColumns(extras: false);

        var lines = await LinesAsync(o => CliCommands.WriteDiffTableAsync(o, Diff(columns), columns, DiffRows(false)));

        lines.ShouldBe(
        [
            .. s_diffHeader,
            " Objects delta      Bytes delta      Objects            Bytes Change  Name",
            "          +500         +500,000        1,500        1,500,000   Grew  App.Entry",
            "          -200           -6,400            0                0   Gone  App.Gone",
        ]);
    }

    [Fact]
    public async Task The_diff_table_with_new_dead_and_retained_columns()
    {
        var columns = DiffColumns(extras: true);

        var lines = await LinesAsync(o => CliCommands.WriteDiffTableAsync(o, Diff(columns), columns, DiffRows(true)));

        lines.ShouldBe(
        [
            .. s_diffHeader,
            " Objects delta      Bytes delta      Objects            Bytes        New       Dead   Retained delta Change  Name",
            "          +500         +500,000        1,500        1,500,000        600        100         +500,000   Grew  App.Entry",
            "          -200           -6,400            0                0          0        200           -6,400   Gone  App.Gone",
        ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_diff_table_reads_cells_by_column_name(bool extras)
    {
        var columns = DiffColumns(extras);
        var rows = DiffRows(extras);
        var (shuffled, shuffledRows) = Shuffle(columns, rows);

        var expected = await LinesAsync(o => CliCommands.WriteDiffTableAsync(o, Diff(columns), columns, rows));
        var actual = await LinesAsync(o => CliCommands.WriteDiffTableAsync(o, Diff(shuffled), shuffled, shuffledRows));

        actual.ShouldBe(expected);
    }

    [Fact]
    public async Task The_type_referrers_table()
    {
        ResultColumn[] columns = [new("Type", Text), new("Module", Text), new("Objects", Integer), new("References", Integer), new("Targets", Integer), new("Bytes", Bytes)];
        List<IReadOnlyList<string?>> rows =
        [
            ["App.Entry[]", "App.dll", "1", "2000", "2000", "97200"],
            ["App.Cache", string.Empty, "3", "3", "3", "72"],
        ];
        var result = new TypeReferrersResult(new ResultSetInfo(Guid.Empty, 2, columns), MatchedTypes: 1, TargetObjects: 2000, ReferenceCount: 2003, Index: null, CompleteGraph: true);

        var lines = await LinesAsync(o => CliCommands.WriteTypeReferrersTableAsync(o, "App.Entry", "App.dll", result, columns, rows));

        lines.ShouldBe(
        [
            "Referrers of App.Entry (App.dll): 2,000 objects, 2,003 references from 2 types, showing 2",
            "     Objects   References      Targets          Bytes  Type",
            "           1        2,000        2,000         97,200  App.Entry[] (App.dll)",
            "           3            3            3             72  App.Cache",
        ]);
    }

    [Fact]
    public async Task The_object_referrers_table()
    {
        ResultColumn[] columns = [new("Field", Text), new("Address", Address, IsKey: true), new("Type", Text), new("Bytes", Bytes), new("References", Integer)];
        List<IReadOnlyList<string?>> rows =
        [
            ["[0].value", "0x1b001c00048", "App.Entry[]", "97200", "1"],
            [string.Empty, "0x2000", "App.Holder", "24", "2"],
        ];
        var result = new ReferrersResult(new ResultSetInfo(Guid.Empty, 2, columns), ReferenceCount: 3, FieldsNote: null, new ReverseIndexInfo(true, SnapshotMemoryMode.InMemory, 0.1, 100, true), CompleteGraph: true);

        var lines = await LinesAsync(o => CliCommands.WriteObjectReferrersTableAsync(o, 0x1b001047950, result, columns, rows));

        lines.ShouldBe(
        [
            "Referrers of 0x1b001047950: 2 objects, 3 references, showing 2",
            "Address                   Bytes   Refs  Type",
            "0x1b001c00048            97,200      1  App.Entry[]  via [0].value",
            "0x2000                       24      2  App.Holder",
        ]);
    }

    private static readonly ResultColumn[] s_instanceColumns =
        [new("Address", Address, IsKey: true), new("Type", Text), new("Bytes", Bytes), new("Generation", Text), new("Value", Text, HasValues: true)];

    [Fact]
    public async Task The_instances_table()
    {
        List<IReadOnlyList<string?>> rows =
        [
            ["0x1000", "System.String", "32", "Gen2", "a\tb\r\nc"],
            ["0x2000", "System.String", "1032", "LargeObjectHeap", null],
        ];
        var result = new InstancesResult(new ResultSetInfo(Guid.Empty, 2, s_instanceColumns), TotalObjects: 5622, TotalBytes: 300_000, MatchedTypes: 1, ValuesAvailable: true, ElapsedSeconds: 0.1);

        var lines = await LinesAsync(o => CliCommands.WriteInstancesTableAsync(o, "System.String", "System.Private.CoreLib.dll", result, s_instanceColumns, rows));

        lines.ShouldBe(
        [
            "System.String (System.Private.CoreLib.dll): 5,622 objects, 0.3 MB; 2 match, showing 2",
            "Address                     Bytes Generation    Value",
            "0x1000                         32 Gen2          a\\tb\\r\\nc",
            "0x2000                      1,032 LargeObjectHeap  ",
        ]);
    }

    [Fact]
    public async Task The_instances_table_with_retained_bytes_leaves_an_unreachable_object_blank()
    {
        ResultColumn[] columns = [.. s_instanceColumns, new("Retained", Bytes)];
        List<IReadOnlyList<string?>> rows =
        [
            ["0x1000", "App.Entry", "32", "Gen2", string.Empty, "1064"],
            ["0x2000", "App.Entry", "32", "Gen2", string.Empty, null],
        ];
        var result = new InstancesResult(new ResultSetInfo(Guid.Empty, 2, columns), TotalObjects: 2000, TotalBytes: 64_000, MatchedTypes: 1, ValuesAvailable: true, ElapsedSeconds: 0.1, Dominators: s_dominators);

        var lines = await LinesAsync(o => CliCommands.WriteInstancesTableAsync(o, "App.Entry", "App.dll", result, columns, rows));

        lines.ShouldBe(
        [
            "App.Entry (App.dll): 2,000 objects, 0.1 MB; 2 match, showing 2",
            s_dominatorsLine + " An object no root reaches retains nothing (blank).",
            "Address                     Bytes Generation           Retained  Value",
            "0x1000                         32 Gen2                    1,064  ",
            "0x2000                         32 Gen2                           ",
        ]);
    }

    [Fact]
    public async Task The_object_text()
    {
        var details = new ObjectDetails(
            0x1000,
            "App.Entry",
            32,
            HeapKindDto.Gen2,
            ReferenceCount: 2,
            IsRooted: false,
            Fields: [new("_name", "System.String", ObjectFieldKindDto.String, "\"abc\"", 0x3000, [])],
            FieldsUnavailable: null,
            Text: null);
        var retained = new RetainedResult(0x1000, "App.Entry", 32, Reachable: true, RetainedBytes: 1096, DominatorAddress: 0x2000, DominatorType: "App.Holder", DominatedByRoots: false, UnreachableNote: string.Empty, s_dominators, CompleteGraph: true);
        ResultColumn[] columns = [new("Field", Text), new("Address", Address, IsKey: true), new("Type", Text), new("Bytes", Bytes)];
        List<IReadOnlyList<string?>> rows =
        [
            ["_name", "0x3000", "System.String", "28"],
            [string.Empty, "0x4000", "App.Other", "1024"],
        ];

        var lines = await LinesAsync(o => CliCommands.WriteObjectTextAsync(o, details, retained, columns, rows));

        lines.ShouldBe(
        [
            "Object:      0x1000",
            "Type:        App.Entry",
            "Size:        32 bytes",
            "Generation:  Gen2",
            "Rooted:      no direct root",
            "Retained:    1,096 bytes (what it alone keeps alive)",
            "Dominator:   0x2000  App.Holder",
            "Fields (1):",
            "  _name (System.String) = \"abc\"  0x3000",
            "References (2, showing 2):",
            "  0x3000                       28  System.String  via _name",
            "  0x4000                    1,024  App.Other",
        ]);
    }

    [Fact]
    public async Task The_top_types_of_open()
    {
        var columns = SummaryColumns(retained: false, generations: true);
        var page = new ResultPage(Guid.Empty, 0, 212, SummaryRows(false, true));

        var lines = await LinesAsync(o => CliCommands.WriteTopTypesAsync(o, columns, page));

        lines.ShouldBe(
        [
            "Top 2 of 212 types by shallow size:",
            "        10,001       0.5 MB  System.Byte[]",
            "         2,000       0.1 MB  App.Entry",
        ]);
    }

    [Fact]
    public async Task The_trend_table()
    {
        ResultColumn[] columns =
        [
            new("Name", Text, IsKey: true), new("Objects1", Integer), new("Objects2", Integer), new("Bytes1", Bytes), new("Bytes2", Bytes),
            new("ObjectsDelta", SignedInteger), new("BytesDelta", SignedInteger), new("GrowthSteps", Integer), .. s_typeKeys,
        ];
        List<IReadOnlyList<string?>> rows =
        [
            ["App.Entry", "1000", "2000", "32000", "64000", "1000", "32000", "1", "App.Entry", "App.dll"],
            ["App.Gone", "10", "0", "320", "0", "-10", "-320", "0", "App.Gone", "App.dll"],
        ];
        var result = new TypeTrendResult(new ResultSetInfo(Guid.Empty, 2, columns), [2_676_784, 2_708_464], UnfilteredRows: 10, ElapsedSeconds: 0.1, ExactCounts: true);

        var lines = await LinesAsync(o => CliCommands.WriteTrendTableAsync(o, result, 2, columns, rows));

        lines.ShouldBe(
        [
            "2 of 10 rows across 2 dumps; heap bytes 2,676,784, then 2,708,464; showing 2",
            "       Bytes 1        Bytes 2    Bytes delta  Grew  Name",
            "        32,000         64,000        +32,000     1  App.Entry",
            "           320              0           -320     0  App.Gone",
        ]);
    }

    [Fact]
    public async Task The_synthetic_lines()
    {
        ResultColumn[] columns = [.. s_summaryBase];
        var result = new SyntheticJobResult(100_000, 300_000, 5_557_000, 3_355_443, 0.06, new ResultSetInfo(Guid.Empty, 1000, columns));
        var page = new ResultPage(Guid.Empty, 0, 1000, [["Synthetic.Type0060", "136", "7072", "0.13"], ["Synthetic.Type0937", "130", "1048576", "0.12"]]);

        var lines = await LinesAsync(o => CliCommands.WriteSyntheticAsync(o, result, page));

        lines.ShouldBe(
        [
            "100,000 objects, 300,000 references, 5.3 MB shallow size, 3.2 MB graph columns, 0.06 s",
            "Top 2 of 1000 types by shallow size:",
            "  Synthetic.Type0060                136 objects       0.0 MB",
            "  Synthetic.Type0937                130 objects       1.0 MB",
        ]);
    }

    private static async Task<string[]> LinesAsync(Func<TextWriter, Task> write)
    {
        using var output = new StringWriter();
        await write(output);
        // Any line break: a multi-line block (the object's facts) breaks its lines as the source file does.
        return [.. output.ToString().Split('\n')[..^1].Select(line => line.TrimEnd('\r'))];
    }

    /// <summary>The same rows with their columns reversed, cells moved with their columns.</summary>
    private static (ResultColumn[] Columns, List<IReadOnlyList<string?>> Rows) Shuffle(IReadOnlyList<ResultColumn> columns, List<IReadOnlyList<string?>> rows)
    {
        var order = Enumerable.Range(0, columns.Count).Reverse().ToArray();
        return ([.. order.Select(i => columns[i])], [.. rows.Select(row => (IReadOnlyList<string?>)[.. order.Select(i => row[i])])]);
    }
}
