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


using System.Reflection;
using HeapScope.Contracts;

namespace HeapScope.Architecture.Tests;

/// <summary>
/// The worker's contracts on the wire: JSON-RPC carries only method names, so names stay unique across every contract
/// the worker serves, whichever interface declares them, and each call can be canceled.
/// </summary>
public class WorkerContractTests
{
    private static IEnumerable<MethodInfo> Methods() => WorkerContracts.All.SelectMany(t => t.GetMethods());

    [Fact]
    public void Worker_contracts_are_interfaces_in_contracts_and_listed_once()
    {
        WorkerContracts.All.ShouldAllBe(t => t.IsInterface && t.Assembly == typeof(WorkerContracts).Assembly);
        WorkerContracts.All.Distinct().Count().ShouldBe(WorkerContracts.All.Count);
        typeof(WorkerContracts).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.Name.EndsWith("Service", StringComparison.Ordinal) && t != typeof(IWorkerService))
            .ShouldAllBe(t => WorkerContracts.All.Contains(t));
    }

    [Fact]
    public void Worker_contract_method_names_are_unique()
    {
        Methods().GroupBy(m => m.Name).Where(g => g.Count() > 1).Select(g => g.Key).ShouldBeEmpty();
    }

    [Fact]
    public void Worker_contract_methods_are_asynchronous_and_take_a_cancellation_token_last()
    {
        foreach (var method in Methods())
        {
            typeof(Task).IsAssignableFrom(method.ReturnType).ShouldBeTrue(method.Name);
            method.GetParameters().LastOrDefault()?.ParameterType.ShouldBe(typeof(CancellationToken), method.Name);
        }
    }
}
