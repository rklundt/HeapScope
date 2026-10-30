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

using System.Diagnostics;
using System.Globalization;
using HeapScope.Core;
using HeapScope.Core.Synthetic;

namespace HeapScope.Storage.Tests;

/// <summary>
/// Index caches signed with the user's key (sprint 3.7, peer review P14) and caches mutated at random: whatever a file
/// next to a dump holds, opening it answers with a graph or a reason, never a crash, a hang or a forged graph.
/// </summary>
public sealed class SignedCacheTests : IDisposable
{
    private static readonly SnapshotFingerprint s_fingerprint = new(1, 2, 3);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    public SignedCacheTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void A_cache_opens_with_the_key_it_was_signed_with_and_no_other()
    {
        var cache = Write("signed.hsidx", _key);

        using (IndexCacheKey.UseScoped(_key))
        {
            var opened = IndexCacheReader.Open(cache, s_fingerprint, MemoryMode.InMemory, TestContext.Current.CancellationToken);
            opened.Success.ShouldBeTrue(opened.Detail);
            opened.Graph!.Dispose();
            IndexCacheReader.Probe(cache, s_fingerprint).ShouldBeNull("a valid signed cache probes valid");
        }

        using (IndexCacheKey.UseScoped([.. _key.Select(b => (byte)(b ^ 0xFF))]))
        {
            // Another user's or machine's cache, or one shipped next to a dump: never used, so the dump is read again.
            var other = IndexCacheReader.Open(cache, s_fingerprint, MemoryMode.InMemory, TestContext.Current.CancellationToken);
            other.Reason.ShouldBe(IndexCacheInvalidReason.Untrusted);
            IndexCacheReader.Probe(cache, s_fingerprint).ShouldNotBeNull().Reason.ShouldBe(IndexCacheInvalidReason.Untrusted);
        }
    }

    [Fact]
    public void A_cache_written_without_a_signature_is_untrusted_where_a_key_is_in_effect()
    {
        // Caches from before 3.7, and any forged cache whose checksums were recomputed.
        var cache = Write("unsigned.hsidx", key: null);

        using (IndexCacheKey.UseScoped(_key))
        {
            IndexCacheReader.Open(cache, s_fingerprint, MemoryMode.Mapped, TestContext.Current.CancellationToken).Reason.ShouldBe(IndexCacheInvalidReason.Untrusted);
        }
    }

    [Fact]
    public void An_unsigned_cache_and_one_signed_with_another_key_are_told_apart()
    {
        // 3.7 review: every cache from before 3.7 is unsigned; the note must not call it foreign.
        var unsigned = Write("unsigned-detail.hsidx", key: null);
        var foreign = Write("foreign-detail.hsidx", [.. _key.Select(b => (byte)(b ^ 0x5A))]);

        using (IndexCacheKey.UseScoped(_key))
        {
            IndexCacheReader.Probe(unsigned, s_fingerprint).ShouldNotBeNull().Detail!.ShouldContain("unsigned");
            IndexCacheReader.Probe(foreign, s_fingerprint).ShouldNotBeNull().Detail!.ShouldContain("another key");
            IndexCacheReader.GetInMemoryBytes(foreign, out var failure).ShouldBeNull();
            failure.ShouldNotBeNull().Reason.ShouldBe(IndexCacheInvalidReason.Untrusted, "the worker logs why the base cache was not used");
        }
    }

    [Fact]
    public void The_key_file_is_created_once_reloaded_and_replaced_when_damaged()
    {
        // 3.7 review: the key's lifecycle, which the worker logs.
        var path = Path.Combine(_directory, "data", IndexCacheKey.FileName);
        var (created, how) = IndexCacheKey.LoadOrCreate(path);
        how.ShouldBe(IndexCacheKeySource.Created);
        created.Length.ShouldBe(32);
        File.ReadAllBytes(path).ShouldBe(created);

        var (loaded, again) = IndexCacheKey.LoadOrCreate(path);
        again.ShouldBe(IndexCacheKeySource.Loaded);
        loaded.ShouldBe(created);

        File.WriteAllBytes(path, [1, 2, 3]);
        var (replaced, repaired) = IndexCacheKey.LoadOrCreate(path);
        repaired.ShouldBe(IndexCacheKeySource.ReplacedDamaged);
        replaced.Length.ShouldBe(32);
        File.ReadAllBytes(path).ShouldBe(replaced);
    }

    [Fact]
    public async Task Processes_creating_the_key_at_once_end_with_one_key()
    {
        var path = Path.Combine(_directory, "race", IndexCacheKey.FileName);
        var keys = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => IndexCacheKey.LoadOrCreate(path).Key, TestContext.Current.CancellationToken)));
        keys.Select(Convert.ToHexString).Distinct().ShouldHaveSingleItem();
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).ShouldBe(keys[0]);
    }

    [Fact]
    public async Task A_cache_mutated_at_random_answers_with_a_graph_or_a_reason()
    {
        // A small fuzz pass in every run; the nightly job raises HEAPSCOPE_FUZZ_ITERATIONS (sprint 3.7, peer review P13).
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("HEAPSCOPE_FUZZ_ITERATIONS"), CultureInfo.InvariantCulture, out var n) ? n : 60;
        var seed = int.TryParse(Environment.GetEnvironmentVariable("HEAPSCOPE_FUZZ_SEED"), CultureInfo.InvariantCulture, out var s) ? s : 1;
        var original = await File.ReadAllBytesAsync(Write("original.hsidx", key: null), TestContext.Current.CancellationToken);
        var random = new Random(seed);
        var mutated = Path.Combine(_directory, "mutated.hsidx");
        for (var i = 0; i < iterations; i++)
        {
            var bytes = (byte[])original.Clone();

            // Mostly the header page, where counts and offsets live; sometimes anywhere.
            for (var changes = random.Next(1, 8); changes > 0; changes--)
            {
                var at = random.Next(4) == 0 ? random.Next(bytes.Length) : random.Next(Math.Min(bytes.Length, IndexCacheFormat.PageSize));
                bytes[at] = (byte)random.Next(256);
            }

            await File.WriteAllBytesAsync(mutated, bytes, TestContext.Current.CancellationToken);
            foreach (var mode in Enum.GetValues<MemoryMode>())
            {
                var watch = Stopwatch.StartNew();
                var open = Task.Run(() => IndexCacheReader.Open(mutated, s_fingerprint, mode, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
                var result = await open.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                result.Graph?.Dispose();

                watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), $"seed {seed}, iteration {i}, {mode}");
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Write(string name, byte[]? key)
    {
        var path = Path.Combine(_directory, name);
        using var graph = SyntheticGraphs.Random(new RandomGraphOptions(2_000, 2, TypeCount: 20, RootCount: 5, Seed: 3));
        var metadata = new IndexCacheMetadata(new SnapshotInfo(SnapshotKind.FullDump, "x.dmp"), SnapshotCapabilities.FullDump, "test");
        using (key is null ? null : IndexCacheKey.UseScoped(key))
        {
            IndexCacheWriter.Write(path, graph, metadata, s_fingerprint, cancellationToken: TestContext.Current.CancellationToken);
        }

        return path;
    }
}
