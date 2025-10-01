#nullable enable
using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;
using WANT.Service.Interface;

namespace WANT.Service
{
    /// <summary>
    /// Фоновый сервис: раз в 5 секунд обновляет список интерфейсов
    /// и печатает снапшот в консоль.
    /// </summary>
    public sealed class PortScannerWorker : BackgroundService
    {
        private readonly InterfaceScanner _store;

        public PortScannerWorker(InterfaceScanner store) => _store = store;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[PortScannerWorker] started");

            var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    Console.WriteLine("[PortScannerWorker] tick");

                    try
                    {
                        InterfaceSnapshotUpdater.UpdateOnce(_store);

                        var snapshot = _store.GetAll();
                        Console.WriteLine("=== Текущий список интерфейсов ===");
                        foreach (var ni in snapshot)
                        {
                            Console.WriteLine("----------------------------------------");
                            Console.WriteLine($"GUID:       {ni.Guid}");
                            Console.WriteLine($"Desc:       {ni.Description}");
                            Console.WriteLine($"OSName:     {ni.OSName}");
                            Console.WriteLine($"Friendly:   {ni.FriendlyName}");
                            Console.WriteLine($"MAC:        {ni.Mac}");
                            Console.WriteLine($"Status:     {ni.Status}");
                            Console.WriteLine($"UserName:   {ni.UserName}");
                            Console.WriteLine($"IP:         {ni.Ip}");
                            Console.WriteLine($"Mask:       {ni.Mask}");
                            Console.WriteLine($"PrefixLen:  {ni.PrefixLen}");
                            Console.WriteLine($"TrackState: {ni.TrackState}");
                        }
                        Console.WriteLine("========================================");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PortScannerWorker] Ошибка во время тика: {ex}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // нормальная остановка
            }
            finally
            {
                Console.WriteLine("[PortScannerWorker] stopped");
            }
        }
    }
}
