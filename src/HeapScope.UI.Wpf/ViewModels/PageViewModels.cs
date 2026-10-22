using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application;
using HeapScope.Application.Jobs;
using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>Home page: for now, the synthetic diagnostic job (sprint 0.3).</summary>
public sealed partial class HomeViewModel(WorkerOperations operations) : ObservableObject
{
    private readonly WorkerOperations _operations = operations ?? throw new ArgumentNullException(nameof(operations));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunSyntheticCommand))]
    private int _objectCount = 5_000_000;

    [ObservableProperty]
    private string? _lastResult;

    /// <summary>The most recent synthetic job (smoke tests await it).</summary>
    public TrackedJob<SyntheticJobResult>? LastJob { get; private set; }

    private bool CanRunSynthetic() => ObjectCount is >= 0 and <= 100_000_000;

    [RelayCommand(CanExecute = nameof(CanRunSynthetic), AllowConcurrentExecutions = true)]
    private async Task RunSyntheticAsync()
    {
        var job = _operations.StartSyntheticJob(new SyntheticJobRequest(ObjectCount));
        LastJob = job;
        try
        {
            var result = await job.Result;
            LastResult = string.Create(
                CultureInfo.CurrentCulture,
                $"{result.ObjectCount:N0} objects, {result.ReferenceCount:N0} references, {result.TotalSize / (1024.0 * 1024.0):N1} MB, built in {result.ElapsedSeconds:N2} s");
            await _operations.ReleaseResultSetAsync(result.TypeSummary.Id, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            LastResult = "Canceled.";
        }
        catch (Exception ex) when (ex is WorkerStartException or WorkerCrashedException or WorkerOperationException)
        {
            LastResult = ex.Message;
        }
    }
}

/// <summary>Jobs page: the full job list with a way to clear finished jobs.</summary>
public sealed partial class JobsPageViewModel(ObservableCollection<JobViewModel> jobs, JobTracker tracker) : ObservableObject
{
    public ObservableCollection<JobViewModel> Jobs { get; } = jobs ?? throw new ArgumentNullException(nameof(jobs));

    [RelayCommand]
    private void ClearFinished()
    {
        tracker.ClearFinished();
        for (var i = Jobs.Count - 1; i >= 0; i--)
        {
            if (Jobs[i].Status != JobStatus.Running)
            {
                Jobs.RemoveAt(i);
            }
        }
    }
}

/// <summary>About page: product and worker versions.</summary>
public sealed partial class AboutViewModel(WorkerOperations operations) : ObservableObject
{
    [ObservableProperty]
    private string _workerDetails = "Not queried yet.";

    public string ProductVersion { get; } = $"{ProductInfo.Name} {ProductInfo.Version}";

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await operations.GetInfoAsync(cancellationToken);
            WorkerDetails = string.Create(
                CultureInfo.CurrentCulture,
                $"Worker {info.ProductVersion}, protocol {info.ProtocolVersion}, pid {info.ProcessId}, server GC {info.ServerGc}, working set {info.WorkingSetBytes / (1024.0 * 1024.0):N0} MB");
        }
        catch (Exception ex) when (ex is WorkerStartException or WorkerCrashedException or WorkerOperationException or OperationCanceledException)
        {
            WorkerDetails = ex.Message;
        }
    }
}
