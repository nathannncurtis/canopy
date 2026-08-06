using System.IO;
using System.Windows;
using System.Windows.Controls;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class StorageDistributionView : UserControl
{
    CancellationTokenSource? _calculationCancellation;
    ScanResultManaged? _result;
    long _generation;

    public StorageDistributionView()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelCalculation();
    }

    /// <summary>Displays file storage distribution for a completed scan, or clears the view.</summary>
    public void SetResult(ScanResultManaged? result)
    {
        _result = result;
        _distribution.ItemsSource = null;
        if (result is null)
        {
            CancelCalculation();
            _status.Text = "Run a scan to view storage distribution.";
            return;
        }

        StartCalculation();
    }

    void OnGroupingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_distribution is not null && _result is not null)
            StartCalculation();
    }

    void StartCalculation()
    {
        ScanResultManaged? result = _result;
        if (result is null) return;

        CancelCalculation();
        var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        _calculationCancellation = cancellation;
        long generation = Volatile.Read(ref _generation);
        bool byCategory = _grouping.SelectedIndex == 1;
        _status.Text = $"Calculating storage by {(byCategory ? "category" : "extension")}...";
        _ = CalculateAsync(result, byCategory, cancellation, token, generation);
    }

    async Task CalculateAsync(
        ScanResultManaged result,
        bool byCategory,
        CancellationTokenSource cancellation,
        CancellationToken token,
        long generation)
    {
        try
        {
            IReadOnlyList<StorageDistributionBucket> buckets = await Task.Run(
                () => byCategory
                    ? StorageDistribution.ByCategory(result, token)
                    : StorageDistribution.ByExtension(result, token),
                token);
            if (generation != Volatile.Read(ref _generation) || token.IsCancellationRequested)
                return;

            _distribution.ItemsSource = buckets;
            ulong totalBytes = 0;
            ulong fileCount = 0;
            foreach (StorageDistributionBucket bucket in buckets)
            {
                totalBytes = SaturatingAdd(totalBytes, bucket.Bytes);
                fileCount = SaturatingAdd(fileCount, bucket.FileCount);
            }
            _status.Text = $"{buckets.Count:N0} groups · {fileCount:N0} files · " +
                           $"{totalBytes:N0} bytes";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _distribution.ItemsSource = null;
                _status.Text = $"Unable to calculate storage distribution: {ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_calculationCancellation, cancellation))
                _calculationCancellation = null;
            cancellation.Dispose();
        }
    }

    void CancelCalculation()
    {
        Interlocked.Increment(ref _generation);
        _calculationCancellation?.Cancel();
        _calculationCancellation = null;
    }

    static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
}
