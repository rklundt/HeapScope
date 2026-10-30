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

using System.Globalization;
using System.IO;
using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>A worker service whose synthetic job finishes only when the test says so.</summary>
internal sealed partial class FakeWorkerService : IWorkerService
{
    private TaskCompletionSource<SyntheticJobResult> _synthetic = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IProgress<JobProgress>? LastProgress { get; private set; }

    public CancellationToken LastToken { get; private set; }

    public List<Guid> Released { get; } = [];

    public void CompleteSynthetic(int objects) => _synthetic.TrySetResult(new SyntheticJobResult(
        objects, objects * 3, (ulong)objects * 48, objects * 34L, 0.5, new ResultSetInfo(Guid.NewGuid(), 1, [])));

    public List<OpenSnapshotRequest> Opened { get; } = [];

    public List<Guid> Closed { get; } = [];

    /// <summary>Rows returned by every page: type, count, bytes.</summary>
    public List<IReadOnlyList<string?>> Rows { get; } = [];

    /// <summary>The capture time reported for a path (null when not set).</summary>
    public Func<string, DateTimeOffset?>? CapturedAt { get; set; }

    /// <summary>Whether a dump's capture time is only its file's time (sprint 2.3.6: Linux and macOS cores).</summary>
    public Func<string, bool>? CaptureTimeFromFile { get; set; }

    /// <summary>The index cache identity reported for each open (0 when not set).</summary>
    public ulong CacheIdentity { get; set; }

    /// <summary>When set, the next opens fail with it.</summary>
    public Exception? OpenFailure { get; set; }

    /// <summary>When set, opens are refused until a request trusts this DAC (sprint 3.6).</summary>
    public DacTrustDetails? RequiredDac { get; set; }

    /// <summary>Type summary queries received (tests check filter, sort and grouping).</summary>
    public List<TypeSummaryRequest> SummaryQueries { get; } = [];

    /// <summary>Whether summaries report generations.</summary>
    public bool HasGenerations { get; set; } = true;

    /// <summary>When set, summary queries wait for it (or for their cancellation) before answering.</summary>
    public TaskCompletionSource? SummaryGate { get; set; }

    /// <summary>When set, page requests wait for it (or for their cancellation) before answering.</summary>
    public TaskCompletionSource? PageGate { get; set; }

    /// <summary>When set, page requests fail with it.</summary>
    public Exception? PageFailure { get; set; }

    /// <summary>Page requests received: offset and count.</summary>
    public List<(int Offset, int Count)> PageRequests { get; } = [];

    /// <summary>Result sets alive in the fake worker (released sets are removed, as in the real store).</summary>
    public IReadOnlyCollection<Guid> LiveSets => _sets.Keys;

    private readonly Dictionary<Guid, List<IReadOnlyList<string?>>> _sets = [];

    /// <summary>When set, summary and instances queries that ask for retained bytes fail with it (sprint 2.3).</summary>
    public Exception? RetainedFailure { get; set; }

    /// <summary>Instance rows: address (0x...), type, bytes, generation, value.</summary>
    public List<IReadOnlyList<string?>> InstanceRows { get; } = [];

    /// <summary>Instance queries received.</summary>
    public List<InstancesRequest> InstanceQueries { get; } = [];

    /// <summary>Objects returned by address; others get a plain object with one field.</summary>
    public Dictionary<ulong, ObjectDetails> Objects { get; } = [];

    /// <summary>Reference rows: field, address (0x...), type, bytes.</summary>
    public List<IReadOnlyList<string?>> ReferenceRows { get; } = [];

    /// <summary>Whether the snapshot reads values (string contents).</summary>
    public bool ValuesAvailable { get; set; } = true;

    /// <summary>Whether referrer answers come from a complete graph (sprint 2.1 review M9).</summary>
    public bool CompleteGraph { get; set; } = true;

    /// <summary>Progress reported by referrer queries before they answer.</summary>
    public JobProgress? ReferrerProgress { get; set; }

    /// <summary>Referrer rows: field, address (0x...), type, bytes, references (sprint 2.1).</summary>
    public List<IReadOnlyList<string?>> ReferrerRows { get; } = [];

    /// <summary>Referrers queries received.</summary>
    public List<ReferrersRequest> ReferrerQueries { get; } = [];

    /// <summary>Type referrer rows: type, module, objects, references, targets, bytes (sprint 2.1).</summary>
    public List<IReadOnlyList<string?>> TypeReferrerRows { get; } = [];

    /// <summary>Type referrers queries received.</summary>
    public List<TypeReferrersRequest> TypeReferrerQueries { get; } = [];

    /// <summary>What unreachable means, as the worker says it for a full dump.</summary>
    public const string UnreachableNote = "No root reaches it: it is garbage not yet collected, or held only weakly (weak handles, weak references).";

    /// <summary>The answer to object paths queries (sprint 2.2); null answers that nothing holds the object.</summary>
    public ObjectPathsResult? ObjectPaths { get; set; }

    /// <summary>Object paths queries received.</summary>
    public List<ObjectPathsRequest> ObjectPathQueries { get; } = [];

    /// <summary>When set, object paths queries fail with it.</summary>
    public Exception? ObjectPathsFailure { get; set; }

    /// <summary>Key paths returned by type paths queries (sprint 2.2).</summary>
    public List<KeyPathDto> TypePaths { get; } = [];

    /// <summary>Type paths queries received.</summary>
    public List<TypePathsRequest> TypePathQueries { get; } = [];

    /// <summary>Retained queries received (sprint 2.3).</summary>
    public List<RetainedRequest> RetainedQueries { get; } = [];

    /// <summary>The answer to retained queries; by default 4,096 bytes retained, dominated by the object at 0x500.</summary>
    public RetainedResult? Retained { get; set; }

    /// <summary>How the fake's dominators were obtained.</summary>
    public DominatorIndexInfo Dominators { get; } = new(false, SnapshotMemoryMode.InMemory, 1.5, 1200, true, 100);

    /// <summary>
    /// The fake's dominator tree (sprint 2.4), typed rather than positional (2.3.7 review M15): the top level has an empty
    /// parent key; a node's children show when its key is expanded.
    /// </summary>
    public List<FakeTreeNode> TreeNodes { get; } = [];

    /// <summary>"Objects retained by this set" queries received (sprint 2.4).</summary>
    public List<RetainedBySetRequest> SetQueries { get; } = [];

    /// <summary>The retained types the fake answers a set query with: name, objects, bytes.</summary>
    public List<(string Name, long Objects, long Bytes)> SetRows { get; } = [];

    /// <summary>Set dominator queries received (sprint 3.1).</summary>
    public List<SetDominatorsRequest> SetDominatorQueries { get; } = [];

    /// <summary>The top members the fake answers a set dominators query with: address, type, bytes, retained, members below.</summary>
    public List<(ulong Address, string Type, long Bytes, long Retained, int Below)> SetDominatorRows { get; } = [];

    /// <summary>Similar Retention queries received (sprint 3.2).</summary>
    public List<SimilarRetentionRequest> SimilarRetentionQueries { get; } = [];

    /// <summary>The groups Similar Retention answers with.</summary>
    public List<RetentionGroupDto> RetentionGroups { get; } = [];

    /// <summary>Icicle queries received (sprint 2.4).</summary>
    public List<IcicleRequest> IcicleQueries { get; } = [];

    /// <summary>Dominator tree queries received.</summary>
    public List<DominatorTreeRequest> TreeQueries { get; } = [];

    /// <summary>Inspections offered for every snapshot (sprint 1.6).</summary>
    public List<InspectionInfo> Inspections { get; } =
    [
        new("duplicate-strings", "Duplicate strings", "Same text held many times.", "Reuse one instance.", null),
        new("loh", "Large object heap", "Large objects.", "Pool them.", null),
        new("empty-collections", "Empty collections", "Allocated, unused.", "Create lazily.", "Field values are not stored in a gcdump."),
        new("large-arrays", "Large arrays", "Large.", "Pool them.", null),
        new("fragmentation", "Fragmentation", "Gaps.", "Pin briefly.", null),
    ];

    /// <summary>Rows per inspection: the first cell is the key, then the columns of <see cref="InspectionColumnsFor"/>.</summary>
    public Dictionary<string, List<IReadOnlyList<string?>>> InspectionRows { get; } = new(StringComparer.Ordinal)
    {
        ["duplicate-strings"] = [["alpha", "3", "32", "64", "0x10", "System.String", "System.Private.CoreLib.dll", "SameValue"]],
        ["loh"] = [["App.Buffer", "2", "300000", "App.Buffer", "App.dll", "LargeObjectHeap"]],
        ["large-arrays"] = [["0x20", "System.Byte[]", "100000", "100024", "LargeObjectHeap"]],
        ["fragmentation"] =
        [
            ["Gen0", "1000", "100", "9.09", "0", "0"],
            ["Gen2", "8000", "3000", "27.27", "1", "1000"],
        ],
    };

    /// <summary>Inspections run, by name.</summary>
    public List<string> InspectionRuns { get; } = [];

    /// <summary>When set, inspection runs wait for it (or their cancellation).</summary>
    // Allocation traces (sprint 2.5): one canned trace. Byte[] is allocated by Grow (300 bytes in 3 samples, called from
    // Handle and Main); String by Log (100 bytes in 1 sample, called from Main).
    public List<OpenTraceRequest> TracesOpened { get; } = [];

    public List<Guid> TracesClosed { get; } = [];

    public List<AllocationTypesRequest> TypeQueries { get; } = [];

    public List<AllocationStacksRequest> StackQueries { get; } = [];

    public List<AllocationTreeRequest> AllocationTreeQueries { get; } = [];

    public static readonly ResultColumn[] AllocationTypeColumnsList =
    [
        new(AllocationTypeColumns.Type, ResultColumnKind.Text, IsKey: true),
        new(AllocationTypeColumns.Bytes, ResultColumnKind.Bytes, Bound: ValueBound.Estimate),
        new(AllocationTypeColumns.Samples, ResultColumnKind.Integer),
        new(AllocationTypeColumns.Percent, ResultColumnKind.Percent),
    ];

    public static readonly ResultColumn[] AllocationTreeColumnsList =
    [
        new(AllocationTreeColumns.Key, ResultColumnKind.Text, IsKey: true), new(AllocationTreeColumns.ParentKey, ResultColumnKind.Text),
        new(AllocationTreeColumns.Depth, ResultColumnKind.Integer), new(AllocationTreeColumns.Kind, ResultColumnKind.Text),
        new(AllocationTreeColumns.Name, ResultColumnKind.Text), new(AllocationTreeColumns.Total, ResultColumnKind.Bytes, Bound: ValueBound.Estimate),
        new(AllocationTreeColumns.Own, ResultColumnKind.Bytes, Bound: ValueBound.Estimate), new(AllocationTreeColumns.Samples, ResultColumnKind.Integer),
        new(AllocationTreeColumns.Percent, ResultColumnKind.Percent), new(AllocationTreeColumns.Children, ResultColumnKind.Integer),
        new(AllocationTreeColumns.Expanded, ResultColumnKind.Integer),
    ];

    /// <summary>The back traces: key, parent key, name, total, own (-1 for none), and the type they are of (null for both).</summary>
    public static readonly (string Key, string Parent, string Name, long Total, long Own, string? Type)[] BackTraceNodes =
    [
        ("b:1", AllocationTreeKeys.Root, "App.Grow", 300, 300, "System.Byte[]"),
        ("b:1/2", "b:1", "App.Handle", 300, -1, "System.Byte[]"),
        ("b:3", AllocationTreeKeys.Root, "App.Log", 100, 100, "System.String"),
        ("b:3/0", "b:3", "App.Main", 100, -1, "System.String"),
    ];

    public static readonly (string Key, string Parent, string Name, long Total, long Own, string? Type)[] CallTreeNodes =
    [
        ("n:0", AllocationTreeKeys.Root, "App.Main", 400, 0, null),
        ("n:1", "n:0", "App.Handle", 300, 0, null),
        ("n:2", "n:1", "App.Grow", 300, 300, "System.Byte[]"),
        ("n:3", "n:0", "App.Log", 100, 100, "System.String"),
    ];

    /// <summary>When set, trace opens wait for it (tests make queries overlap a re-read).</summary>
    public TaskCompletionSource? OpenTraceGate { get; set; }

    /// <summary>The trace file's size every open reports; a test changes it to make the file look changed.</summary>
    public long TraceFileBytes { get; set; } = 1000;

    public TaskCompletionSource? InspectionGate { get; set; }

    /// <summary>Inspection requests received, with their baselines (sprint 2.6).</summary>
    public List<InspectionRequest> InspectionRequests { get; } = [];

    /// <summary>The worker's columns for the fake's inspections.</summary>
    public static IReadOnlyList<ResultColumn> InspectionColumnsFor(string name) => name switch
    {
        "duplicate-strings" =>
        [
            new("Value", ResultColumnKind.Text, HasValues: true), new("Copies", ResultColumnKind.Integer), new("Bytes", ResultColumnKind.Bytes), new("Wasted", ResultColumnKind.Bytes),
            new(InspectionColumns.Address, ResultColumnKind.Address, true), new(InspectionColumns.TypeName, ResultColumnKind.Text, IsHidden: true),
            new(InspectionColumns.Module, ResultColumnKind.Text, IsHidden: true), new(InspectionColumns.Scope, ResultColumnKind.Text, IsHidden: true),
        ],
        "large-arrays" =>
        [
            new(InspectionColumns.Address, ResultColumnKind.Address, true), new("Type", ResultColumnKind.Text), new("Length", ResultColumnKind.Integer),
            new("Bytes", ResultColumnKind.Bytes), new("Generation", ResultColumnKind.Text),
        ],
        InspectionNames.Fragmentation =>
        [
            new(InspectionColumns.Heap, ResultColumnKind.Text, true), new(InspectionColumns.Live, ResultColumnKind.Bytes), new(InspectionColumns.Free, ResultColumnKind.Bytes),
            new("FreePercent", ResultColumnKind.Percent), new(InspectionColumns.PinnedObjects, ResultColumnKind.Integer), new(InspectionColumns.PinnedBytes, ResultColumnKind.Bytes),
        ],
        _ =>
        [
            new("Type", ResultColumnKind.Text, true), new("Objects", ResultColumnKind.Integer), new("Bytes", ResultColumnKind.Bytes),
            new(InspectionColumns.TypeName, ResultColumnKind.Text, IsHidden: true), new(InspectionColumns.Module, ResultColumnKind.Text, IsHidden: true), new(InspectionColumns.Scope, ResultColumnKind.Text, IsHidden: true),
        ],
    };

    /// <summary>Diff rows: name, change, baseline objects, current objects, objects delta, baseline bytes, current bytes, bytes delta.</summary>
    public List<IReadOnlyList<string?>> DiffRows { get; } = [];

    /// <summary>Diff queries received.</summary>
    public List<TypeDiffRequest> DiffQueries { get; } = [];

    /// <summary>Whether diffs report exact counts.</summary>
    public bool DiffExact { get; set; } = true;

    /// <summary>Result sets created by diff queries.</summary>
    public List<Guid> DiffSets { get; } = [];

    /// <summary>Why the fake refuses to match objects (null: it matches, sprint 3.3).</summary>
    public string? IdentityNote { get; set; }

    /// <summary>Trend queries received (sprint 3.3).</summary>
    public List<TypeTrendRequest> TrendQueries { get; } = [];

    /// <summary>The trend's rows: a name, then objects per snapshot (bytes are ten times the objects).</summary>
    public List<(string Name, long[] Objects)> TrendRows { get; } = [];

    private static IReadOnlyList<string?> WithPercent(IReadOnlyList<string?> row) =>
        row.Count >= 4 ? row : [row[0], row[1], row[2], "0"];
}
