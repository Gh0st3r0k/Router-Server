using WANT.Service.Interface;

namespace WANT.Service;

/// <summary>Refreshes the physical interface snapshot every five seconds.</summary>
public sealed class PortScannerWorker : BackgroundService
{
    private readonly InterfaceScanner _store;

    public PortScannerWorker(InterfaceScanner store) => _store = store;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    InterfaceSnapshotUpdater.UpdateOnce(_store);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[Interfaces] Snapshot refresh failed: {exception}");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cancellation is the normal hosted-service shutdown path.
        }
    }
}
