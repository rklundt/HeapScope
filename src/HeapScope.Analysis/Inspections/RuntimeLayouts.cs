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

namespace HeapScope.Analysis.Inspections;

/// <summary>
/// The runtime's private field names the inspections read, in one table (sprint 3.8, peer review P19): when a runtime
/// renames one, it changes here. A field missing from a dump reads as unknown, never as zero.
/// </summary>
internal static class RuntimeLayouts
{
    /// <summary><c>System.Delegate</c> and <c>System.MulticastDelegate</c>.</summary>
    public static class Delegates
    {
        public const string Target = "_target";
        public const string MethodPtrAux = "_methodPtrAux";
        public const string InvocationList = "_invocationList";
        public const string InvocationCount = "_invocationCount";
    }

    /// <summary><c>List</c>, <c>Queue</c>, <c>Stack</c>, <c>Dictionary</c> and <c>HashSet</c> of System.Collections.Generic.</summary>
    public static class Collections
    {
        public const string Size = "_size";
        public const string Count = "_count";
        public const string FreeCount = "_freeCount";
        public const string Items = "_items";
        public const string Array = "_array";
        public const string Entries = "_entries";
        public const string Buckets = "_buckets";
    }

    /// <summary><c>System.Threading.TimerQueueTimer</c> and <c>System.Timers.Timer</c>.</summary>
    public static class Timers
    {
        public const string Canceled = "_canceled";
        public const string DueTime = "_dueTime";
        public const string Period = "_period";
        public const string TimerCallback = "_timerCallback";
        public const string State = "_state";
        public const string OnIntervalElapsed = "_onIntervalElapsed";
    }

    /// <summary><c>System.Net.Http.HttpClient</c> (its base, <c>HttpMessageInvoker</c>).</summary>
    public static class HttpClients
    {
        public const string DisposeHandler = "_disposeHandler";
        public const string Handler = "_handler";
    }

    /// <summary>Microsoft.Extensions.DependencyInjection's <c>ServiceProviderEngineScope</c>.</summary>
    public static class ServiceScopes
    {
        public const string IsRootScope = "<IsRootScope>k__BackingField";
        public const string Disposed = "_disposed";
    }

    /// <summary><c>System.Threading.Tasks.Task</c>.</summary>
    public static class Tasks
    {
        public const string StateFlags = "m_stateFlags";
    }
}
