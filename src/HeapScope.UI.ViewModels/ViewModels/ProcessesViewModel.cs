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
using HeapScope.Application.Sessions;
using HeapScope.Contracts;
using HeapScope.UI.Services;

namespace HeapScope.UI.ViewModels;

/// <summary>One row of the process list. Numbers stay numeric so the grid sorts them.</summary>
public sealed record ProcessRow(int ProcessId, string Name, string Runtime, string Elevation, long WorkingSetBytes, string CommandLine, bool NeedsAdministrator)
{
    public static ProcessRow From(ProcessEntry process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var elevation = process.Elevation switch
        {
            ProcessElevation.Elevated => process.NeedsAdministrator ? "Needs administrator" : "Elevated",
            ProcessElevation.NotElevated => string.Empty,
            _ => "Unknown",
        };
        return new ProcessRow(process.ProcessId, process.Name, process.RuntimeVersion ?? string.Empty, elevation, process.WorkingSetBytes ?? 0, process.CommandLine ?? string.Empty, process.NeedsAdministrator);
    }
}

/// <summary>
/// The Processes page (sprint 1.1): running .NET processes with elevation hints, capture into the current session,
/// and opening the captured dump through the Snapshots page (which runs the memory check). Since sprint 2.5 it also
/// records a process's allocations, shown on the Allocations page when done.
/// </summary>
public sealed partial class ProcessesViewModel(ICaptureService capture, SnapshotsViewModel snapshots, IUserInteraction interaction, AllocationsViewModel? allocations = null) : ObservableObject
{
    private CancellationTokenSource? _recording;

    private readonly ICaptureService _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    private readonly SnapshotsViewModel _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    private readonly IUserInteraction _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand), nameof(RecordAllocationsCommand))]
    private ProcessRow? _selected;

    /// <summary>True while Record allocations records; Stop ends it and keeps what was recorded.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordAllocationsCommand), nameof(StopRecordingCommand))]
    private bool _isRecording;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _showElevationHint;

    /// <summary>Capture a gcdump instead of a full dump (sprint 1.8): the object graph only, smaller, no field values.</summary>
    [ObservableProperty]
    private bool _captureGcDump;

    public ObservableCollection<ProcessRow> Processes { get; } = [];

    /// <summary>The path of the last successful capture (tests).</summary>
    public string? LastCapturePath { get; private set; }

    /// <summary>Why capture is not available on this system (sprint 2.3.6), or null; the page shows it instead of the list.</summary>
    public string? Unavailable => _capture.UnavailableReason;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Unavailable is { } reason)
        {
            Status = reason;
            return;
        }

        IsBusy = true;
        try
        {
            var processes = await _capture.ListProcessesAsync(CancellationToken.None);
            var selectedId = Selected?.ProcessId;
            Processes.Clear();
            foreach (var process in processes)
            {
                Processes.Add(ProcessRow.From(process));
            }

            Selected = Processes.FirstOrDefault(p => p.ProcessId == selectedId);
            ShowElevationHint = Processes.Any(p => p.NeedsAdministrator);
            Status = Processes.Count == 0 ? "No running .NET processes were found." : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
        {
            Status = "The process list could not be read: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCapture() => Selected is not null && Unavailable is null;

    /// <summary>Whether this page can record allocations (the host has an Allocations page).</summary>
    public bool CanRecordAllocations => allocations is not null;

    private bool CanRecord() => CanCapture() && allocations is not null && !IsRecording;

    /// <summary>
    /// Records the selected process's sampled allocations (sprint 2.5) for up to 30 s into the session folder, then shows
    /// them on the Allocations page.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRecord))]
    private async Task RecordAllocationsAsync()
    {
        var target = Selected!;
        _recording?.Dispose();
        _recording = new CancellationTokenSource();
        IsRecording = true;
        var duration = HeapScope.Application.Allocations.AllocationSession.DefaultRecording;
        Status = $"Recording the allocations of {target.Name} (pid {target.ProcessId}) for up to {duration.TotalSeconds:N0} s. Use the application now; Stop keeps what was recorded.";
        try
        {
            var handle = await allocations!.RecordAsync(target.ProcessId, target.Name, duration, _recording.Token);
            LastCapturePath = handle.Path;
            Status = $"Recorded {System.IO.Path.GetFileName(handle.Path)}: about {Format.Bytes(handle.Descriptor.SampledBytes)} allocated in {handle.Descriptor.DurationSeconds:N1} s. A trace holds method and type names, not heap contents; keep it private.";
        }
        catch (OperationCanceledException)
        {
            Status = "Recording canceled.";
        }
        catch (CaptureFailedException ex)
        {
            Status = ex.Message;
        }
        catch (HeapScope.Application.Workers.WorkerException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsRecording = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private void StopRecording() => _recording?.Cancel();

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureAsync()
    {
        var target = Selected!;
        IsBusy = true;
        var gcdump = CaptureGcDump;
        Status = gcdump
            ? $"Capturing a gcdump of {target.Name} (pid {target.ProcessId}). The process runs one full garbage collection and reports its heap."
            : $"Capturing {target.Name} (pid {target.ProcessId}). The process is paused while its memory is written to disk.";
        try
        {
            var result = await _capture.StartCapture(target.ProcessId, kind: gcdump ? SnapshotKindDto.GcDump : SnapshotKindDto.FullDump).Result;
            LastCapturePath = result.Path;
            Status = $"Captured {System.IO.Path.GetFileName(result.Path)} ({Format.Bytes(result.SizeBytes)}).{(result.Warning is null ? string.Empty : " " + result.Warning)}";
            if (await _interaction.ConfirmAsync("Capture complete", $"{result.SecretsNote}\n\nOpen the {(gcdump ? "gcdump" : "dump")} now?"))
            {
                await _snapshots.OpenPathAsync(result.Path);
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Capture canceled.";
        }
        catch (CaptureFailedException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
