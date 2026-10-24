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
using System.IO;
using HeapScope.Contracts;
using Newtonsoft.Json;

namespace HeapScope.Application.Tests;

/// <summary>
/// Sprint 1.3 decision 6: the cost of receiving a full page of type summary rows (ten columns) in the client process,
/// with the serializer StreamJsonRpc's JsonMessageFormatter uses. The formatter changes only above 50 MB per page.
/// </summary>
public sealed class ResultPageJsonCostTests
{
    [Fact]
    public void A_full_page_of_type_summary_rows_deserializes_within_the_budget()
    {
        var json = JsonConvert.SerializeObject(WidePage());
        var serializer = new JsonSerializer();
        Deserialize(serializer, json);

        var times = new List<double>();
        long maxBytes = 0;
        for (var i = 0; i < 9; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            var page = Deserialize(serializer, json);
            watch.Stop();
            maxBytes = Math.Max(maxBytes, GC.GetAllocatedBytesForCurrentThread() - before);
            times.Add(watch.Elapsed.TotalMilliseconds);
            page.Rows.Count.ShouldBe(WorkerProtocol.MaxPageSize);
        }

        times.Sort();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{json.Length / 1048576.0:F1} MB of JSON; deserialize median {times[times.Count / 2]:F1} ms, {maxBytes / 1048576.0:F1} MB allocated");
        maxBytes.ShouldBeLessThan(50L * 1024 * 1024);
    }

    private static ResultPage Deserialize(JsonSerializer serializer, string json)
    {
        using var reader = new JsonTextReader(new StringReader(json));
        return serializer.Deserialize<ResultPage>(reader)!;
    }

    private static ResultPage WidePage()
    {
        var rows = new List<IReadOnlyList<string?>>(WorkerProtocol.MaxPageSize);
        for (var i = 0; i < WorkerProtocol.MaxPageSize; i++)
        {
            var n = (i + 1L) * 7919;
            rows.Add([
                $"Contoso.Inventory.Services.Caching.WarehouseEntry{i}",
                n.ToString(CultureInfo.InvariantCulture),
                (n * 48).ToString(CultureInfo.InvariantCulture),
                (n % 10000 / 100.0).ToString("F4", CultureInfo.InvariantCulture),
                (n * 3).ToString(CultureInfo.InvariantCulture),
                (n * 5).ToString(CultureInfo.InvariantCulture),
                (n * 40).ToString(CultureInfo.InvariantCulture),
                "0",
                "0",
                "0",
            ]);
        }

        return new ResultPage(Guid.NewGuid(), 0, 30_000, rows);
    }
}
