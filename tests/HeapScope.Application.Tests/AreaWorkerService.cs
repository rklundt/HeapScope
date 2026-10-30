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

namespace HeapScope.Application.Tests;

/// <summary>
/// An <see cref="IWorkerService"/> made of only the contracts a test implements (sprint 3.8, peer review P5): each call goes
/// to the part that implements the method's contract, and a contract no part implements fails with
/// <see cref="NotSupportedException"/> naming it. A test fakes one area instead of every worker method.
/// </summary>
public class AreaWorkerService : DispatchProxy
{
    private object[] _parts = [];

    /// <summary>A worker service whose calls go to <paramref name="parts"/>, each implementing one or more contracts.</summary>
    public static IWorkerService Create(params object[] parts)
    {
        var proxy = Create<IWorkerService, AreaWorkerService>();
        ((AreaWorkerService)(object)proxy)._parts = parts;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var contract = targetMethod.DeclaringType!;
        var part = _parts.FirstOrDefault(contract.IsInstanceOfType)
            ?? throw new NotSupportedException($"This test worker does not implement {contract.Name}.");
        try
        {
            return targetMethod.Invoke(part, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }
}
