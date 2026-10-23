using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application;
using HeapScope.Application.Jobs;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.UI.Wpf.Services;

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>A navigation entry of the shell.</summary>
public sealed record NavigationItem(string Title, ObservableObject Page);

/// <summary>
/// The shell: navigation, the jobs panel, worker status and the crash banner. Application events arrive on any
/// thread and are marshaled to the UI thread through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly WorkerOperations _operations;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<Guid, JobViewModel> _jobsById = [];

    [ObservableProperty]
    private NavigationItem _selectedItem;

    [ObservableProperty]
    private string _workerStatus = "Worker not started";

    [ObservableProperty]
    private bool _isCrashBannerVisible;

    [ObservableProperty]
    private string? _crashMessage;

    public ShellViewModel(WorkerOperations operations, IUiDispatcher dispatcher, HomeViewModel home, SnapshotsViewModel snapshots, AboutViewModel about)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Home = home ?? throw new ArgumentNullException(nameof(home));
        Snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        JobsPage = new JobsPageViewModel(Jobs, operations.Jobs);
        NavigationItems =
        [
            new NavigationItem("Home", Home),
            new NavigationItem("Snapshots", Snapshots),
            new NavigationItem("Jobs", JobsPage),
            new NavigationItem("About", about ?? throw new ArgumentNullException(nameof(about))),
        ];
        _selectedItem = NavigationItems[0];

        foreach (var job in operations.Jobs.Jobs)
        {
            AddJob(job);
        }

        // Opening a snapshot (from Home or the Snapshots page) shows it.
        Snapshots.PropertyChanged += OnSnapshotsPropertyChanged;
        operations.Jobs.JobAdded += OnJobAdded;
        operations.Jobs.JobChanged += OnJobChanged;
        operations.Worker.StateChanged += OnWorkerStateChanged;
        UpdateWorkerStatus(operations.Worker.State, null);
    }

    public string Title { get; } = $"{ProductInfo.Name} {ProductInfo.Version}";

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    public HomeViewModel Home { get; }

    public SnapshotsViewModel Snapshots { get; }

    public JobsPageViewModel JobsPage { get; }

    /// <summary>All jobs, newest first; the jobs panel and the Jobs page bind to it.</summary>
    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public void Dispose()
    {
        Snapshots.PropertyChanged -= OnSnapshotsPropertyChanged;
        Snapshots.Dispose();
        _operations.Jobs.JobAdded -= OnJobAdded;
        _operations.Jobs.JobChanged -= OnJobChanged;
        _operations.Worker.StateChanged -= OnWorkerStateChanged;
    }

    [RelayCommand]
    private async Task RestartWorkerAsync(CancellationToken cancellationToken)
    {
        IsCrashBannerVisible = false;
        try
        {
            await _operations.Worker.RestartAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is WorkerStartException or WorkerCrashedException or WorkerOperationException or OperationCanceledException)
        {
            CrashMessage = ex.Message;
            IsCrashBannerVisible = true;
        }
    }

    [RelayCommand]
    private void DismissCrashBanner() => IsCrashBannerVisible = false;

    private void OnSnapshotsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SnapshotsViewModel.Selected) && Snapshots.Selected is not null)
        {
            SelectedItem = NavigationItems[1];
        }
    }

    private void OnJobAdded(object? sender, TrackedJob job) => _dispatcher.Post(() => AddJob(job));

    private void OnJobChanged(object? sender, TrackedJob job) => _dispatcher.Post(() =>
    {
        if (_jobsById.TryGetValue(job.Id, out var viewModel))
        {
            viewModel.Refresh();
        }
    });

    private void OnWorkerStateChanged(object? sender, WorkerStateChangedEventArgs e) =>
        _dispatcher.Post(() => UpdateWorkerStatus(e.State, e.Message));

    private void AddJob(TrackedJob job)
    {
        if (_jobsById.ContainsKey(job.Id))
        {
            return;
        }

        var viewModel = new JobViewModel(job);
        _jobsById.Add(job.Id, viewModel);
        Jobs.Insert(0, viewModel);
    }

    private void UpdateWorkerStatus(WorkerState state, string? message)
    {
        WorkerStatus = state switch
        {
            WorkerState.NotStarted => "Worker not started",
            WorkerState.Starting => "Worker starting",
            WorkerState.Running => _operations.Worker.Info is { } info ? $"Worker running (pid {info.ProcessId})" : "Worker running",
            WorkerState.Crashed => "Worker stopped unexpectedly",
            WorkerState.Stopped => "Worker stopped",
            _ => state.ToString(),
        };

        if (state == WorkerState.Crashed)
        {
            CrashMessage = message ?? "The analysis worker stopped unexpectedly.";
            IsCrashBannerVisible = true;
        }
        else if (state == WorkerState.Running)
        {
            IsCrashBannerVisible = false;
        }
    }
}
