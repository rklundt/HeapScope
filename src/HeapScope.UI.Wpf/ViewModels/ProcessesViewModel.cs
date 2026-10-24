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
using HeapScope.UI.Wpf.Services;

namespace HeapScope.UI.Wpf.ViewModels;

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
/// and opening the captured dump through the Snapshots page (which runs the memory check).
/// </summary>
public sealed partial class ProcessesViewModel(ICaptureService capture, SnapshotsViewModel snapshots, IUserInteraction interaction) : ObservableObject
{
    private readonly ICaptureService _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    private readonly SnapshotsViewModel _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    private readonly IUserInteraction _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand))]
    private ProcessRow? _selected;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _showElevationHint;

    public ObservableCollection<ProcessRow> Processes { get; } = [];

    /// <summary>The path of the last successful capture (tests).</summary>
    public string? LastCapturePath { get; private set; }

    [RelayCommand]
    private async Task RefreshAsync()
    {
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

    private bool CanCapture() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureAsync()
    {
        var target = Selected!;
        IsBusy = true;
        Status = $"Capturing {target.Name} (pid {target.ProcessId}). The process is paused while its memory is written to disk.";
        try
        {
            var result = await _capture.StartCapture(target.ProcessId).Result;
            LastCapturePath = result.Path;
            Status = $"Captured {System.IO.Path.GetFileName(result.Path)} ({Format.Bytes(result.SizeBytes)}).{(result.Warning is null ? string.Empty : " " + result.Warning)}";
            if (_interaction.Confirm("Capture complete", $"{result.SecretsNote}\n\nOpen the dump now?"))
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
