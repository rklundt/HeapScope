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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application.Allocations;
using HeapScope.Application.Workers;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// The Allocations page (sprint 2.5, ADR-028): the allocation traces open in this session, one tab each. Traces come from
/// Record allocations on the Processes page or from a <c>.nettrace</c> file.
/// </summary>
public sealed partial class AllocationsViewModel : ObservableObject
{
    private readonly AllocationSession _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUserInteraction _interaction;
    private readonly ILoggerFactory? _loggers;
    private readonly ILogger _logger;

    [ObservableProperty]
    private AllocationTraceViewModel? _selected;

    [ObservableProperty]
    private string? _status;

    public AllocationsViewModel(AllocationSession session, IUiDispatcher dispatcher, IUserInteraction interaction, ILoggerFactory? loggers = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _loggers = loggers;
        _logger = loggers?.CreateLogger<AllocationsViewModel>() ?? (ILogger)NullLogger.Instance;
    }

    public ObservableCollection<AllocationTraceViewModel> Traces { get; } = [];

    public bool IsEmpty => Traces.Count == 0;

    /// <summary>Raised when a trace was opened and shown, so the shell switches to this page.</summary>
    public event EventHandler? TraceShown;

    /// <summary>Opens a trace file chosen in the file picker.</summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        if (await _interaction.PickTraceFileAsync() is { } path)
        {
            await OpenPathAsync(path);
        }
    }

    /// <summary>Reads a trace file in the worker and shows it.</summary>
    public async Task OpenPathAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Traces.FirstOrDefault(t => string.Equals(t.Trace.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            Selected = open;
            TraceShown?.Invoke(this, EventArgs.Empty);
            return;
        }

        Status = "Reading " + Path.GetFileName(path) + "...";
        try
        {
            Show(await _session.StartOpen(path).Result);
            Status = null;
        }
        catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
        {
            Status = ex.Message;
        }
    }

    /// <summary>
    /// Records sampled allocations of a process into the session folder (Record allocations on the Processes page), then
    /// shows the trace. <paramref name="stop"/> ends the recording early and keeps it.
    /// </summary>
    public async Task<TraceHandle> RecordAsync(int processId, string processName, TimeSpan duration, CancellationToken stop)
    {
        var handle = await _session.StartRecord(processId, processName, duration, stop).Result;
        Show(handle);
        return handle;
    }

    /// <summary>Shows a trace this session opened (a recording just finished, or a file).</summary>
    public void Show(TraceHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var tab = new AllocationTraceViewModel(handle, _session, _dispatcher, _loggers);
        Traces.Add(tab);
        OnPropertyChanged(nameof(IsEmpty));
        Selected = tab;
        _logger.LogInformation("Allocations: showing {Path}", handle.Path);
        tab.Start();
        TraceShown?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task CloseAsync(AllocationTraceViewModel? tab)
    {
        if (tab is null || !Traces.Remove(tab))
        {
            return;
        }

        OnPropertyChanged(nameof(IsEmpty));
        tab.Types.Close();
        tab.Tree.Close();
        if (Selected == tab)
        {
            Selected = Traces.LastOrDefault();
        }

        try
        {
            await _session.CloseAsync(tab.Trace, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
        {
            // The worker is gone or restarting; it holds nothing of this trace any more.
            _logger.LogInformation("Allocations: closing {Path} in the worker failed: {Reason}", tab.Trace.Path, ex.Message);
        }
    }
}
