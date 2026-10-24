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

using HeapScope.Contracts;

namespace HeapScope.Application.Workers;

/// <summary>Configuration of <see cref="WorkerHost"/> (configuration section "Worker").</summary>
public sealed class WorkerHostOptions
{
    public const string SectionName = "Worker";

    /// <summary>Full path of HeapScope.Worker.exe; defaults to the worker/ folder next to the executable.</summary>
    public string? WorkerPath { get; set; }

    /// <summary>When set, connects to an already running worker on this pipe instead of launching one.</summary>
    public string? AttachPipeName { get; set; }

    /// <summary>How long to wait for the worker's pipe.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an orderly shutdown may take before the worker is killed.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Configuration section of the DAC lookup options, bound onto these options too (ADR-017).</summary>
    public const string SymbolsSectionName = "Symbols";

    /// <summary>A user folder searched for the data access component (<c>Symbols:DacFolder</c>).</summary>
    public string? DacFolder { get; set; }

    /// <summary>
    /// Opt-in, default off (<c>Symbols:AllowMicrosoftSymbolServer</c>): a missing DAC may be downloaded from
    /// Microsoft's symbol server. Sends module identifiers, never dump contents.
    /// </summary>
    public bool AllowMicrosoftSymbolServer { get; set; }

    /// <summary>
    /// Server GC heaps (and GC threads) of the worker (<c>Worker:GcHeapCount</c>). Default: half the logical processors,
    /// so the worker's blocking collections cannot occupy every core and starve the UI thread (measured in sprint 1.3:
    /// 550 to 760 ms of input lag while a Medium snapshot opened with 24 heaps on 24 cores, under 200 ms with 12, at the
    /// same load time). A <c>DOTNET_GCHeapCount</c> already in the environment wins.
    /// </summary>
    public int? GcHeapCount { get; set; }

    /// <summary>The GC heap count the worker is started with.</summary>
    public int ResolveGcHeapCount() => Math.Max(1, GcHeapCount ?? Environment.ProcessorCount / 2);

    /// <summary>Starts the worker with its crash and hang test hooks enabled (tests only).</summary>
    public bool EnableTestHooks { get; set; }

    /// <summary>Protocol version the client announces in the handshake. Tests override it to exercise mismatches.</summary>
    internal int ClientProtocolVersion { get; set; } = WorkerProtocol.Version;

    /// <summary>The worker path to launch.</summary>
    public string ResolveWorkerPath() =>
        WorkerPath ?? Path.Combine(AppContext.BaseDirectory, "worker", "HeapScope.Worker.exe");
}
