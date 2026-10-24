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

using System.Text.Json;
using HeapScope.Core;
using HeapScope.Core.Synthetic;

namespace HeapScope.Storage.Tests;

/// <summary>Optional metadata fields added in sprint 1.2 without a format version change.</summary>
public sealed class MetadataTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));

    public MetadataTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(MemoryMode.InMemory)]
    [InlineData(MemoryMode.Mapped)]
    public void Load_diagnostics_survive_a_round_trip(MemoryMode mode)
    {
        var snapshot = Path.Combine(_directory, "app.dmp");
        File.WriteAllBytes(snapshot, new byte[8192]);
        var fingerprint = SnapshotFingerprint.Compute(snapshot);
        var metadata = new IndexCacheMetadata(
            new SnapshotInfo(SnapshotKind.FullDump, snapshot),
            SnapshotCapabilities.FullDump,
            IndexCacheMetadata.CurrentWriterVersion,
            ["App.dll", "Library.dll"],
            UnreadableObjects: 3,
            LoaderVersion: 2);
        using var graph = SyntheticGraphs.Chain(10);
        var cache = snapshot + IndexCacheFormat.Extension;

        IndexCacheWriter.Write(cache, graph, metadata, fingerprint, cancellationToken: TestContext.Current.CancellationToken);
        var result = IndexCacheReader.Open(cache, fingerprint, mode, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue(result.Detail);
        result.Graph!.Dispose();
        result.Metadata!.DebuggableModules.ShouldBe(["App.dll", "Library.dll"]);
        result.Metadata.UnreadableObjects.ShouldBe(3);
        result.Metadata.LoaderVersion.ShouldBe(2);
    }

    [Fact]
    public void Metadata_written_before_the_fields_existed_reads_with_defaults()
    {
        const string written05 = """
            {"Metadata":{"Info":{"Kind":1,"SourcePath":"C:\\app.dmp","ProcessName":null,"ProcessId":null,"RuntimeVersion":null,"PointerSize":8,"CapturedAtUtc":null},"Capabilities":511,"WriterVersion":"0.5.0"},"DroppedReferenceCount":0,"DroppedRootCount":0}
            """;

        var document = JsonSerializer.Deserialize<MetadataDocument>(written05)!;

        document.Metadata.DebuggableModules.ShouldBeNull();
        document.Metadata.UnreadableObjects.ShouldBe(0);
        document.Metadata.LoaderVersion.ShouldBe(0, "caches written before 1.8 are rebuilt by the worker");
        document.Metadata.WriterVersion.ShouldBe("0.5.0");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
