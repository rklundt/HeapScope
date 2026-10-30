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

using HeapScope.Application.Sessions;
using Microsoft.Extensions.Logging;

namespace HeapScope.Application.Monitoring;

/// <summary>
/// A monitoring run's record in its session (sprint 3.4, split from the run in 3.8, peer review P11): the session folder
/// fixed at the start, marked in use so it cannot be deleted while the run writes into it, and the run's entry in
/// <c>session.json</c>, written at the start and at the end.
/// </summary>
internal sealed class RunRecord : IDisposable
{
    private readonly SessionLibrary _library;
    private readonly ILogger _logger;
    private readonly Guid _runId;
    private readonly IDisposable? _inUse;

    private RunRecord(SessionLibrary library, ILogger logger, Guid runId, string? folder, IDisposable? inUse)
    {
        _library = library;
        _logger = logger;
        _runId = runId;
        Folder = folder;
        _inUse = inUse;
    }

    /// <summary>The session folder the run writes into, or null when it records nothing.</summary>
    public string? Folder { get; }

    /// <summary>
    /// Fixes the run's session folder now, so opening another session meanwhile does not split the timeline from it.
    /// A session that cannot be read refuses the run with the reason (the front end shows it); a folder that cannot be
    /// reached leaves the run without a record or timeline.
    /// </summary>
    /// <exception cref="SessionUnreadableException">The current session cannot be read.</exception>
    public static RunRecord Open(SessionLibrary library, bool record, ILogger logger, Guid runId)
    {
        if (!record)
        {
            return new RunRecord(library, logger, runId, null, null);
        }

        try
        {
            var folder = library.Current.Folder;
            return new RunRecord(library, logger, runId, folder, library.MarkInUse(folder, "a monitoring run"));
        }
        catch (SessionUnreadableException ex)
        {
            logger.LogWarning("Monitoring run {RunId} refused: {Reason}", runId, ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Monitoring run {RunId} has no session folder: it is not recorded and writes no timeline", runId);
            return new RunRecord(library, logger, runId, null, null);
        }
    }

    /// <summary><paramref name="path"/> relative to the session folder, as the record keeps it.</summary>
    public string? Relative(string? path) => path is null || Folder is null ? null : Path.GetRelativePath(Folder, path);

    /// <summary>Writes the run's entry; a failure is logged and the run goes on.</summary>
    public void Write(SessionRun run, IReadOnlyList<string> snapshots)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (Folder is null)
        {
            return;
        }

        try
        {
            _library.RecordRun(Folder, run);
            _logger.LogInformation(
                "Monitoring run {RunId} recorded in {Folder} ({State}: snapshots {Snapshots}, forced collections {Forced}, timeline {Timeline}, trace {Trace})",
                _runId,
                Folder,
                run.EndedUtc is null ? "started" : "ended",
                snapshots.Count == 0 ? "none" : string.Join(", ", snapshots),
                run.ForcedGcs?.Count ?? 0,
                run.Timeline ?? "none",
                run.Trace ?? "none");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The monitoring run {RunId} could not be recorded in its session", _runId);
        }
    }

    /// <summary>Ends the in-use mark.</summary>
    public void Dispose() => _inUse?.Dispose();
}
