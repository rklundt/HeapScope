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
            UnreadableObjects: 3);
        using var graph = SyntheticGraphs.Chain(10);
        var cache = snapshot + IndexCacheFormat.Extension;

        IndexCacheWriter.Write(cache, graph, metadata, fingerprint, cancellationToken: TestContext.Current.CancellationToken);
        var result = IndexCacheReader.Open(cache, fingerprint, mode, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue(result.Detail);
        result.Graph!.Dispose();
        result.Metadata!.DebuggableModules.ShouldBe(["App.dll", "Library.dll"]);
        result.Metadata.UnreadableObjects.ShouldBe(3);
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
