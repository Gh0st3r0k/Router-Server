#nullable enable
using Microsoft.Extensions.Hosting;
using SharpPcap;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;                       // IPAddress
using System.Net.NetworkInformation;    // NetworkInterface, PhysicalAddress
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WANT.Service.Interface;

using PacketDotNet;                     // ArpPacket, ArpOperation, EthernetPacket, EthernetType

namespace WANT.Service.Packets
{
    /// <summary>
    /// Слушаем все UP Ethernet-интерфейсы; на ARP-запросы, адресованные IP этого же NIC (из стора), отправляем ARP-ответ.
    /// Никакой ретрансляции прочего трафика не делаем.
    /// </summary>
    public sealed class PacketBridgeWorker : BackgroundService
    {
        private readonly InterfaceScanner _store;

        private readonly ArpCache _arpCache;
        private readonly ConcurrentDictionary<string, ICaptureDevice> _opened = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentDictionary<string, ICaptureDevice> OpenedDevices => _opened;

        
        public bool TryGetDevice(string nicId, out ICaptureDevice dev)
            => _opened.TryGetValue(nicId, out dev!);

        public PacketBridgeWorker(InterfaceScanner store, ArpCache arpCache)
        {
            _store = store;
            _arpCache = arpCache;             
        }


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
                {
                    try { dev.StopCapture(); dev.Close(); } catch { }
                    Console.WriteLine($"[PacketBridge] closed {kv.Key}");
                }

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
                dev.Open(DeviceModes.Promiscuous, read_timeout: 1000);

                // ⚠️ Видим только ARP-запросы (who-has)
                dev.Filter = "arp";

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

                var dev = sender as ICaptureDevice;
                var ingressGuid = FindIngressGuidByDevice(dev);
                if (ingressGuid is null) return;

                // Разбираем Ethernet
                var packet = Packet.ParsePacket(LinkLayers.Ethernet, data);
                var eth = packet.Extract<EthernetPacket>();
                if (eth is null) return;

                // Интересует только ARP (BPF уже отрезал остальное, но проверим ещё раз)
                if (eth.Type != EthernetType.Arp) return;

                var arp = packet.Extract<ArpPacket>();
                if (arp is null) return;

                // УЧИМСЯ ТОЛЬКО ИЗ REPLY
                if (arp.Operation == ArpOperation.Response)
                {
                    var senderIp  = arp.SenderProtocolAddress;
                    var senderMac = arp.SenderHardwareAddress ?? eth.SourceHardwareAddress;

                    if (TryGetNicMac_FromSystem(ingressGuid, out var ourMacSub) &&
                        senderMac != null && senderMac.Equals(ourMacSub))
                        return;

                    if (TryGetNicIpsV4_FromStore(ingressGuid, out var ourIpsSub) &&
                        senderIp != null && ourIpsSub.Contains(senderIp))
                        return;

                    if (IsValidHostIp(senderIp) && senderMac != null && !senderMac.Equals(PhysicalAddress.None))
                    {
                        _arpCache.Learn(senderIp, senderMac, ingressGuid);
                        // Console.WriteLine($"[ARP][LEARN] {senderIp} -> {senderMac} on {ingressGuid}");
                    }
                    return; // reply обработан, дальше не отвечаем
                }



                // Только ARP-запрос (who-has)
                if (arp.Operation != ArpOperation.Request) return;

                Console.WriteLine($"1 -- [PacketBridge][ARP] request {arp.SenderProtocolAddress} is asking about {arp.TargetProtocolAddress} on {ingressGuid}");

                // Кого спрашивают (TPA)
                var targetIp = arp.TargetProtocolAddress;
                if (targetIp is null || targetIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return;

                Console.WriteLine($"2 -- [PacketBridge][ARP] request {arp.SenderProtocolAddress} is asking about {targetIp} on {ingressGuid}");

                // ⚙️ IP именно из ТВОЕГО стора (REST-названные пользователем)
                if (!TryGetNicIpsV4_FromStore(ingressGuid, out var ips) || !ips.Contains(targetIp))
                    return;

                Console.WriteLine($"3 -- [PacketBridge][ARP] request {arp.SenderProtocolAddress} is asking about {targetIp} on {ingressGuid} (we have it)");

                // Наш MAC — берём из ОС по GUID (надёжно и быстро)
                if (!TryGetNicMac_FromSystem(ingressGuid, out var ourMac))
                    return;

                // MAC запрашивающего
                var requesterMac = arp.SenderHardwareAddress ?? eth.SourceHardwareAddress;
                if (requesterMac is null || requesterMac.Equals(PhysicalAddress.None)) return;

                // Стоп-условия (gratuitous/self)
                if (arp.SenderProtocolAddress != null && arp.SenderProtocolAddress.Equals(targetIp)) return; // gratuitous
                if (requesterMac.Equals(ourMac)) return; // сами себе

                // Сборка ARP reply: "is-at <our-ip> <our-mac>"
                // Наш IP (тот, о котором спрашивали)
                var ourIp = arp.TargetProtocolAddress;

                // IP и MAC спрашивающего
                var theirIp = arp.SenderProtocolAddress;
                var theirMac = arp.SenderHardwareAddress ?? eth.SourceHardwareAddress;

                // Формируем ARP reply: "172.16.0.1 is-at 00:E0:4C:42:6E:CB"
                var arpReply = new ArpPacket(
                    ArpOperation.Response,
                    theirMac,    // THA — MAC того, кто спрашивал
                    theirIp,      // TPA — IP того, кто спрашивал
                    ourMac,      // SHA — наш MAC
                    ourIp       // SPA — наш IP (о котором спрашивали)
                );


                // Ethernet обёртка
                var ethReply = new EthernetPacket(
                    ourMac,                 // src
                    requesterMac,           // dst
                    EthernetType.Arp
                )
                {
                    PayloadPacket = arpReply
                };

                // Отправляем на том же NIC
                if (dev is IInjectionDevice inj)
                {
                    inj.SendPacket(ethReply.Bytes);
                    Console.WriteLine($"[PacketBridge][ARP] replied {targetIp} on {ingressGuid} -> {arp.SenderProtocolAddress}");
                }
                else
                {
                    Console.WriteLine($"[PacketBridge][ARP] device {ingressGuid} не поддерживает injection");
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
                if (ReferenceEquals(kv.Value, dev))
                    return kv.Key;
            return null;
        }

        // ====================== УТИЛИТЫ ======================

        /// <summary>
        /// MAC берём из ОС по GUID (Windows: NetworkInterface.Id == GUID без скобок).
        /// </summary>
        private bool TryGetNicMac_FromSystem(string guidUpper, out PhysicalAddress mac)
        {
            mac = PhysicalAddress.None;
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n =>
                    n.NetworkInterfaceType == NetworkInterfaceType.Ethernet &&
                    string.Equals(n.Id.Trim('{', '}').ToUpperInvariant(), guidUpper, StringComparison.OrdinalIgnoreCase));

            if (ni is null) return false;

            mac = ni.GetPhysicalAddress();
            return mac != null && !mac.Equals(PhysicalAddress.None);
        }

        /// <summary>
        /// IPv4 адреса берём из ТВОЕГО стора (_store), т.к. их задаёт пользователь через REST API.
        /// Метод максимально терпеливый: пытается найти IP в типичных полях (Ip, IPv4, Address, Addresses, коллекции и т.п.).
        /// </summary>
        private bool TryGetNicIpsV4_FromStore(string guidUpper, out HashSet<IPAddress> ips)
        {
            ips = new HashSet<IPAddress>();

            var ni = _store.GetAll()
                .FirstOrDefault(x => string.Equals(
                    x.Guid?.Trim('{', '}').ToUpperInvariant(),
                    guidUpper,
                    StringComparison.OrdinalIgnoreCase));

            if (ni is null)
            {
                Console.WriteLine($"[ARP] Interface {guidUpper}: not found in store");
                return false;
            }

            // Достаём значение свойства IP у объекта NetInterface
            string? ipStr = null;
            try
            {
                var prop = ni.GetType().GetProperty("IP", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null)
                {
                    ipStr = prop.GetValue(ni)?.ToString();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ARP] Error reading IP property: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(ipStr) && IPAddress.TryParse(ipStr, out var ip))
            {
                ips.Add(ip);
                Console.WriteLine($"[ARP] Interface {guidUpper}: found IP {ip}");
            }
            else
            {
                Console.WriteLine($"[ARP] Interface {guidUpper}: no IPv4 addresses found (IP='{ipStr ?? "null"}')");
            }

            return ips.Count > 0;
        }



        // -------- helper extractors (reflection-friendly, без знания точного типа NetInterface) --------

        private static void TryPullIPv4FromStringProperty(object obj, string[] propNames, HashSet<IPAddress> sink)
        {
            foreach (var name in propNames)
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p is null) continue;

                if (p.PropertyType == typeof(string))
                {
                    var val = p.GetValue(obj) as string;
                    if (!string.IsNullOrWhiteSpace(val) && IPAddress.TryParse(val, out var ip)
                        && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        sink.Add(ip);
                    }
                }
            }
        }

        private static void TryPullIPv4FromEnumerableOfStrings(object obj, string[] propNames, HashSet<IPAddress> sink)
        {
            foreach (var name in propNames)
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p is null) continue;

                var val = p.GetValue(obj);
                if (val is IEnumerable en && val is not string)
                {
                    foreach (var item in en)
                    {
                        if (item is string s &&
                            IPAddress.TryParse(s, out var ip) &&
                            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            sink.Add(ip);
                        }
                    }
                }
            }
        }

        private static void TryPullIPv4FromEnumerableOfObjects(object obj, string[] propNames, HashSet<IPAddress> sink)
        {
            foreach (var name in propNames)
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p is null) continue;

                var val = p.GetValue(obj);
                if (val is IEnumerable en && val is not string)
                {
                    foreach (var item in en)
                    {
                        if (item is null) continue;

                        // Попытка достать поле/свойство с именем IP/Address/Ipv4
                        var strIp = GetStringProperty(item, new[] { "IP", "Ip", "Address", "Addr", "IPv4", "Ipv4" });
                        if (!string.IsNullOrWhiteSpace(strIp) &&
                            IPAddress.TryParse(strIp, out var ip) &&
                            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            sink.Add(ip);
                        }
                    }
                }
            }
        }

        private void DumpStore()
        {
            Console.WriteLine("========== [STORE DUMP] ==========");
            var all = _store.GetAll();
            int i = 0;

            foreach (var ni in all)
            {
                Console.WriteLine($"--- Interface #{++i} ---");
                Console.WriteLine($"Guid:     {ni.Guid}");
                Console.WriteLine($"UserName: {ni.UserName}");
                Console.WriteLine($"Type:     {ni.Type}");
                Console.WriteLine($"Status:   {ni.Status}");
                Console.WriteLine($"MAC:      {ni.Mac}");
                Console.WriteLine($"IP:       {ni.Ip}");
                Console.WriteLine($"Mask:     {ni.Mask}");
                Console.WriteLine($"Desc:     {ni.Description}");

                // если есть Addresses, выведем все
                try
                {
                    var prop = ni.GetType().GetProperty("Addresses");
                    if (prop != null)
                    {
                        var val = prop.GetValue(ni);
                        if (val is IEnumerable<string> list)
                            Console.WriteLine($"Addresses: {string.Join(", ", list)}");
                        else if (val is IEnumerable en)
                        {
                            Console.WriteLine("Addresses:");
                            foreach (var item in en)
                                Console.WriteLine("  - " + item);
                        }
                    }
                }
                catch { }

                Console.WriteLine();
            }

            Console.WriteLine("=================================");
        }


        private static string? GetStringProperty(object obj, string[] propNames)
        {
            foreach (var name in propNames)
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p is null) continue;
                if (p.PropertyType == typeof(string))
                {
                    return p.GetValue(obj) as string;
                }
            }
            return null;
        }

        private static bool IsValidHostIp(IPAddress? ip)
        {
            if (ip is null) return false;
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            if (ip.Equals(IPAddress.Any)) return false;            // 0.0.0.0 (probe) — можно игнорить
            if (ip.Equals(IPAddress.Broadcast)) return false;      // 255.255.255.255
            return true;
        }

    }
}
