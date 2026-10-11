using System.Collections.ObjectModel;
using System.Windows.Input;
using CluadeX.Services.Batch;

namespace CluadeX.ViewModels;

/// <summary>One row of Settings ▸ Batch jobs.</summary>
public sealed class BatchJobRow
{
    public BatchJob Job { get; }
    public string Title { get; }
    public string StateText { get; }
    public string Summary { get; }
    public string? ErrorText { get; }
    /// <summary>What came back — kept here so an answer whose chat was deleted is not lost.</summary>
    public string AnswerText { get; }
    public bool IsOpen => Job.IsOpen;

    public BatchJobRow(BatchJob job, Func<string, string> t)
    {
        Job = job;
        Title = string.IsNullOrWhiteSpace(job.Title) ? job.Kind : job.Title;
        StateText = job.State switch
        {
            BatchState.InProgress => t("batch.state.running"),
            BatchState.Ended => job.Delivered ? t("batch.state.delivered") : t("batch.state.ended"),
            BatchState.Canceled => t("batch.state.canceled"),
            BatchState.Expired => t("batch.state.expired"),
            _ => t("batch.state.failed"),
        };
        string age = Ago(DateTime.UtcNow - job.CreatedUtc, t);
        string counts = job.IsOpen
            ? string.Format(t("batch.countsOpen"), job.Succeeded + job.Errored, job.Requests)
            : string.Format(t("batch.countsDone"), job.Succeeded, job.Errored);
        Summary = $"{job.Provider} · {job.Model} · {counts} · {age}" + (string.IsNullOrEmpty(job.Detail) ? "" : $" · {job.Detail}");
        ErrorText = job.LastError;
        AnswerText = job.Results == null ? ""
            : string.Join("\n\n───\n\n", job.Results.Select(r => r.Ok ? $"[{r.CustomId}]\n{r.Text}" : $"[{r.CustomId}] ⚠ {r.Error}"));
    }

    static string Ago(TimeSpan d, Func<string, string> t) =>
        d.TotalMinutes < 1 ? t("batch.ago.now")
        : d.TotalHours < 1 ? string.Format(t("batch.ago.min"), (int)d.TotalMinutes)
        : d.TotalDays < 1 ? string.Format(t("batch.ago.hour"), (int)d.TotalHours)
        : string.Format(t("batch.ago.day"), (int)d.TotalDays);
}

public partial class SettingsViewModel
{
    private BatchService? _batchService;

    public ObservableCollection<BatchJobRow> BatchJobs { get; } = new();

    private BatchJobRow? _selectedBatchJob;
    public BatchJobRow? SelectedBatchJob
    {
        get => _selectedBatchJob;
        set { if (SetProperty(ref _selectedBatchJob, value)) OnPropertyChanged(nameof(SelectedBatchAnswer)); }
    }
    public string SelectedBatchAnswer => _selectedBatchJob?.AnswerText ?? "";

    private string _batchStatusText = "";
    public string BatchStatusText { get => _batchStatusText; set => SetProperty(ref _batchStatusText, value); }

    public bool HasBatchService => _batchService != null;

    public ICommand CheckBatchesCommand { get; private set; } = null!;
    public ICommand CancelBatchCommand { get; private set; } = null!;
    public ICommand RemoveBatchCommand { get; private set; } = null!;

    private void InitBatchJobs(BatchService? batchService)
    {
        _batchService = batchService;
        CheckBatchesCommand = new AsyncRelayCommand(CheckBatchesAsync);
        CancelBatchCommand = new AsyncRelayCommand(CancelSelectedBatchAsync);
        RemoveBatchCommand = new RelayCommand(RemoveSelectedBatch);
        if (batchService == null) return;
        batchService.JobsChanged += () => App.Current?.Dispatcher.BeginInvoke(RefreshBatchJobs);
        RefreshBatchJobs();
    }

    private void RefreshBatchJobs()
    {
        if (_batchService == null) return;
        string? keep = _selectedBatchJob?.Job.Id;
        BatchJobs.Clear();
        foreach (var job in _batchService.Jobs.OrderByDescending(j => j.CreatedUtc))
            BatchJobs.Add(new BatchJobRow(job, T));
        SelectedBatchJob = BatchJobs.FirstOrDefault(r => r.Job.Id == keep);
        int open = BatchJobs.Count(r => r.IsOpen);
        BatchStatusText = BatchJobs.Count == 0 ? T("batch.none") : string.Format(T("batch.count"), BatchJobs.Count, open);
    }

    private async Task CheckBatchesAsync()
    {
        if (_batchService == null) return;
        BatchStatusText = T("batch.checking");
        await _batchService.PollOnceAsync(CancellationToken.None);
        RefreshBatchJobs();
    }

    private async Task CancelSelectedBatchAsync()
    {
        if (_batchService == null || SelectedBatchJob is not { IsOpen: true } row) return;
        var confirm = System.Windows.MessageBox.Show(
            string.Format(T("batch.cancelConfirm"), row.Title), T("batch.cancelTitle"),
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;
        try
        {
            await _batchService.CancelAsync(row.Job);
            BatchStatusText = T("batch.cancelSent");
        }
        catch (Exception ex)
        {
            _log?.Warn("Batch", $"cancelling batch {row.Job.BatchId} failed", ex);
            BatchStatusText = $"{T("settings.failedPrefix")}: {DescribeFailure(ex)}";
        }
    }

    private void RemoveSelectedBatch()
    {
        if (_batchService == null || SelectedBatchJob is not { } row) return;
        if (row.IsOpen) { BatchStatusText = T("batch.removeOpen"); return; }
        var confirm = System.Windows.MessageBox.Show(
            string.Format(T("batch.removeConfirm"), row.Title), T("batch.removeTitle"),
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;
        _batchService.Remove(row.Job);
    }
}
