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

using System.IO;
using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>A worker client that calls an in-process fake service.</summary>
internal sealed class FakeWorkerClient : IWorkerClient
{
    public FakeWorkerService Service { get; } = new();

    public WorkerState State { get; private set; } = WorkerState.NotStarted;

    public WorkerInfo? Info { get; } = new(Guid.NewGuid(), WorkerProtocol.Version, "0.0.0", 1234, true, 0, 0, 0, false);

    public int RestartCount { get; private set; }

    public int Generation { get; private set; } = 1;

    public event EventHandler<WorkerStateChangedEventArgs>? StateChanged;

    public Task<WorkerInfo> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        State = WorkerState.Running;
        return Task.FromResult(Info!);
    }

    public Task<T> InvokeAsync<T>(Func<IWorkerService, CancellationToken, Task<T>> call, CancellationToken cancellationToken) =>
        call(Service, cancellationToken);

    public Task InvokeAsync(Func<IWorkerService, CancellationToken, Task> call, CancellationToken cancellationToken) =>
        call(Service, cancellationToken);

    /// <summary>Simulates a worker restart that sessions see on their next call (they reopen their snapshots).</summary>
    public void NewGeneration() => Generation++;

    public Task RestartAsync(CancellationToken cancellationToken)
    {
        RestartCount++;
        Raise(WorkerState.Running, null);
        return Task.CompletedTask;
    }

    public void Raise(WorkerState state, string? message)
    {
        State = state;
        StateChanged?.Invoke(this, new WorkerStateChangedEventArgs(state, message));
    }
}

/// <summary>A worker service whose synthetic job finishes only when the test says so.</summary>
internal sealed class FakeWorkerService : IWorkerService
{
    private TaskCompletionSource<SyntheticJobResult> _synthetic = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IProgress<JobProgress>? LastProgress { get; private set; }

    public CancellationToken LastToken { get; private set; }

    public List<Guid> Released { get; } = [];

    public void CompleteSynthetic(int objects) => _synthetic.TrySetResult(new SyntheticJobResult(
        objects, objects * 3, (ulong)objects * 48, objects * 34L, 0.5, new ResultSetInfo(Guid.NewGuid(), 1, [])));

    public Task<WorkerInfo> GetInfoAsync(int clientProtocolVersion, CancellationToken cancellationToken) =>
        Task.FromResult(new WorkerInfo(Guid.NewGuid(), WorkerProtocol.Version, "0.0.0", 1234, true, 1, 2, 3, false));

    public Task<SyntheticJobResult> RunSyntheticJobAsync(SyntheticJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        _synthetic = new TaskCompletionSource<SyntheticJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        LastProgress = progress;
        LastToken = cancellationToken;
        cancellationToken.Register(() => _synthetic.TrySetCanceled(cancellationToken));
        return _synthetic.Task;
    }

    public List<OpenSnapshotRequest> Opened { get; } = [];

    public List<Guid> Closed { get; } = [];

    /// <summary>Rows returned by every page: type, count, bytes.</summary>
    public List<IReadOnlyList<string?>> Rows { get; } = [];

    public Task<SnapshotProbe> ProbeSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new HeapScope.Application.Workers.WorkerOperationException(HeapScopeErrorCode.InvalidArgument, $"The file {path} does not exist.", null);
        }

        var cache = path + ".hsidx";
        var state = File.Exists(cache) ? SnapshotCacheState.Valid : SnapshotCacheState.Missing;
        return Task.FromResult(new SnapshotProbe(path, new FileInfo(path).Length, state, cache, null, false));
    }

    /// <summary>The capture time reported for a path (null when not set).</summary>
    public Func<string, DateTimeOffset?>? CapturedAt { get; set; }

    /// <summary>Whether a dump's capture time is only its file's time (sprint 2.3.6: Linux and macOS cores).</summary>
    public Func<string, bool>? CaptureTimeFromFile { get; set; }

    /// <summary>The index cache identity reported for each open (0 when not set).</summary>
    public ulong CacheIdentity { get; set; }

    /// <summary>When set, the next opens fail with it.</summary>
    public Exception? OpenFailure { get; set; }

    public Task<SnapshotDescriptor> OpenSnapshotAsync(OpenSnapshotRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (OpenFailure is { } failure)
        {
            return Task.FromException<SnapshotDescriptor>(failure);
        }

        Opened.Add(request);
        return Task.FromResult(new SnapshotDescriptor(
            Guid.NewGuid(), request.Path, SnapshotKindDto.FullDump, SnapshotFeatures.ObjectGraph, "App", 42, "10.0.13", CapturedAt?.Invoke(request.Path),
            1000, 3000, 10, 5, 48_000, SnapshotMemoryMode.InMemory, false, request.Path + ".hsidx", 1.5, ["A note."], CacheIdentity,
            CaptureTimeFromFile?.Invoke(request.Path) ?? false));
    }

    public Task CloseSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        Closed.Add(snapshotId);
        return Task.CompletedTask;
    }

    public Task<ResultSetInfo> GetTypeSummaryAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        _sets[id] = [.. Rows];
        return Task.FromResult(new ResultSetInfo(id, Rows.Count, []));
    }

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

    public async Task<TypeSummaryResult> QueryTypeSummaryAsync(TypeSummaryRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        SummaryQueries.Add(request);
        if (request.IncludeRetained && RetainedFailure is { } failure)
        {
            throw failure;
        }

        if (SummaryGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        // The worker's order: the sort column, then the name ascending for ties.
        var matching = Rows.Where(r => NameFilter.IsMatch(r[0]!, request.Filter));
        Func<IReadOnlyList<string?>, long> key = request.SortColumn switch
        {
            TypeSummaryColumns.Objects => r => long.Parse(r[1]!, System.Globalization.CultureInfo.InvariantCulture),
            _ => r => long.Parse(r[2]!, System.Globalization.CultureInfo.InvariantCulture),
        };
        List<IReadOnlyList<string?>> rows = request.SortColumn == TypeSummaryColumns.Name
            ? (request.Descending
                ? [.. matching.OrderByDescending(r => r[0], StringComparer.OrdinalIgnoreCase).ThenBy(r => r[0], StringComparer.Ordinal)]
                : [.. matching.OrderBy(r => r[0], StringComparer.OrdinalIgnoreCase).ThenBy(r => r[0], StringComparer.Ordinal)])
            : (request.Descending
                ? [.. matching.OrderByDescending(key).ThenBy(r => r[0], StringComparer.Ordinal)]
                : [.. matching.OrderBy(key).ThenBy(r => r[0], StringComparer.Ordinal)]);

        // Type rows carry the hidden key columns, as the worker's do; retained bytes (sprint 2.3) are three times the bytes.
        var byType = request.GroupBy == TypeGrouping.Type;
        var retained = request.IncludeRetained;
        if (retained)
        {
            progress?.Report(new JobProgress("Finding immediate dominators", 50, 100));
        }

        rows = [.. rows.Select(r => (IReadOnlyList<string?>)[
            .. r.Take(3), r.Count >= 4 ? r[3] : "0",
            .. retained ? [(3 * long.Parse(r[2]!, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture)] : Array.Empty<string?>(),
            .. byType ? [r[0], "App.dll"] : Array.Empty<string?>()])];
        var id = Guid.NewGuid();
        _sets[id] = rows;
        ResultColumn[] columns = [new("Name", ResultColumnKind.Text, true), new("Objects", ResultColumnKind.Integer), new("Bytes", ResultColumnKind.Bytes), new("Percent", ResultColumnKind.Percent),
            .. retained ? new ResultColumn[] { new(TypeSummaryColumns.Retained, ResultColumnKind.Bytes) } : [],
            .. byType ? new ResultColumn[] { new(TypeSummaryColumns.TypeName, ResultColumnKind.Text), new(TypeSummaryColumns.Module, ResultColumnKind.Text) } : []];
        var totalBytes = Rows.Sum(r => long.Parse(r[2]!, System.Globalization.CultureInfo.InvariantCulture));
        HeapKindTotal[] kinds = HasGenerations ? [new(HeapKindDto.Gen0, 10, totalBytes / 2), new(HeapKindDto.Gen2, 5, totalBytes / 2)] : [];
        return new TypeSummaryResult(
            new ResultSetInfo(id, rows.Count, columns),
            Rows.Sum(r => long.Parse(r[1]!, System.Globalization.CultureInfo.InvariantCulture)),
            totalBytes,
            Rows.Count,
            HasGenerations,
            kinds,
            0.1,
            Rows.Count,
            retained ? Dominators : null);
    }

    /// <summary>Instance rows: address (0x...), type, bytes, generation, value.</summary>
    public List<IReadOnlyList<string?>> InstanceRows { get; } = [];

    /// <summary>Instance queries received.</summary>
    public List<InstancesRequest> InstanceQueries { get; } = [];

    /// <summary>Objects returned by address; others get a plain object with one field.</summary>
    public Dictionary<ulong, ObjectDetails> Objects { get; } = [];

    /// <summary>Reference rows: field, address (0x...), type, bytes.</summary>
    public List<IReadOnlyList<string?>> ReferenceRows { get; } = [];

    public Task<InstancesResult> QueryInstancesAsync(InstancesRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        InstanceQueries.Add(request);
        if (request.IncludeRetained && RetainedFailure is { } failure)
        {
            return Task.FromException<InstancesResult>(failure);
        }

        List<IReadOnlyList<string?>> rows = [.. InstanceRows.Where(r => string.IsNullOrEmpty(request.Filter) || (r[4] ?? string.Empty).Contains(request.Filter, StringComparison.OrdinalIgnoreCase))];

        // Sprint 2.3: retained bytes are twice the bytes, and empty for a Gen0 object (as if no root reached it).
        if (request.IncludeRetained)
        {
            rows = [.. rows.Select(r => (IReadOnlyList<string?>)[.. r.Take(5), r[3] == "Gen0" ? null : (2 * long.Parse(r[2]!, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture)])];
        }

        var id = Guid.NewGuid();
        _sets[id] = rows;
        ResultColumn[] columns =
        [
            new(InstanceColumns.Address, ResultColumnKind.Address, true), new(InstanceColumns.Type, ResultColumnKind.Text), new(InstanceColumns.Bytes, ResultColumnKind.Bytes),
            new(InstanceColumns.Generation, ResultColumnKind.Text), new(InstanceColumns.Value, ResultColumnKind.Text),
            .. request.IncludeRetained ? new ResultColumn[] { new(InstanceColumns.Retained, ResultColumnKind.Bytes) } : [],
        ];
        return Task.FromResult(new InstancesResult(
            new ResultSetInfo(id, rows.Count, columns),
            InstanceRows.Count,
            InstanceRows.Sum(r => long.Parse(r[2]!, System.Globalization.CultureInfo.InvariantCulture)),
            1,
            ValuesAvailable,
            0.1,
            request.IncludeRetained ? Dominators : null));
    }

    /// <summary>Whether the snapshot reads values (string contents).</summary>
    public bool ValuesAvailable { get; set; } = true;

    public Task<ObjectDetails> GetObjectAsync(ObjectRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(Objects.TryGetValue(request.Address, out var details)
            ? details
            : new ObjectDetails(request.Address, "App.Order", 40, HeapKindDto.Gen2, ReferenceRows.Count, false,
                [new ObjectFieldDto("Id", "System.Int32", ObjectFieldKindDto.Primitive, "7", 0, [])], null, null));

    public Task<ResultSetInfo> QueryReferencesAsync(ReferencesRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        _sets[id] = [.. ReferenceRows];
        return Task.FromResult(new ResultSetInfo(id, ReferenceRows.Count,
        [
            new(ReferenceColumns.Field, ResultColumnKind.Text), new(ReferenceColumns.Address, ResultColumnKind.Address, true),
            new(ReferenceColumns.Type, ResultColumnKind.Text), new(ReferenceColumns.Bytes, ResultColumnKind.Bytes),
        ]));
    }

    /// <summary>Whether referrer answers come from a complete graph (sprint 2.1 review M9).</summary>
    public bool CompleteGraph { get; set; } = true;

    /// <summary>Progress reported by referrer queries before they answer.</summary>
    public JobProgress? ReferrerProgress { get; set; }

    /// <summary>Referrer rows: field, address (0x...), type, bytes, references (sprint 2.1).</summary>
    public List<IReadOnlyList<string?>> ReferrerRows { get; } = [];

    /// <summary>Referrers queries received.</summary>
    public List<ReferrersRequest> ReferrerQueries { get; } = [];

    public Task<ReferrersResult> QueryReferrersAsync(ReferrersRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ReferrerQueries.Add(request);
        if (ReferrerProgress is { } report)
        {
            progress?.Report(report);
        }

        var id = Guid.NewGuid();
        _sets[id] = [.. ReferrerRows];
        var info = new ResultSetInfo(id, ReferrerRows.Count,
        [
            new(ReferrerColumns.Field, ResultColumnKind.Text), new(ReferrerColumns.Address, ResultColumnKind.Address, true),
            new(ReferrerColumns.Type, ResultColumnKind.Text), new(ReferrerColumns.Bytes, ResultColumnKind.Bytes),
            new(ReferrerColumns.References, ResultColumnKind.Integer),
        ]);
        var references = ReferrerRows.Sum(r => long.Parse(r[4]!, System.Globalization.CultureInfo.InvariantCulture));
        return Task.FromResult(new ReferrersResult(info, references, null, new ReverseIndexInfo(false, SnapshotMemoryMode.InMemory, 0.2, 1024, true), CompleteGraph));
    }

    /// <summary>Type referrer rows: type, module, objects, references, targets, bytes (sprint 2.1).</summary>
    public List<IReadOnlyList<string?>> TypeReferrerRows { get; } = [];

    /// <summary>Type referrers queries received.</summary>
    public List<TypeReferrersRequest> TypeReferrerQueries { get; } = [];

    public Task<TypeReferrersResult> QueryTypeReferrersAsync(TypeReferrersRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        TypeReferrerQueries.Add(request);
        var id = Guid.NewGuid();
        _sets[id] = [.. TypeReferrerRows];
        var info = new ResultSetInfo(id, TypeReferrerRows.Count,
        [
            new(TypeReferrerColumns.Type, ResultColumnKind.Text), new(TypeReferrerColumns.Module, ResultColumnKind.Text),
            new(TypeReferrerColumns.Objects, ResultColumnKind.Integer), new(TypeReferrerColumns.References, ResultColumnKind.Integer),
            new(TypeReferrerColumns.Targets, ResultColumnKind.Integer), new(TypeReferrerColumns.Bytes, ResultColumnKind.Bytes),
        ]);
        var references = TypeReferrerRows.Sum(r => long.Parse(r[3]!, System.Globalization.CultureInfo.InvariantCulture));
        return Task.FromResult(new TypeReferrersResult(info, 1, 5, references, new ReverseIndexInfo(true, SnapshotMemoryMode.InMemory, 0.1, 1024, true), CompleteGraph));
    }

    /// <summary>What unreachable means, as the worker says it for a full dump.</summary>
    public const string UnreachableNote = "No root reaches it: it is garbage not yet collected, or held only weakly (weak handles, weak references).";

    /// <summary>The answer to object paths queries (sprint 2.2); null answers that nothing holds the object.</summary>
    public ObjectPathsResult? ObjectPaths { get; set; }

    /// <summary>Object paths queries received.</summary>
    public List<ObjectPathsRequest> ObjectPathQueries { get; } = [];

    /// <summary>When set, object paths queries fail with it.</summary>
    public Exception? ObjectPathsFailure { get; set; }

    public Task<ObjectPathsResult> QueryObjectPathsAsync(ObjectPathsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ObjectPathQueries.Add(request);
        if (ObjectPathsFailure is { } failure)
        {
            return Task.FromException<ObjectPathsResult>(failure);
        }

        return Task.FromResult(ObjectPaths ?? new ObjectPathsResult(
            [new RetentionNodeDto(0, -1, request.Address, "App.Order", null, false, false, 0, null, 0, 0)],
            [], [], false, false, 1, null, CompleteGraph, new ReverseIndexInfo(true, SnapshotMemoryMode.InMemory, 0, 1024, true), false, UnreachableNote));
    }

    /// <summary>Key paths returned by type paths queries (sprint 2.2).</summary>
    public List<KeyPathDto> TypePaths { get; } = [];

    /// <summary>Type paths queries received.</summary>
    public List<TypePathsRequest> TypePathQueries { get; } = [];

    public Task<TypePathsResult> QueryTypePathsAsync(TypePathsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        TypePathQueries.Add(request);
        return Task.FromResult(new TypePathsResult(
            [.. TypePaths], 1, TypePaths.Sum(p => p.Objects) + 2, 2, 80, 0, 0, 0, CompleteGraph, 0.3, UnreachableNote));
    }

    /// <summary>Retained queries received (sprint 2.3).</summary>
    public List<RetainedRequest> RetainedQueries { get; } = [];

    /// <summary>The answer to retained queries; by default 4,096 bytes retained, dominated by the object at 0x500.</summary>
    public RetainedResult? Retained { get; set; }

    public Task<RetainedResult> QueryRetainedAsync(RetainedRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        RetainedQueries.Add(request);
        return Task.FromResult(Retained ?? new RetainedResult(
            request.Address, "App.Order", 48, true, 4096, 0x500, "App.Cache", false, UnreachableNote, Dominators, CompleteGraph));
    }

    /// <summary>How the fake's dominators were obtained.</summary>
    public DominatorIndexInfo Dominators { get; } = new(false, SnapshotMemoryMode.InMemory, 1.5, 1200, true, 100);

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
    public TaskCompletionSource? InspectionGate { get; set; }

    public Task<IReadOnlyList<InspectionInfo>> ListInspectionsAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InspectionInfo>>([.. Inspections]);

    public async Task<InspectionResultDto> RunInspectionAsync(InspectionRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        InspectionRuns.Add(request.Name);
        if (InspectionGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (Inspections.SingleOrDefault(i => i.Name == request.Name) is not { Unavailable: null })
        {
            throw new HeapScope.Application.Workers.WorkerOperationException(HeapScopeErrorCode.CapabilityNotSupported, "Not available.", null);
        }

        var rows = InspectionRows.GetValueOrDefault(request.Name) ?? [];
        var id = Guid.NewGuid();
        _sets[id] = [.. rows];
        var columns = InspectionColumnsFor(request.Name);
        return new InspectionResultDto(request.Name, rows.Count, 1000, request.Name == "loh" ? null : 64, null, new ResultSetInfo(id, rows.Count, columns), 0.1);
    }

    /// <summary>The worker's columns for the fake's inspections.</summary>
    public static IReadOnlyList<ResultColumn> InspectionColumnsFor(string name) => name switch
    {
        "duplicate-strings" =>
        [
            new("Value", ResultColumnKind.Text), new("Copies", ResultColumnKind.Integer), new("Bytes", ResultColumnKind.Bytes), new("Wasted", ResultColumnKind.Bytes),
            new(InspectionColumns.Address, ResultColumnKind.Address, true), new(InspectionColumns.TypeName, ResultColumnKind.Text),
            new(InspectionColumns.Module, ResultColumnKind.Text), new(InspectionColumns.Scope, ResultColumnKind.Text),
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
            new(InspectionColumns.TypeName, ResultColumnKind.Text), new(InspectionColumns.Module, ResultColumnKind.Text), new(InspectionColumns.Scope, ResultColumnKind.Text),
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

    public async Task<TypeDiffResult> QueryTypeDiffAsync(TypeDiffRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        DiffQueries.Add(request);
        if (SummaryGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        string[] names = [TypeDiffColumns.Name, TypeDiffColumns.Change, TypeDiffColumns.BaselineObjects, TypeDiffColumns.CurrentObjects, TypeDiffColumns.ObjectsDelta, TypeDiffColumns.BaselineBytes, TypeDiffColumns.CurrentBytes, TypeDiffColumns.BytesDelta, TypeDiffColumns.BaselineTypes, TypeDiffColumns.CurrentTypes, TypeDiffColumns.TypeName, TypeDiffColumns.Module];
        var column = Array.IndexOf(names, request.SortColumn ?? TypeDiffColumns.BytesDelta);
        if (column < 0)
        {
            throw new HeapScope.Application.Workers.WorkerOperationException(HeapScopeErrorCode.InvalidArgument, $"Unknown sort column {request.SortColumn}.", null);
        }

        var matching = DiffRows.Where(r => NameFilter.IsMatch(r[0]!, request.Filter));
        IOrderedEnumerable<IReadOnlyList<string?>> ordered = column <= 1
            ? (request.Descending ? matching.OrderByDescending(r => r[column], StringComparer.OrdinalIgnoreCase) : matching.OrderBy(r => r[column], StringComparer.OrdinalIgnoreCase))
            : (request.Descending ? matching.OrderByDescending(r => long.Parse(r[column]!, System.Globalization.CultureInfo.InvariantCulture)) : matching.OrderBy(r => long.Parse(r[column]!, System.Globalization.CultureInfo.InvariantCulture)));
        List<IReadOnlyList<string?>> rows = [.. ordered.ThenBy(r => r[0], StringComparer.Ordinal)];
        var id = Guid.NewGuid();
        _sets[id] = rows;
        DiffSets.Add(id);
        static long Sum(IEnumerable<IReadOnlyList<string?>> rows, int index) => rows.Sum(r => long.Parse(r[index]!, System.Globalization.CultureInfo.InvariantCulture));
        return new TypeDiffResult(
            new ResultSetInfo(id, rows.Count, [.. names.Select((n, i) => new ResultColumn(n, i <= 1 ? ResultColumnKind.Text : i is 4 or 7 ? ResultColumnKind.SignedInteger : ResultColumnKind.Integer, i == 0))]),
            Sum(DiffRows, 2),
            Sum(DiffRows, 5),
            Sum(DiffRows, 3),
            Sum(DiffRows, 6),
            DiffRows.Count(r => r[1] == TypeDiffColumns.New),
            DiffRows.Count(r => r[1] == TypeDiffColumns.Gone),
            DiffRows.Count,
            Sum(rows, 5),
            Sum(rows, 7),
            0.1,
            DiffExact);
    }

    public async Task<ResultPage> GetRowsAsync(Guid resultSetId, int offset, int count, CancellationToken cancellationToken)
    {
        PageRequests.Add((offset, count));
        if (PageGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (PageFailure is { } failure)
        {
            throw failure;
        }

        if (!_sets.TryGetValue(resultSetId, out var rows))
        {
            throw new HeapScope.Application.Workers.WorkerOperationException(HeapScopeErrorCode.ResultSetNotFound, "The result set is gone. Run the query again.", null);
        }

        return new ResultPage(resultSetId, offset, rows.Count, [.. rows.Skip(offset).Take(count).Select(WithPercent)]);
    }

    private static IReadOnlyList<string?> WithPercent(IReadOnlyList<string?> row) =>
        row.Count >= 4 ? row : [row[0], row[1], row[2], "0"];

    public Task ReleaseResultSetAsync(Guid resultSetId, CancellationToken cancellationToken)
    {
        Released.Add(resultSetId);
        _sets.Remove(resultSetId);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CrashForTestingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task HangForTestingAsync(IProgress<JobProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Scripted dialogs.</summary>
internal sealed class FakeUserInteraction : HeapScope.UI.Services.IUserInteraction
{
    public string? NextFile { get; set; }

    public bool Answer { get; set; } = true;

    public List<string> Questions { get; } = [];

    public Task<string?> PickDumpFileAsync() => Task.FromResult(NextFile);

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Questions.Add(message);
        return Task.FromResult(Answer);
    }
}
