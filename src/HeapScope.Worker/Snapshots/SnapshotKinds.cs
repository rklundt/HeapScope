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

using System.Collections.Concurrent;
using HeapScope.Core;
using HeapScope.Core.Graph;
using HeapScope.Core.Loading;
using HeapScope.Loaders.ClrMd;
using HeapScope.Loaders.GcDump;
using HeapScope.Storage;

namespace HeapScope.Worker.Snapshots;

/// <summary>
/// What the worker does differently per snapshot kind (sprint 1.9): the loader and its version, how an opened graph
/// becomes an <see cref="ISnapshot"/> (with or without a reader of the original file), the kind's data quality notes,
/// why a loader fact is missing, and how its type names are matched against another kind's. The worker asks these
/// handlers instead of checking the kind itself. Contracts' <c>CapabilityDescriptions</c> also words explanations per
/// kind, for front ends (ADR-016).
/// </summary>
internal abstract class SnapshotKindHandler
{
    public abstract SnapshotKind Kind { get; }

    /// <summary>The current version of the kind's loader; index caches from an older one are rebuilt.</summary>
    public abstract int LoaderVersion { get; }

    public abstract ISnapshotLoader CreateLoader();

    /// <summary>The snapshot over an opened graph. <paramref name="ensureUnchanged"/> checks the file before a reader uses it.</summary>
    public abstract ISnapshot Open(SnapshotInfo info, SnapshotCapabilities capabilities, HeapGraph graph, string path, Action ensureUnchanged);

    /// <summary>Notes on what this kind of snapshot can and cannot answer, given what its loader recorded.</summary>
    public abstract void AddNotes(IndexCacheMetadata metadata, List<string> notes);

    /// <summary>Why a feature named <paramref name="title"/> lacks a loader fact (<see cref="SnapshotFacts"/>) here.</summary>
    public abstract string ExplainMissingFact(string title);

    /// <summary>
    /// How this kind's type names are mapped before they are matched with a snapshot of <paramref name="other"/>, or null
    /// when they are matched as they are (a diff across kinds, sprint 1.8).
    /// </summary>
    public abstract Func<string, string>? NamesWhenComparedWith(SnapshotKind other);

    /// <summary>
    /// Refuses a snapshot opened from its index cache whose later reads would need native code nobody trusted (sprint 3.6
    /// review H1: the full dump's DAC next to the dump); nothing for kinds that load no native code.
    /// </summary>
    public virtual void CheckBeforeCacheOpen(string path)
    {
    }
}

/// <summary>The handlers of every kind, chosen by kind or by a file's first bytes.</summary>
/// <param name="dacChosen">Told which DAC each full dump's runtime was created with (sprint 2.3.7).</param>
internal sealed class SnapshotKinds
{
    // For what does not depend on the worker's options (texts, name forms, versions): explanations and diffs.
    private static readonly SnapshotKinds s_descriptions = new(new WorkerOptions(string.Empty, null));

    // DACs the user trusts to load from next to a dump, by SHA-256, as open requests named them (sprint 3.6, peer review
    // P2). Kept for the worker's lifetime: the field reader opens the dump again later with the same trust.
    private readonly ConcurrentDictionary<string, byte> _trustedDacs = new(StringComparer.Ordinal);

    private readonly SnapshotKindHandler[] _handlers;

    public SnapshotKinds(WorkerOptions options, Action<DacChoice>? dacChosen = null)
    {
        _handlers = [new FullDumpKind(options, dacChosen, _trustedDacs), new GcDumpKind()];
    }

    /// <summary>Adds the DACs an open request trusts.</summary>
    public void Trust(IReadOnlyList<string>? sha256s)
    {
        foreach (var sha256 in sha256s ?? [])
        {
            if (sha256 is { Length: 64 } && sha256.All(char.IsAsciiHexDigitLower))
            {
                _trustedDacs.TryAdd(sha256, 0);
            }
        }
    }

    /// <summary>The handler of <paramref name="kind"/> for texts, name forms and versions (not for loading).</summary>
    public static SnapshotKindHandler Describe(SnapshotKind kind) => s_descriptions.For(kind);

    public SnapshotKindHandler For(SnapshotKind kind) =>
        _handlers.FirstOrDefault(h => h.Kind == kind) ?? throw new ArgumentOutOfRangeException(nameof(kind), $"No handler for snapshot kind {kind}.");

    /// <summary>The handler for the file at <paramref name="path"/>, by its content, never its extension (sprint 1.8).</summary>
    /// <exception cref="SnapshotFormatException">The file is neither kind.</exception>
    public SnapshotKindHandler ForFile(string path) =>
        SnapshotFileSignature.Detect(path) is { } kind ? For(kind) : throw new SnapshotFormatException(SnapshotFileSignature.DescribeUnknown(path));

    private sealed class FullDumpKind(WorkerOptions options, Action<DacChoice>? dacChosen, ConcurrentDictionary<string, byte> trustedDacs) : SnapshotKindHandler
    {
        public override SnapshotKind Kind => SnapshotKind.FullDump;

        public override int LoaderVersion => ClrMdDumpLoader.CurrentVersion;

        public override ISnapshotLoader CreateLoader() => new ClrMdDumpLoader(LoaderOptions());

        public override void CheckBeforeCacheOpen(string path) => ClrMdDumpLoader.CheckDacTrust(path, LoaderOptions());

        // Field values and strings are read from the dump on demand (sprint 1.5); the reader opens the dump only when
        // something is inspected, after checking it is still the file the graph was built from.
        public override ISnapshot Open(SnapshotInfo info, SnapshotCapabilities capabilities, HeapGraph graph, string path, Action ensureUnchanged) =>
            new DumpSnapshot(info, capabilities, graph, new ClrMdObjectReader(path, LoaderOptions(), ensureUnchanged));

        public override void AddNotes(IndexCacheMetadata metadata, List<string> notes)
        {
            if (!metadata.Capabilities.HasFlag(SnapshotCapabilities.CompleteGraph))
            {
                notes.Add("Parts of the managed heap could not be read from this dump, so some objects may be missing and counts are not exact. The dump may be truncated or damaged; capture another full dump if results look wrong.");
            }

            if (metadata.Info.Facts.HasFlag(SnapshotFacts.CaptureTimeFromFile))
            {
                notes.Add("This core dump does not record when it was taken; its capture time is the file's time, which a copy of the file can change.");
            }
        }

        public override string ExplainMissingFact(string title) =>
            $"{title} needs data newer versions of HeapScope record, and this snapshot's index cache is older. Reopen the dump with Rebuild.";

        // A gcdump cannot name a nested type with its declaring type, so a full dump's names take the gcdump's form.
        public override Func<string, string>? NamesWhenComparedWith(SnapshotKind other) =>
            other == SnapshotKind.GcDump ? TypeNameForms.AsGcDumpName : null;

        private ClrMdLoaderOptions LoaderOptions() => new()
        {
            DacFolder = options.DacFolder,
            AllowMicrosoftSymbolServer = options.AllowMicrosoftSymbolServer,
            DacChosen = dacChosen,
            IsTrustedDac = trustedDacs.ContainsKey,
            TrustedDacCopyFolder = options.Folders.DacCopies,
        };
    }

    private sealed class GcDumpKind : SnapshotKindHandler
    {
        public override SnapshotKind Kind => SnapshotKind.GcDump;

        public override int LoaderVersion => GcDumpLoader.CurrentVersion;

        public override ISnapshotLoader CreateLoader() => new GcDumpLoader();

        // A gcdump holds no field values or string text: the graph is all there is.
        public override ISnapshot Open(SnapshotInfo info, SnapshotCapabilities capabilities, HeapGraph graph, string path, Action ensureUnchanged) =>
            new GraphSnapshot(info, capabilities, graph);

        public override void AddNotes(IndexCacheMetadata metadata, List<string> notes)
        {
            notes.Add("A gcdump holds the objects that survived the full garbage collection its capture ran, with their types, sizes and references. It has no generations, field values or string text; capture a full dump for those.");
            if (!metadata.Capabilities.HasFlag(SnapshotCapabilities.CompleteGraph))
            {
                notes.Add("This gcdump is sampled or incomplete (its writer kept only part of a very large heap, or the runtime's events were lost while it was captured), so some objects or types are missing and counts are not exact. Capture another gcdump, or a full dump, if results look wrong.");
            }
        }

        public override string ExplainMissingFact(string title) =>
            $"{title} needs data a gcdump does not record. Capture a full dump for it.";

        public override Func<string, string>? NamesWhenComparedWith(SnapshotKind other) => null;
    }
}
