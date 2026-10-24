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

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application.Jobs;

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>One row of the jobs panel. Updated only on the UI thread by <see cref="ShellViewModel"/>.</summary>
public sealed partial class JobViewModel : ObservableObject
{
    private readonly TrackedJob _job;

    [ObservableProperty]
    private string _phase = "Starting";

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private JobStatus _status = JobStatus.Running;

    [ObservableProperty]
    private string _statusText = "Running";

    public JobViewModel(TrackedJob job)
    {
        _job = job ?? throw new ArgumentNullException(nameof(job));
        Refresh();
    }

    public Guid Id => _job.Id;

    public string Title => _job.Title;

    /// <summary>Copies the job's current state into bindable properties.</summary>
    public void Refresh()
    {
        Status = _job.Status;
        if (_job.Progress is { } progress)
        {
            Phase = progress.Phase;
            IsIndeterminate = progress.Total is not > 0;
            Percent = progress.Total is > 0 ? 100.0 * progress.Completed / progress.Total.Value : 0;
        }

        StatusText = Status switch
        {
            JobStatus.Running => IsIndeterminate ? Phase : string.Create(CultureInfo.CurrentCulture, $"{Phase} {Percent:N0}%"),
            JobStatus.Completed => "Completed",
            JobStatus.Canceled => "Canceled",
            JobStatus.Failed => "Failed: " + _job.Error,
            _ => Status.ToString(),
        };
        if (Status != JobStatus.Running)
        {
            IsIndeterminate = false;
            Percent = Status == JobStatus.Completed ? 100 : Percent;
        }
    }

    private bool CanCancel() => Status == JobStatus.Running;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _job.Cancel();
}
