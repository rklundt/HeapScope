using HeapScope.Application;
using HeapScope.Application.Jobs;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.UI.Wpf.Services;
using HeapScope.UI.Wpf.ViewModels;

namespace HeapScope.UI.Wpf.Tests;

public sealed class ShellViewModelTests : IDisposable
{
    private readonly FakeWorkerClient _worker = new();
    private readonly WorkerOperations _operations;
    private readonly ShellViewModel _shell;

    public ShellViewModelTests()
    {
        _operations = new WorkerOperations(_worker, new JobTracker(TimeProvider.System));
        _shell = new ShellViewModel(_operations, new ImmediateUiDispatcher(), new HomeViewModel(_operations), new AboutViewModel(_operations));
    }

    [Fact]
    public void Shell_has_title_navigation_and_starts_on_home()
    {
        _shell.Title.ShouldBe($"HeapScope {ProductInfo.Version}");
        _shell.NavigationItems.Select(n => n.Title).ShouldBe(["Home", "Jobs", "About"]);
        _shell.SelectedItem.Page.ShouldBeSameAs(_shell.Home);
        _shell.WorkerStatus.ShouldBe("Worker not started");
    }

    [Fact]
    public async Task Starting_a_job_adds_it_to_the_jobs_panel_and_progress_updates_it()
    {
        var run = _shell.Home.RunSyntheticCommand.ExecuteAsync(null);

        var row = _shell.Jobs.ShouldHaveSingleItem();
        row.Status.ShouldBe(JobStatus.Running);
        _worker.Service.LastProgress!.Report(new JobProgress("Adding objects", 25, 100));
        row.Phase.ShouldBe("Adding objects");
        row.Percent.ShouldBe(25);
        row.IsIndeterminate.ShouldBeFalse();
        row.StatusText.ShouldContain("25");

        _worker.Service.CompleteSynthetic(1000);
        await run;
        row.Status.ShouldBe(JobStatus.Completed);
        row.CancelCommand.CanExecute(null).ShouldBeFalse();
        _shell.Home.LastResult!.ShouldContain("1,000 objects");
        _worker.Service.Released.ShouldHaveSingleItem();
        _shell.JobsPage.Jobs.ShouldBeSameAs(_shell.Jobs);
    }

    [Fact]
    public async Task Cancel_calls_through_to_the_worker_call()
    {
        var run = _shell.Home.RunSyntheticCommand.ExecuteAsync(null);
        var row = _shell.Jobs.ShouldHaveSingleItem();

        row.CancelCommand.Execute(null);
        await run;

        _worker.Service.LastToken.IsCancellationRequested.ShouldBeTrue();
        row.Status.ShouldBe(JobStatus.Canceled);
        row.StatusText.ShouldBe("Canceled");
        _shell.Home.LastResult.ShouldBe("Canceled.");
    }

    [Fact]
    public async Task Crash_banner_appears_on_worker_crash_and_restart_hides_it()
    {
        _worker.Raise(WorkerState.Crashed, "The analysis worker stopped unexpectedly (exit code -1).");

        _shell.IsCrashBannerVisible.ShouldBeTrue();
        _shell.CrashMessage!.ShouldContain("exit code -1");
        _shell.WorkerStatus.ShouldBe("Worker stopped unexpectedly");

        await _shell.RestartWorkerCommand.ExecuteAsync(null);

        _worker.RestartCount.ShouldBe(1);
        _shell.IsCrashBannerVisible.ShouldBeFalse();
        _shell.WorkerStatus.ShouldBe("Worker running (pid 1234)");
    }

    [Fact]
    public void Clear_finished_removes_only_finished_rows()
    {
        _ = _shell.Home.RunSyntheticCommand.ExecuteAsync(null);
        _worker.Service.CompleteSynthetic(1);
        _ = _shell.Home.RunSyntheticCommand.ExecuteAsync(null);

        SpinWait.SpinUntil(() => _shell.Jobs.Count(j => j.Status == JobStatus.Completed) == 1, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        _shell.JobsPage.ClearFinishedCommand.Execute(null);

        _shell.Jobs.ShouldHaveSingleItem().Status.ShouldBe(JobStatus.Running);
    }

    [Fact]
    public async Task About_page_shows_worker_details()
    {
        var about = (AboutViewModel)_shell.NavigationItems[2].Page;

        await about.RefreshCommand.ExecuteAsync(null);

        about.WorkerDetails.ShouldContain("pid 1234");
        about.ProductVersion.ShouldBe($"HeapScope {ProductInfo.Version}");
    }

    public void Dispose() => _shell.Dispose();
}
