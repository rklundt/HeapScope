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
using HeapScope.Core;
using HeapScope.Integration.Tests.Infrastructure;
using HeapScope.Loaders.GcDump;

namespace HeapScope.Integration.Tests;

/// <summary>
/// A real gcdump mutated at random (sprint 3.7, peer review P13): the loader answers with a graph or a format error within
/// a bounded time, never hangs, never allocates what the file cannot hold. A short pass in every run; the nightly job
/// raises <c>HEAPSCOPE_FUZZ_ITERATIONS</c> and varies <c>HEAPSCOPE_FUZZ_SEED</c>. The gcdump is captured live, so a seed
/// replays a failure only with the same base file (3.7 review): with <c>HEAPSCOPE_FUZZ_KEEP</c> set to a folder, the base
/// is kept there, the seed and iteration of the load in progress are written there (so a crash of the test host still
/// names them), and a failing input is kept; <c>HEAPSCOPE_FUZZ_BASE</c> names a kept base to replay from. Fixture data
/// only: LeakyConsole's heap holds nothing private.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CraftedGcDumpTests(StaticCacheGcDumps shared) : IClassFixture<StaticCacheGcDumps>, IDisposable
{
    private readonly TempFolder _folder = new();

    [Fact]
    public async Task A_mutated_gcdump_loads_or_is_refused_in_bounded_time()
    {
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("HEAPSCOPE_FUZZ_ITERATIONS"), CultureInfo.InvariantCulture, out var n) ? n : 20;
        var seed = int.TryParse(Environment.GetEnvironmentVariable("HEAPSCOPE_FUZZ_SEED"), CultureInfo.InvariantCulture, out var s) ? s : 1;
        var basePath = Environment.GetEnvironmentVariable("HEAPSCOPE_FUZZ_BASE") is { Length: > 0 } given ? given : shared.BaselineGcDump;
        var original = await File.ReadAllBytesAsync(basePath, TestContext.Current.CancellationToken);
        var keep = Environment.GetEnvironmentVariable("HEAPSCOPE_FUZZ_KEEP");
        if (!string.IsNullOrEmpty(keep))
        {
            Directory.CreateDirectory(keep);
            await File.WriteAllBytesAsync(Path.Combine(keep, "base.gcdump"), original, TestContext.Current.CancellationToken);
        }

        var random = new Random(seed);
        Directory.CreateDirectory(_folder.Path);
        var mutated = _folder.File("mutated.gcdump");
        for (var i = 0; i < iterations; i++)
        {
            var bytes = (byte[])original.Clone();
            for (var changes = random.Next(1, 6); changes > 0; changes--)
            {
                // Counts sit near the start and before each table: favor the first kilobytes.
                var at = random.Next(3) == 0 ? random.Next(bytes.Length) : random.Next(Math.Min(bytes.Length, 4096));
                bytes[at] = (byte)random.Next(256);
            }

            await File.WriteAllBytesAsync(mutated, bytes, TestContext.Current.CancellationToken);
            if (!string.IsNullOrEmpty(keep))
            {
                await File.WriteAllTextAsync(Path.Combine(keep, "in-progress.txt"), $"seed {seed}, iteration {i}", TestContext.Current.CancellationToken);
            }

            var load = Task.Run(
                () =>
                {
                    try
                    {
                        new GcDumpLoader().Load(mutated, cancellationToken: TestContext.Current.CancellationToken).Graph.Dispose();
                    }
                    catch (SnapshotFormatException)
                    {
                        // Refused with a reason: the expected answer for most mutations.
                    }
                },
                TestContext.Current.CancellationToken);

            var ended = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken)) == load;
            if ((!ended || load.IsFaulted) && !string.IsNullOrEmpty(keep))
            {
                File.Copy(mutated, Path.Combine(keep, $"failing-seed{seed}-iteration{i}.gcdump"), overwrite: true);
            }

            ended.ShouldBeTrue($"seed {seed}, iteration {i}: the load did not end within 60 s");
            try
            {
                await load;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"seed {seed}, iteration {i}: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }
    }

    public void Dispose() => _folder.Dispose();
}
