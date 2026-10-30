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

namespace HeapScope.Worker.Rpc;

/// <summary>Reports a count of work done synchronously on the reporting thread.</summary>
internal sealed class CallbackProgress(Action<long> report) : IProgress<long>
{
    public void Report(long value) => report(value);
}

/// <summary>Reports synchronously on the reporting thread (<see cref="Progress{T}"/> would post and reorder).</summary>
internal sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
