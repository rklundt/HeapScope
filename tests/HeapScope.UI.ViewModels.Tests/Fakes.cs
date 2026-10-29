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
            .. retained ? new ResultColumn[] { new(TypeSummaryColumns.Retained, ResultColumnKind.Bytes, Bound: ValueBound.LowerBound) } : [],
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

    /// <summary>
    /// The fake's dominator tree (sprint 2.4), typed rather than positional (2.3.7 review M15): the top level has an empty
    /// parent key; a node's children show when its key is expanded.
    /// </summary>
    public List<FakeTreeNode> TreeNodes { get; } = [];

    /// <summary>"Objects retained by this set" queries received (sprint 2.4).</summary>
    public List<RetainedBySetRequest> SetQueries { get; } = [];

    /// <summary>The retained types the fake answers a set query with: name, objects, bytes.</summary>
    public List<(string Name, long Objects, long Bytes)> SetRows { get; } = [];

    public Task<RetainedBySetResult> QueryRetainedBySetAsync(RetainedBySetRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        SetQueries.Add(request);
        var total = SetRows.Sum(r => r.Bytes);
        IReadOnlyList<string?>[] rows =
        [
            .. SetRows.Select(r => (IReadOnlyList<string?>)[
                r.Name, r.Objects.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (total == 0 ? 0 : 100.0 * r.Bytes / total).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), r.Name, "App.dll"]),
        ];
        var id = Guid.NewGuid();
        _sets[id] = [.. rows];
        var columns = new ResultColumn[]
        {
            new(RetainedBySetColumns.Name, ResultColumnKind.Text, IsKey: true), new(RetainedBySetColumns.Objects, ResultColumnKind.Integer),
            new(RetainedBySetColumns.Retained, ResultColumnKind.Bytes), new(RetainedBySetColumns.Percent, ResultColumnKind.Percent),
            new(RetainedBySetColumns.TypeName, ResultColumnKind.Text), new(RetainedBySetColumns.Module, ResultColumnKind.Text),
        };
        var set = SetRows.FirstOrDefault(r => r.Name == request.TypeName);
        return Task.FromResult(new RetainedBySetResult(
            new ResultSetInfo(id, rows.Length, columns), SetRows.Count == 0 ? 0 : 1, set.Objects, set.Bytes, SetRows.Sum(r => r.Objects), total, set.Bytes, 10 * total, 0.25, Dominators));
    }

    /// <summary>Set dominator queries received (sprint 3.1).</summary>
    public List<SetDominatorsRequest> SetDominatorQueries { get; } = [];

    /// <summary>The top members the fake answers a set dominators query with: address, type, bytes, retained, members below.</summary>
    public List<(ulong Address, string Type, long Bytes, long Retained, int Below)> SetDominatorRows { get; } = [];

    /// <summary>Similar Retention queries received (sprint 3.2).</summary>
    public List<SimilarRetentionRequest> SimilarRetentionQueries { get; } = [];

    /// <summary>The groups Similar Retention answers with.</summary>
    public List<RetentionGroupDto> RetentionGroups { get; } = [];

    public Task<SimilarRetentionResult> QuerySimilarRetentionAsync(SimilarRetentionRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        SimilarRetentionQueries.Add(request);
        return Task.FromResult(new SimilarRetentionResult(
            [.. RetentionGroups],
            new ObjectSetInfo(RetentionGroups.Sum(g => g.Objects) + 1, RetentionGroups.Sum(g => g.Bytes) + 10, 1),
            RetentionGroups.Count,
            1,
            10,
            0,
            0,
            0,
            CompleteGraph,
            0.2,
            UnreachableNote));
    }

    public Task<SetDominatorsResult> QuerySetDominatorsAsync(SetDominatorsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        SetDominatorQueries.Add(request);
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        IReadOnlyList<string?>[] rows =
        [
            .. SetDominatorRows.Select(r => (IReadOnlyList<string?>)[
                "0x" + r.Address.ToString("x", invariant), r.Type, r.Bytes.ToString(invariant), r.Retained.ToString(invariant), r.Below.ToString(invariant)]),
        ];
        var id = Guid.NewGuid();
        _sets[id] = [.. rows];
        var columns = new ResultColumn[]
        {
            new(SetDominatorColumns.Address, ResultColumnKind.Address, IsKey: true), new(SetDominatorColumns.Type, ResultColumnKind.Text),
            new(SetDominatorColumns.Bytes, ResultColumnKind.Bytes), new(SetDominatorColumns.Retained, ResultColumnKind.Bytes),
            new(SetDominatorColumns.Members, ResultColumnKind.Integer),
        };
        var members = SetDominatorRows.Count + SetDominatorRows.Sum(r => r.Below);
        return Task.FromResult(new SetDominatorsResult(
            new ResultSetInfo(id, rows.Length, columns), new ObjectSetInfo(members, SetDominatorRows.Sum(r => r.Bytes), 1), SetDominatorRows.Count, SetDominatorRows.Sum(r => r.Retained), 0, Dominators));
    }

    /// <summary>Icicle queries received (sprint 2.4).</summary>
    public List<IcicleRequest> IcicleQueries { get; } = [];

    /// <summary>
    /// The fake's icicle: below the focus (or the top level), each <see cref="TreeNodes"/> child side by side, as wide as its
    /// retained bytes, down to <see cref="IcicleRequest.Levels"/>.
    /// </summary>
    public Task<IcicleResult> QueryIcicleAsync(IcicleRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        IcicleQueries.Add(request);
        var focus = request.FocusAddress == 0 ? null : TreeNodes.First(n => n.Address == request.FocusAddress);
        var total = focus?.Retained ?? TreeNodes.Where(n => n.ParentKey.Length == 0).Sum(n => n.Retained);
        var segments = new List<IcicleSegmentDto> { new(0, 0, total, focus?.Key ?? string.Empty, focus?.Name ?? "All reachable objects", focus?.Address ?? 0) };
        void Add(string parent, int level, long offset)
        {
            if (level > request.Levels)
            {
                return;
            }

            foreach (var node in TreeNodes.Where(n => n.ParentKey == parent))
            {
                segments.Add(new IcicleSegmentDto(level, offset, node.Retained, node.Key, node.Name, node.Address));
                Add(node.Key, level + 1, offset);
                offset += node.Retained;
            }
        }

        Add(focus?.Key ?? string.Empty, 1, 0);
        return Task.FromResult(new IcicleResult([.. segments.OrderBy(s => s.Level)], total, Dominators));
    }

    /// <summary>Dominator tree queries received.</summary>
    public List<DominatorTreeRequest> TreeQueries { get; } = [];

    public Task<DominatorTreeResult> QueryDominatorTreeAsync(DominatorTreeRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        TreeQueries.Add(request);
        var expanded = request.Expanded.ToHashSet(StringComparer.Ordinal);
        var focus = request.FocusAddress == 0 ? null : TreeNodes.FirstOrDefault(n => n.Address == request.FocusAddress);
        var total = TreeNodes.Where(n => n.ParentKey.Length == 0).Sum(n => n.Retained);
        var rows = new List<IReadOnlyList<string?>>();
        var keys = new List<string>();
        int ChildCount(FakeTreeNode node) => TreeNodes.Count(n => n.ParentKey == node.Key);
        void Add(FakeTreeNode node, int depth, bool open)
        {
            rows.Add(node.Cells(depth, open, ChildCount(node), total));
            keys.Add(node.Key);
        }

        void Emit(string parent, int depth)
        {
            foreach (var node in TreeNodes.Where(n => n.ParentKey == parent))
            {
                var open = expanded.Contains(node.Key) && ChildCount(node) > 0;
                Add(node, depth, open);
                if (open)
                {
                    Emit(node.Key, depth + 1);
                }
            }
        }

        // The focus's dominators from the top: its parents by key.
        var ancestors = new List<DominatorAncestorDto>();
        if (focus is not null)
        {
            for (var parent = TreeNodes.FirstOrDefault(n => n.Key == focus.ParentKey); parent is not null; parent = TreeNodes.FirstOrDefault(n => n.Key == parent.ParentKey))
            {
                ancestors.Insert(0, new DominatorAncestorDto(parent.Key, parent.Name, parent.Address, parent.Retained));
            }

            Add(focus, 0, true);
            Emit(focus.Key, 1);
        }
        else
        {
            Emit(string.Empty, 0);
        }

        var id = Guid.NewGuid();
        _sets[id] = rows;
        return Task.FromResult(new DominatorTreeResult(
            new ResultSetInfo(id, rows.Count, FakeTreeNode.Columns),
            focus?.Key,
            [.. ancestors.Take(DominatorTreeLimits.TrailHead), .. ancestors.Skip(DominatorTreeLimits.TrailHead).TakeLast(DominatorTreeLimits.TrailTail)],
            focus?.Retained ?? total,
            total,
            TreeNodes.Count(n => n.ParentKey.Length == 0),
            false,
            Dominators,
            ancestors.Count,
            request.RevealKey is { } reveal ? keys.IndexOf(reveal) : -1));
    }

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

    public Task<TraceDescriptor> OpenTraceAsync(OpenTraceRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (OpenFailure is { } failure)
        {
            return Task.FromException<TraceDescriptor>(failure);
        }

        TracesOpened.Add(request);
        var descriptor = new TraceDescriptor(Guid.NewGuid(), request.Path, 42, "App", DateTimeOffset.UnixEpoch, 10, 4, 400, 2, 0, 0, 0.1, TraceFileBytes, DateTimeOffset.UnixEpoch);
        return OpenTraceGate is { } gate ? gate.Task.ContinueWith(_ => descriptor, TaskScheduler.Default) : Task.FromResult(descriptor);
    }

    /// <summary>When set, trace opens wait for it (tests make queries overlap a re-read).</summary>
    public TaskCompletionSource? OpenTraceGate { get; set; }

    /// <summary>The trace file's size every open reports; a test changes it to make the file look changed.</summary>
    public long TraceFileBytes { get; set; } = 1000;

    public Task CloseTraceAsync(Guid traceId, CancellationToken cancellationToken)
    {
        TracesClosed.Add(traceId);
        return Task.CompletedTask;
    }

    public Task<AllocationTimelineResult> QueryAllocationTimelineAsync(AllocationTimelineRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new AllocationTimelineResult([new(0, 300, 3), new(5, 100, 1)], 5, 10));

    public Task<AllocationTypesResult> QueryAllocationTypesAsync(AllocationTypesRequest request, CancellationToken cancellationToken)
    {
        TypeQueries.Add(request);
        List<IReadOnlyList<string?>> rows = [["System.Byte[]", "300", "3", "75"], ["System.String", "100", "1", "25"]];
        var id = Guid.NewGuid();
        _sets[id] = rows;
        return Task.FromResult(new AllocationTypesResult(new ResultSetInfo(id, rows.Count, AllocationTypeColumnsList), 400, 4));
    }

    public Task<AllocationStacksResult> QueryAllocationStacksAsync(AllocationStacksRequest request, CancellationToken cancellationToken)
    {
        StackQueries.Add(request);
        AllocationStackDto[] stacks = [new(300, 3, ["App.Grow", "App.Handle", "App.Main"]), new(100, 1, ["App.Log", "App.Main"])];
        var shown = request.TypeName switch
        {
            "System.Byte[]" => stacks[..1],
            "System.String" => stacks[1..],
            _ => stacks,
        };
        return Task.FromResult(new AllocationStacksResult(shown, shown.Sum(s => s.Bytes), shown.Sum(s => s.Samples)));
    }

    public Task<AllocationTreeResult> QueryAllocationTreeAsync(AllocationTreeRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        AllocationTreeQueries.Add(request);
        var nodes = (request.Kind == AllocationTreeKindDto.CallTree ? CallTreeNodes : BackTraceNodes)
            .Where(n => request.TypeName is null || n.Type is null || n.Type == request.TypeName)
            .ToArray();
        var expanded = request.Expanded.ToHashSet(StringComparer.Ordinal);
        var rows = new List<IReadOnlyList<string?>>();
        var keys = new List<string>();
        var total = nodes.Where(n => n.Parent == AllocationTreeKeys.Root).Sum(n => n.Total);
        void Emit(string parent, int depth)
        {
            foreach (var node in nodes.Where(n => n.Parent == parent))
            {
                var children = nodes.Count(n => n.Parent == node.Key);
                var open = children > 0 && expanded.Contains(node.Key);
                rows.Add(
                [
                    node.Key, node.Parent, depth.ToString(CultureInfo.InvariantCulture), AllocationRowKinds.Frame, node.Name,
                    node.Total.ToString(CultureInfo.InvariantCulture), node.Own < 0 ? null : node.Own.ToString(CultureInfo.InvariantCulture), "1",
                    (100.0 * node.Total / total).ToString("0.##", CultureInfo.InvariantCulture), children.ToString(CultureInfo.InvariantCulture), open ? "1" : "0",
                ]);
                keys.Add(node.Key);
                if (open)
                {
                    Emit(node.Key, depth + 1);
                }
            }
        }

        Emit(AllocationTreeKeys.Root, 0);
        var id = Guid.NewGuid();
        _sets[id] = rows;
        return Task.FromResult(new AllocationTreeResult(new ResultSetInfo(id, rows.Count, AllocationTreeColumnsList), total, 4, false, request.RevealKey is { } reveal ? keys.IndexOf(reveal) : -1));
    }

    public TaskCompletionSource? InspectionGate { get; set; }

    public Task<IReadOnlyList<InspectionInfo>> ListInspectionsAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InspectionInfo>>([.. Inspections]);

    /// <summary>Inspection requests received, with their baselines (sprint 2.6).</summary>
    public List<InspectionRequest> InspectionRequests { get; } = [];

    public async Task<InspectionResultDto> RunInspectionAsync(InspectionRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        InspectionRuns.Add(request.Name);
        InspectionRequests.Add(request);
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
        return new InspectionResultDto(request.Name, rows.Count, 1000, request.Name == "loh" ? null : 64, null, new ResultSetInfo(id, rows.Count, columns), 0.1, request.BaselineSnapshotId);
    }

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

    public Task<string?> PickTraceFileAsync() => Task.FromResult(NextFile);

    /// <summary>Where a save picker answers (sprint 2.7); null cancels.</summary>
    public string? SaveFile { get; set; }

    public List<string> Messages { get; } = [];

    public Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        SuggestedNames.Add(suggestedName);
        return Task.FromResult(SaveFile);
    }

    public List<string> SuggestedNames { get; } = [];

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Questions.Add(message);
        return Task.FromResult(Answer);
    }

    /// <summary>Files asked to be opened or shown in their folder (the Live timeline).</summary>
    public List<string> Opened { get; } = [];

    public List<string> Shown { get; } = [];

    public Task<bool> OpenFileAsync(string path)
    {
        Opened.Add(path);
        return Task.FromResult(true);
    }

    public Task<bool> ShowInFolderAsync(string path)
    {
        Shown.Add(path);
        return Task.FromResult(true);
    }
}
