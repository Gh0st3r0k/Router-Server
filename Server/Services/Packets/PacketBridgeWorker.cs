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
using WANT.Service.Routing;             // ✅ добавили
using System.Net.Sockets;              // ← для AddressFamily
using WANT.Service.Rip;
using WANT.Service.Dhcp;



using PacketDotNet;                     // ArpPacket, ArpOperation, EthernetPacket, EthernetType, IPv4Packet

namespace WANT.Service.Packets
{
    /// <summary>
    /// Слушаем все UP Ethernet-интерфейсы;
    /// - учимся из ARP reply (заполняем ArpCache),
    /// - отвечаем на ARP-запросы (OWN + PROXY ARP),
    /// - маршрутизируем IPv4-пакеты между интерфейсами.
    /// </summary>
    public sealed class PacketBridgeWorker : BackgroundService
    {

        private readonly InterfaceScanner _store;
        private readonly ArpCache _arpCache;
        private readonly IRoutingService _routingService;
        private readonly IRipService _ripService;   // ✅ добавили

        private readonly IDhcpService _dhcpService;

        private readonly ConcurrentDictionary<string, ICaptureDevice> _opened = new(StringComparer.OrdinalIgnoreCase);
        public static readonly PhysicalAddress BroadcastMac =
            new PhysicalAddress(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

        public ConcurrentDictionary<string, ICaptureDevice> OpenedDevices => _opened;

        public bool TryGetDevice(string nicId, out ICaptureDevice dev)
            => _opened.TryGetValue(nicId, out dev!);


        // включён ли Proxy ARP на интерфейсе (GUID без скобок, Upper)
        private readonly ConcurrentDictionary<string, bool> _proxyArpEnabled =
            new(StringComparer.OrdinalIgnoreCase);

        public PacketBridgeWorker(
            InterfaceScanner store,
            ArpCache arpCache,
            IRoutingService routingService,
            IRipService ripService,
            IDhcpService dhcpService)          // ✅ ДОБАВИЛСЯ параметр
        {
            _store = store;
            _arpCache = arpCache;
            _routingService = routingService;
            _ripService = ripService;
            _dhcpService = dhcpService;

            // По умолчанию включаем Proxy ARP для всех известных Ethernet-интерфейсов
            foreach (var ni in _store.GetAll())
            {
                if (ni.Type != NicType.Ethernet) continue;
                var id = ni.Guid?.Trim('{', '}').ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(id)) continue;
                _proxyArpEnabled.TryAdd(id, true);
            }
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

            // Обновляем словарь Proxy ARP: по умолчанию ВКЛЮЧЁН и для новых интерфейсов
            foreach (var guid in want)
            {
                _proxyArpEnabled.TryAdd(guid, true);
            }
        }

        private void TryOpen(string guidUpper)
        {
            if (_opened.ContainsKey(guidUpper)) return;

            var dev = NpcapUtil.FindDeviceByGuid(guidUpper);
            if (dev is null) { Console.WriteLine($"[PacketBridge] no pcap device for {guidUpper}"); return; }

            try
            {
                dev.Open(DeviceModes.Promiscuous, read_timeout: 1000);

                // ✅ слушаем и ARP, и IPv4
                dev.Filter = "arp or ip";

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

                switch (eth.Type)
                {
                    case EthernetType.Arp:
                        HandleArpPacket(packet, eth, ingressGuid, dev);
                        break;

                    case EthernetType.IPv4:
                        HandleIPv4Packet(packet, eth, ingressGuid, dev);
                        break;

                    default:
                        // остальное пока не интересно
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PacketBridge] OnPacket error: {ex}");
            }
        }

        // ====================== ARP (OWN + PROXY) ======================

        private void HandleArpPacket(Packet packet, EthernetPacket eth, string ingressGuid, ICaptureDevice? dev)
        {
            var arp = packet.Extract<ArpPacket>();
            if (arp is null) return;

            // ---- 1) ARP REPLY: учимся в кэше ----
            if (arp.Operation == ArpOperation.Response)
            {
                var senderIp = arp.SenderProtocolAddress;
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
                }
                return;
            }

            // ---- 2) Нас интересуют только ARP REQUEST ----
            if (arp.Operation != ArpOperation.Request) return;

            var targetIp = arp.TargetProtocolAddress;
            if (targetIp is null || targetIp.AddressFamily != AddressFamily.InterNetwork) return;

            Console.WriteLine($"[PacketBridge][ARP] request {arp.SenderProtocolAddress} is asking about {targetIp} on {ingressGuid}");

            // Наш MAC на этом интерфейсе
            if (!TryGetNicMac_FromSystem(ingressGuid, out var ourMac))
                return;

            // MAC и IP спрашивающего
            var requesterMac = arp.SenderHardwareAddress ?? eth.SourceHardwareAddress;
            var requesterIp  = arp.SenderProtocolAddress;

            if (requesterMac is null || requesterMac.Equals(PhysicalAddress.None)) return;

            // gratuitous/self
            if (requesterIp != null && requesterIp.Equals(targetIp)) return;
            if (requesterMac.Equals(ourMac)) return;

            // ---- 2.1: обычный ARP — IP действительно наш на этом интерфейсе ----
            bool weOwnTarget =
                TryGetNicIpsV4_FromStore(ingressGuid, out var ourIps) &&
                ourIps.Contains(targetIp);

            // Флаг Proxy ARP с учётом настройки (по умолчанию true)
            bool proxyAllowed = IsProxyArpEnabled(ingressGuid);

            bool shouldProxy = false;

            // ---- 2.2: Proxy ARP — IP не наш, но ДО КОГО-ТО есть маршрут ----
            if (!weOwnTarget && proxyAllowed)
            {
                var decision = _routingService.Resolve(targetIp);
                // Proxy ARP имеет смысл только если:
                //  - есть маршрут
                //  - маршрут уходит через ДРУГОЙ интерфейс (иначе мы притворимся хостом в той же сети)
                if (decision.Route != null &&
                    !string.IsNullOrWhiteSpace(decision.OutInterfaceName) &&
                    !decision.OutInterfaceName.Equals(
                        ingressGuid,
                        StringComparison.OrdinalIgnoreCase))
                {
                    shouldProxy = true;
                }
            }

            if (!weOwnTarget && !shouldProxy)
            {
                // Ни наш IP, ни маршрута нет – молчим
                return;
            }

            var mode = weOwnTarget ? "OWN" : "PROXY";

            // Формируем ARP reply и в OWN, и в PROXY:
            // SHA = наш MAC, SPA = targetIp,
            // THA = MAC спрашивающего, TPA = IP спрашивающего
            var arpReply = new ArpPacket(
                ArpOperation.Response,
                requesterMac,   // THA
                requesterIp,    // TPA
                ourMac,         // SHA
                targetIp        // SPA
            );

            var ethReply = new EthernetPacket(
                ourMac,                 // src
                requesterMac,           // dst
                EthernetType.Arp
            )
            {
                PayloadPacket = arpReply
            };

            if (dev is IInjectionDevice inj)
            {
                inj.SendPacket(ethReply.Bytes);
                Console.WriteLine($"[PacketBridge][ARP-{mode}] replied for {targetIp} on {ingressGuid} -> {requesterIp}");
            }
            else
            {
                Console.WriteLine($"[PacketBridge][ARP] device {ingressGuid} не поддерживает injection");
            }
        }


        // ====================== IPv4 ROUTING ======================

        private void HandleIPv4Packet(Packet packet, EthernetPacket eth, string ingressGuid, ICaptureDevice? dev)
        {
            var ip = packet.Extract<IPv4Packet>();
            if (ip is null) return;

            // ---- RIPv2: UDP/520 ----
            if (ip.Protocol == PacketDotNet.ProtocolType.Udp)
            {
                var udp = packet.Extract<UdpPacket>();

                // DHCP: клиент 68, сервер 67
                if (udp.SourcePort is 67 or 68 || udp.DestinationPort is 67 or 68)
                {
                    var respEth = _dhcpService.HandleIncomingDhcpPacket(ingressGuid, eth, ip, udp);
                    // DHCP – чисто L2/L3-вещание, дальше как "routing" не идём
                    if (respEth != null && dev is IInjectionDevice dhcpInj)
                    {
                        dhcpInj.SendPacket(respEth.Bytes);
                        Console.WriteLine($"[DHCP] reply frame sent on {ingressGuid}, len={respEth.Bytes.Length}");
                    }
                    else
                    {
                        Console.WriteLine($"[DHCP] no reply generated (respEth is null or device not injectable) on {ingressGuid}");
                    }
                    return;
                }

                if (udp != null && (udp.SourcePort == 520 || udp.DestinationPort == 520))
                {
                    var ingressId = ingressGuid;
                    _ripService.HandleIncomingRipPacket(ingressId, ip, udp);
                    // RIPv2-пакеты НЕ маршрутизируем дальше как обычный трафик
                    return;
                }
            }


            // 🔥 Фильтр: игнорируем пакеты, которые мы сами отправили с этого интерфейса
            if (TryGetNicMac_FromSystem(ingressGuid, out var ourMacIn) &&
                eth.SourceHardwareAddress != null &&
                eth.SourceHardwareAddress.Equals(ourMacIn))
            {
                return;
            }

            var dst = ip.DestinationAddress;
            var src = ip.SourceAddress;

            // Broadcast, multicast и прочее пока не маршрутизируем
            if (dst.Equals(IPAddress.Broadcast)) return;
            var firstOctet = dst.GetAddressBytes()[0];
            if (firstOctet >= 224 && firstOctet <= 239) return; // multicast

            if (ip.TimeToLive <= 1)
            {
                Console.WriteLine($"[IPv4] TTL expired for {src} -> {dst}");
                return;
            }

            // Пакет к самому роутеру – пока игнорим
            if (IsOurIp(dst))
            {
                Console.WriteLine($"[IPv4] packet to router itself {dst}, ignore for now");
                return;
            }

            // ---- 1) РЕШЕНИЕ МАРШРУТА ЧЕРЕЗ IRoutingService (с рекурсией) ----
            var decision = _routingService.Resolve(dst);

            if (decision.Route == null || string.IsNullOrWhiteSpace(decision.OutInterfaceName))
            {
                Console.WriteLine($"[IPv4][ROUTE] no route to {dst}");
                return;
            }

            // GUID выходного интерфейса – из последнего маршрута в цепочке
            var egressGuid = decision.OutInterfaceName!.Trim('{', '}').ToUpperInvariant();

            // next-hop, по которому надо делать ARP:
            // - для directly connected сети: это конечный IP (dst)
            // - для статиков через next-hop: это IP соседа
            var nextHopIp = decision.NextHop ?? dst;

            // ---- 2) Декремент TTL ----
            ip.TimeToLive--;
            ip.Checksum = ip.CalculateIPChecksum();

            // ---- 3) ARP для конечного next-hop-а ----
            if (!_arpCache.TryGet(nextHopIp, egressGuid, out var nextHopMac))
            {
                Console.WriteLine($"[IPv4][ROUTE] no ARP for {nextHopIp} on {egressGuid}, sending ARP request & dropping packet");
                TrySendArpRequest(egressGuid, nextHopIp);
                return;
            }

            // ---- 4) Наш MAC на выходном интерфейсе ----
            if (!TryGetNicMac_FromSystem(egressGuid, out var ourMacOut))
                return;

            // ---- 5) Собираем новый Ethernet-фрейм ----
            var fwdEth = new EthernetPacket(
                ourMacOut,          // src MAC
                nextHopMac,         // dst MAC
                EthernetType.IPv4
            )
            {
                PayloadPacket = ip
            };

            if (_opened.TryGetValue(egressGuid, out var outDev) && outDev is IInjectionDevice inj)
            {
                inj.SendPacket(fwdEth.Bytes);
                Console.WriteLine($"[IPv4][ROUTE] {src} -> {dst} via {egressGuid}, nextHop {nextHopIp} ({nextHopMac})");
            }
            else
            {
                Console.WriteLine($"[IPv4][ROUTE] device {egressGuid} not injectable");
            }
        }



        // ====================== ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ======================

        private string? FindIngressGuidByDevice(ICaptureDevice? dev)
        {
            if (dev is null) return null;
            foreach (var kv in _opened)
                if (ReferenceEquals(kv.Value, dev))
                    return kv.Key;
            return null;
        }

        /// <summary>
        /// Проверяем, принадлежит ли указанный IP самому роутеру (любому интерфейсу).
        /// </summary>
        private bool IsOurIp(IPAddress ip)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return false;

            foreach (var ni in _store.GetAll())
            {
                var s = ni.Ip?.ToString();
                if (!string.IsNullOrWhiteSpace(s) &&
                    IPAddress.TryParse(s, out var nip) &&
                    nip.Equals(ip))
                    return true;
            }
            return false;
        }

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


        private void TrySendArpRequest(string egressGuid, IPAddress targetIp)
        {
            try
            {
                if (targetIp.AddressFamily != AddressFamily.InterNetwork)
                    return;

                if (!TryGetNicMac_FromSystem(egressGuid, out var ourMac))
                {
                    Console.WriteLine($"[ARP][REQ] no MAC for {egressGuid}");
                    return;
                }

                if (!TryGetNicIpsV4_FromStore(egressGuid, out var ips) || ips.Count == 0)
                {
                    Console.WriteLine($"[ARP][REQ] no IPv4 on {egressGuid}");
                    return;
                }

                var ourIp = ips.First();

                // 🔽 ВОТ ЗДЕСЬ БЫЛ ТВОЙ new ArpPacket(...) — МЕНЯЕМ НА ВЫЗОВ ЛОКАЛЬНОЙ ФУНКЦИИ
                if (_opened.TryGetValue(egressGuid, out var dev) && dev is IInjectionDevice inj)
                {
                    SendArpRequest_Local(inj, ourMac, ourIp, targetIp);
                    Console.WriteLine($"[ARP][REQ] (LOCAL) who-has {targetIp} tell {ourIp} on {egressGuid}");
                }
                else
                {
                    Console.WriteLine($"[ARP][REQ] device {egressGuid} not injectable");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ARP][REQ] error: {ex}");
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
            if (ip.Equals(IPAddress.Any)) return false;            // 0.0.0.0
            if (ip.Equals(IPAddress.Broadcast)) return false;      // 255.255.255.255
            return true;
        }


        private void SendArpRequest_Local(
            IInjectionDevice dev,
            PhysicalAddress srcMac,
            IPAddress srcIp,
            IPAddress targetIp)
        {
            var eth = new EthernetPacket(
                srcMac,
                PhysicalAddress.Parse("FF-FF-FF-FF-FF-FF"),
                EthernetType.Arp);

            var arp = new ArpPacket(
                ArpOperation.Request,
                PhysicalAddress.Parse("00-00-00-00-00-00"), // THA
                targetIp,                                   // TPA
                srcMac,                                     // SHA
                srcIp                                       // SPA
            );

            eth.PayloadPacket = arp;
            dev.SendPacket(eth);
        }

        // уже есть: private bool TryGetNicMac_FromSystem(string guidUpper, out PhysicalAddress mac)

        public static bool TryGetNicMacStatic(string guidUpper, out PhysicalAddress mac)
        {
            // можно просто продублировать ту же логику, что и в TryGetNicMac_FromSystem
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
        /// Включить/выключить Proxy ARP на интерфейсе (GUID / имя из UI).
        /// </summary>
        public void SetProxyArp(string interfaceId, bool enabled)
        {
            var id = interfaceId?.Trim('{', '}').ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(id)) return;

            _proxyArpEnabled[id] = enabled;
            Console.WriteLine($"[ProxyARP] {id} -> {(enabled ? "ENABLED" : "DISABLED")}");
        }

        /// <summary>
        /// Узнать, включён ли Proxy ARP на интерфейсе. По умолчанию = true.
        /// </summary>
        public bool IsProxyArpEnabled(string interfaceId)
        {
            var id = interfaceId?.Trim('{', '}').ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(id)) return true;

            return !_proxyArpEnabled.TryGetValue(id, out var enabled) || enabled;
        }

        /// <summary>
        /// Для UI: список интерфейсов и их состояния Proxy ARP.
        /// </summary>
        public IReadOnlyCollection<(string InterfaceId, bool Enabled)> GetProxyArpStates()
        {
            return _proxyArpEnabled
                .Select(kv => (kv.Key, kv.Value))
                .OrderBy(t => t.Key)
                .ToList()
                .AsReadOnly();
        }



    }
    
}
