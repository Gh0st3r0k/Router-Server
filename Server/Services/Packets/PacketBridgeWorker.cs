#nullable enable
using Microsoft.Extensions.Hosting;
using PacketDotNet;
using SharpPcap;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WANT.Service.Interface;

namespace WANT.Service.Packets
{
    /// <summary>
    /// Task2-bridge: слушаем все UP Ethernet из стора; если кадр ARP — только распознаём;
    /// иначе ретранслируем на остальные интерфейсы.
    /// </summary>
    public sealed class PacketBridgeWorker : BackgroundService
    {
        private readonly InterfaceScanner _store;
        private readonly ConcurrentDictionary<string, ICaptureDevice> _opened = new(StringComparer.OrdinalIgnoreCase);

        // простая анти-петля: дописываем хвост в кадр и не пересылаем его повторно
        private static readonly byte[] _bridgeTag = System.Text.Encoding.ASCII.GetBytes("WANTBRDG");

        public PacketBridgeWorker(InterfaceScanner store) => _store = store;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[PacketBridge] starting…");
            OpenAllUpFromStore();

            _store.InterfacesUpdated += _ => RebindDevices();

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                    await Task.Delay(1000, stoppingToken);
            }
            catch (TaskCanceledException) { }

            foreach (var d in _opened.Values) { try { d.StopCapture(); d.Close(); } catch { } }
            _opened.Clear();
            Console.WriteLine("[PacketBridge] stopped");
        }

        private void OpenAllUpFromStore()
        {
            var snapshot = _store.GetAll()
                                 .Where(ni => ni.Type == NicType.Ethernet && ni.Status == NicStatus.Up)
                                 .Select(ni => ni.Guid.Trim('{', '}').ToUpperInvariant())
                                 .ToList();

            foreach (var guid in snapshot) TryOpen(guid);
        }

        private void RebindDevices()
        {
            var want = _store.GetAll()
                             .Where(ni => ni.Type == NicType.Ethernet && ni.Status == NicStatus.Up)
                             .Select(ni => ni.Guid.Trim('{', '}').ToUpperInvariant())
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // close missing
            foreach (var kv in _opened.ToArray())
                if (!want.Contains(kv.Key) && _opened.TryRemove(kv.Key, out var dev))
                { try { dev.StopCapture(); dev.Close(); } catch { } Console.WriteLine($"[PacketBridge] closed {kv.Key}"); }

            // open new
            foreach (var guid in want) TryOpen(guid);
        }

        private void TryOpen(string guidUpper)
        {
            if (_opened.ContainsKey(guidUpper)) return;

            var dev = NpcapUtil.FindDeviceByGuid(guidUpper);
            if (dev is null) { Console.WriteLine($"[PacketBridge] no pcap device for {guidUpper}"); return; }

            try
            {
                // простой открывающий вызов, совместимый с SharpPcap
                dev.Open(DeviceModes.Promiscuous, read_timeout: 1000);
                dev.OnPacketArrival += Device_OnPacketArrival;
                dev.StartCapture();

                _opened[guidUpper] = dev;
                Console.WriteLine($"[PacketBridge] listening on {guidUpper} | {dev.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PacketBridge] open failed {guidUpper}: {ex.Message}");
                try { dev.Close(); } catch { }
            }
        }

        private void Device_OnPacketArrival(object? sender, PacketCapture e)
        {
            try
            {
                var rawCap = e.GetPacket();
                var data = rawCap.Data;
                if (data is null || data.Length < 14) return;

                var ingressGuid = FindIngressGuidByDevice(sender as ICaptureDevice);
                if (ingressGuid is null) return;

                Console.WriteLine($"[PacketBridge] RX from {ingressGuid}, len={data.Length}");

                var packet = Packet.ParsePacket(LinkLayers.Ethernet, data);
                var eth = packet.Extract<EthernetPacket>();
                if (eth is null) return;

                var etherType = (ushort)eth.Type;
                bool isArp = etherType == 0x0806;

                if (isArp)
                {
                    // Для ARP пока ничего не делаем (только RX залогирован)
                    return;
                }

                // НЕ ARP → шлём всем остальным интерфейсам
                foreach (var kv in _opened)
                {
                    if (string.Equals(kv.Key, ingressGuid, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        if (kv.Value is IInjectionDevice inj)
                        {
                            inj.SendPacket(data);
                            Console.WriteLine($"[PacketBridge] TX to {kv.Key}, len={data.Length}");
                        }
                        else
                        {
                            Console.WriteLine($"[PacketBridge] device {kv.Key} не поддерживает injection");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PacketBridge] send failed on {kv.Key}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PacketBridge] OnPacket error: {ex}");
            }
        }


        private string? FindIngressGuidByDevice(ICaptureDevice? dev)
        {
            if (dev is null) return null;
            foreach (var kv in _opened)
                if (object.ReferenceEquals(kv.Value, dev))
                    return kv.Key;
            return null;
        }

        private static bool HasBridgeTag(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < _bridgeTag.Length) return false;
            var tail = frame.Slice(frame.Length - _bridgeTag.Length, _bridgeTag.Length);
            return tail.SequenceEqual(_bridgeTag);
        }
        private static byte[] AddBridgeTag(ReadOnlySpan<byte> frame)
        {
            var arr = new byte[frame.Length + _bridgeTag.Length];
            frame.CopyTo(arr);
            _bridgeTag.CopyTo(arr.AsSpan(frame.Length));
            return arr;
        }
    }
}
