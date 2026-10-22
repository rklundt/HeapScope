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
