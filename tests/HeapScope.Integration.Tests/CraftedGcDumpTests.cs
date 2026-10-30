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
/// raises <c>HEAPSCOPE_FUZZ_ITERATIONS</c> and varies <c>HEAPSCOPE_FUZZ_SEED</c>.
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
        var original = await File.ReadAllBytesAsync(shared.BaselineGcDump, TestContext.Current.CancellationToken);
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

            (await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken))).ShouldBe(load, $"seed {seed}, iteration {i}: the load did not end within 60 s");
            await load;
        }
    }

    public void Dispose() => _folder.Dispose();
}
