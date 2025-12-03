// WANT.Service/Rip/RipService.cs
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using PacketDotNet;
using SharpPcap;
using WANT.Service.Interface;
using WANT.Service.Packets;
using WANT.Service.Routing;
using System.Net.NetworkInformation;

namespace WANT.Service.Rip
{
    /// <summary>
    /// Реализация протокола RIPv2:
    /// - таймеры (update / invalid / holddown / flush),
    /// - split horizon,
    /// - добавление маршрутов в общую RoutingTable с AD = 120,
    /// - возможность включать/выключать RIP на интерфейсе.
    /// </summary>
    public sealed class RipService : BackgroundService, IRipService
    {
        private readonly IRoutingTable _routingTable;
        private readonly InterfaceScanner _interfaces;
        private PacketBridgeWorker? _packetBridge;

        public void SetPacketBridge(PacketBridgeWorker worker)
        {
            _packetBridge = worker;
        }


        private readonly object _lock = new();

        private RipTimers _timers = new RipTimers();

        // Состояние RIP по интерфейсам
        private readonly ConcurrentDictionary<string, RipInterfaceState> _ifStates =
            new(StringComparer.OrdinalIgnoreCase);

        // Таблица выученных RIP-маршрутов (ключ: dest/prefix)
        private readonly ConcurrentDictionary<(string net, int prefix), RipLearnedRoute> _ripRoutes =
            new();

        // Время следующего update по интерфейсу
        private readonly ConcurrentDictionary<string, DateTime> _nextUpdateTime =
            new(StringComparer.OrdinalIgnoreCase);

        public RipService(
            IRoutingTable routingTable,
            InterfaceScanner interfaces)
        {
            _routingTable = routingTable;
            _interfaces = interfaces;

            // Инициализируем состояние интерфейсов
            foreach (var ni in _interfaces.GetAll())
            {
                if (ni.Type != NicType.Ethernet) continue;
                var id = ni.Guid?.Trim('{', '}').ToUpperInvariant() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id)) continue;

                _ifStates[id] = new RipInterfaceState
                {
                    InterfaceId = id,
                    Enabled = false
                };
            }
        }

        // ==================== IRipService ====================

        public RipTimers GetTimers()
        {
            lock (_lock)
            {
                return new RipTimers
                {
                    UpdateInterval = _timers.UpdateInterval,
                    InvalidInterval = _timers.InvalidInterval,
                    HolddownInterval = _timers.HolddownInterval,
                    FlushInterval = _timers.FlushInterval
                };
            }
        }

        public void UpdateTimers(RipTimers timers)
        {
            if (timers is null) throw new ArgumentNullException(nameof(timers));
            lock (_lock)
            {
                _timers = timers;
            }
        }

        public IReadOnlyCollection<RipInterfaceState> GetInterfaces()
            => _ifStates.Values.OrderBy(x => x.InterfaceId).ToList();

        public void EnableOnInterface(string interfaceId)
        {
            var id = NormalizeGuid(interfaceId);
            if (string.IsNullOrWhiteSpace(id)) return;

            var state = _ifStates.GetOrAdd(id, k => new RipInterfaceState
            {
                InterfaceId = k,
                Enabled = true
            });
            state.Enabled = true;

            _nextUpdateTime[id] = DateTime.UtcNow; // сразу отправим первый update
        }

        public void DisableOnInterface(string interfaceId)
        {
            var id = NormalizeGuid(interfaceId);
            if (string.IsNullOrWhiteSpace(id)) return;

            if (_ifStates.TryGetValue(id, out var st))
                st.Enabled = false;
        }

        public void HandleIncomingRipPacket(string ingressInterfaceId, IPv4Packet ip, UdpPacket udp)
        {
            var id = NormalizeGuid(ingressInterfaceId);
            if (!_ifStates.TryGetValue(id, out var st) || !st.Enabled)
                return;

            // ❗ Если источник – наш же IP на этом интерфейсе, значит это наш собственный RIP update → игнор
            if (TryGetInterfaceIp(id, out var localIp) && localIp.Equals(ip.SourceAddress))
            {
                // Console.WriteLine($"[RIP] Ignore self-originated RIP on {id} from {localIp}");
                return;
            }

            var payload = udp.PayloadData;
            if (payload == null || payload.Length < 4) return;

            byte command = payload[0];
            byte version = payload[1];

            if (version != 2) return; // поддерживаем только v2

            if (command != 2 && command != 1)
                return; // только request/response

            if (command == 1)
            {
                // Простая реализация: на любой request отвечаем normal response
                _nextUpdateTime[id] = DateTime.UtcNow; // форсим отправку
                return;
            }

            // ==== RESPONSE ====
            ParseRipResponse(id, ip.SourceAddress, payload);
        }

        // ==================== BackgroundService ====================

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    TickTimers();
                    await Task.Delay(1000, stoppingToken);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RIP] Timer loop error: {ex}");
                }
            }
        }

        // ==================== Таймеры и отправка ====================

        private void TickTimers()
        {
            var now = DateTime.UtcNow;

            foreach (var kv in _ripRoutes.ToArray())
            {
                var key = kv.Key;
                var route = kv.Value;

                var age = now - route.LastUpdateUtc;

                RipTimers timers;
                lock (_lock) timers = _timers;

                // 1) FLUSH – полностью удалить маршрут
                if (age >= timers.FlushInterval)
                {
                    if (route.InstalledRoute != null &&
                        route.InstalledRoute.AdministrativeDistance == 120)
                    {
                        _routingTable.RemoveRoute(
                            route.InstalledRoute.Destination,
                            route.InstalledRoute.PrefixLength,
                            route.InstalledRoute.OutInterfaceName);
                    }

                    _ripRoutes.TryRemove(key, out _);
                    continue;
                }

                // 2) HOLDDOWN timeout – снимаем holddown, но маршрут
                //    останется "отключённым", пока не придёт новый update
                if (route.InHolddown && route.HolddownStartUtc.HasValue)
                {
                    var hdAge = now - route.HolddownStartUtc.Value;
                    if (hdAge >= timers.HolddownInterval)
                    {
                        route.InHolddown = false;
                        route.HolddownStartUtc = null;
                        // InstalledRoute уже null после invalid;
                        // новый валидный update заново поставит маршрут.
                    }
                }

                // 3) INVALID – маршрут становится недействительным,
                //    уходит в holddown и удаляется из общей routing table
                if (!route.InHolddown &&
                    route.InstalledRoute != null &&
                    age >= timers.InvalidInterval)
                {
                    route.InHolddown = true;
                    route.HolddownStartUtc = now;
                    route.Metric = 16;

                    if (route.InstalledRoute.AdministrativeDistance == 120)
                    {
                        _routingTable.RemoveRoute(
                            route.InstalledRoute.Destination,
                            route.InstalledRoute.PrefixLength,
                            route.InstalledRoute.OutInterfaceName);
                    }

                    route.InstalledRoute = null;
                }
            }

            // ====== UPDATE таймеры как у тебя было ======
            var now2 = DateTime.UtcNow;

            foreach (var st in _ifStates.Values)
            {
                if (!st.Enabled) continue;

                var id = st.InterfaceId;
                var nextTime = _nextUpdateTime.GetOrAdd(id, _ => now2);

                if (now2 >= nextTime)
                {
                    SendRipUpdateOnInterface(id);
                    RipTimers timers;
                    lock (_lock) timers = _timers;
                    _nextUpdateTime[id] = now2 + timers.UpdateInterval;
                }
            }
        }


        // ==================== Обработка входящих RIP ====================

        private void ParseRipResponse(string ingressId, IPAddress neighborIp, byte[] payload)
        {
            // Header: 4 bytes
            const int HEADER_LEN = 4;
            int offset = HEADER_LEN;

            while (offset + 20 <= payload.Length)
            {
                ushort afi = ReadUInt16(payload, offset);
                ushort routeTag = ReadUInt16(payload, offset + 2);
                var ip = ReadIp(payload, offset + 4);
                var mask = ReadIp(payload, offset + 8);
                var nextHop = ReadIp(payload, offset + 12);
                uint metricRaw = ReadUInt32(payload, offset + 16);

                offset += 20;

                if (afi != 2) continue;        // AFI=2 -> IP
                if (metricRaw < 1 || metricRaw > 16) continue;

                int metric = (int)metricRaw;

                if (ip.Equals(IPAddress.Any) || mask.Equals(IPAddress.Any))
                    continue;

                int prefixLen = MaskToPrefix(mask);
                if (prefixLen < 0) continue;

                // Сеть = ip & mask
                var network = ApplyMask(ip, mask);

                if (IsOurConnectedNetwork(network, prefixLen))
                    continue;

                var key = (network.ToString(), prefixLen);

                var ingressNorm = NormalizeGuid(ingressId);
                // === Split Horizon ===
                // Если мы уже знаем, что маршрут был выучен с этого же интерфейса,
                // просто обновляем таймеры, но не меняем направление.
                var entry = _ripRoutes.GetOrAdd(key, _ => new RipLearnedRoute
                {
                    Destination = network,
                    PrefixLength = prefixLen,
                    LearnedOnInterfaceId = ingressId
                });

                // Если маршрут уже в holddown и пришла "лучше метрика" – игнорируем
                if (entry.InHolddown && metric < 16)
                    continue;

                entry.LastUpdateUtc = DateTime.UtcNow;
                entry.LearnedOnInterfaceId = ingressId;
                entry.NextHop = neighborIp; // для простоты – nextHop = сам сосед
                entry.Metric = metric;
                entry.InHolddown = false;

                // Добавляем/обновляем запись в общей routing table, если метрика < 16 (reachable).
                if (metric < 16)
                    InstallOrUpdateRoute(entry);
                else
                {
                    // unreachable – удаляем из routing table, но оставляем в RIP таблице
                    if (entry.InstalledRoute != null)
                    {
                        if (entry.InstalledRoute.AdministrativeDistance == 120)
                        {
                            _routingTable.RemoveRoute(
                                entry.InstalledRoute.Destination,
                                entry.InstalledRoute.PrefixLength,
                                entry.InstalledRoute.OutInterfaceName);
                        }

                        entry.InstalledRoute = null;
                    }
                }
            }
        }

        private bool IsOurConnectedNetwork(IPAddress network, int prefixLength)
        {
            foreach (var ni in _interfaces.GetAll())
            {
                if (ni.Type != NicType.Ethernet) continue;

                if (!IPAddress.TryParse(ni.Ip?.ToString(), out var ip) ||
                    !IPAddress.TryParse(ni.Mask?.ToString(), out var mask))
                    continue;

                var localNet = ApplyMask(ip, mask);
                int localPref = MaskToPrefix(mask);
                if (localPref != prefixLength) continue;

                if (localNet.Equals(network))
                    return true;
            }

            return false;
        }


        private void InstallOrUpdateRoute(RipLearnedRoute ripRoute)
        {
            // AD = 120 для RIP
            const int RipAdministrativeDistance = 120;

            // Ставим OutInterfaceName = тот интерфейс, где висит сосед (LearnedOnInterfaceId)
            var route = new RouteEntry
            {
                Destination = ripRoute.Destination,
                PrefixLength = ripRoute.PrefixLength,
                NextHop = ripRoute.NextHop, // IP соседа
                OutInterfaceName = ripRoute.LearnedOnInterfaceId,
                AdministrativeDistance = RipAdministrativeDistance
            };

            _routingTable.AddRoute(route);
            ripRoute.InstalledRoute = route;
        }

        // ==================== Отправка RIP Updates ====================

        private void SendRipUpdateOnInterface(string interfaceIdRaw)
        {
            var interfaceId = NormalizeGuid(interfaceIdRaw);
            if (string.IsNullOrWhiteSpace(interfaceId))
                return;

            if (_packetBridge == null)
                return;

            if (!_packetBridge.OpenedDevices.TryGetValue(interfaceId, out var dev))
                return;

            if (dev is not IInjectionDevice inj)
                return;

            // IP нашего интерфейса
            if (!TryGetInterfaceIp(interfaceId, out var srcIp))
                return;

            // MAC нашего интерфейса
            if (!TryGetInterfaceMac(interfaceId, out var srcMac))
                return;

            // Мультикаст-адрес RIPv2
            var dstIp = IPAddress.Parse("224.0.0.9");

            // Собираем список маршрутов, которые будем анонсировать
            var entries = BuildRipEntriesForInterface(interfaceId);
            if (entries.Count == 0) return;

            var ripBytes = BuildRipResponsePayload(entries);

            // ---------- UDP ----------
            var udp = new UdpPacket(520, 520)
            {
                PayloadData = ripBytes
            };

            // ---------- IP ----------
            var ip = new IPv4Packet(srcIp, dstIp)
            {
                Protocol = PacketDotNet.ProtocolType.Udp,
                PayloadPacket = udp,
                TimeToLive = 1          // для мультикаста control traffic по RFC лучше 1
            };

            // Связать ip <-> udp (чтобы длины/поля более-менее проставились)
            udp.ParentPacket = ip;

            // НЕ рассчитываем ничего через UpdateCalculatedValues – чексумы посчитаем сами
            // ip.UpdateCalculatedValues();
            // udp.UpdateCalculatedValues();

            // ---------- Ethernet ----------
            var dstMac = new PhysicalAddress(new byte[]
            {
                0x01, 0x00, 0x5E, 0x00, 0x00, 0x09
            });

            var eth = new EthernetPacket(
                srcMac,
                dstMac,
                EthernetType.IPv4)
            {
                PayloadPacket = ip
            };

            // ---------- ЧИНИМ длины и checksum НА СЫРЫХ БАЙТАХ ----------
            var bytes = eth.Bytes;

            // Ethernet: 14 байт, IP начинается с 14
            int ipOffset = 14;

            // Длина IP-заголовка: нижние 4 бита первого байта * 4
            int ipHeaderLength = (bytes[ipOffset] & 0x0F) * 4;

            // Считаем длины
            ushort udpLength = (ushort)(8 + ripBytes.Length);
            ushort totalIpLength = (ushort)(ipHeaderLength + udpLength);

            // total length IP (big endian)
            bytes[ipOffset + 2] = (byte)(totalIpLength >> 8);
            bytes[ipOffset + 3] = (byte)(totalIpLength & 0xFF);

            // TTL и Protocol на всякий случай продублируем
            bytes[ipOffset + 8] = 1;        // TTL = 1
            bytes[ipOffset + 9] = 17;       // UDP

            // IP checksum = 0 перед расчётом
            bytes[ipOffset + 10] = 0x00;
            bytes[ipOffset + 11] = 0x00;

            // UDP offset сразу после IP
            int udpOffset = ipOffset + ipHeaderLength;

            // UDP length (big endian)
            bytes[udpOffset + 4] = (byte)(udpLength >> 8);
            bytes[udpOffset + 5] = (byte)(udpLength & 0xFF);

            // UDP checksum = 0 перед расчётом
            bytes[udpOffset + 6] = 0x00;
            bytes[udpOffset + 7] = 0x00;

            // Считаем IP header checksum
            ushort ipChecksum = ComputeIpHeaderChecksum(bytes, ipOffset, ipHeaderLength);
            bytes[ipOffset + 10] = (byte)(ipChecksum >> 8);
            bytes[ipOffset + 11] = (byte)(ipChecksum & 0xFF);

            // Считаем UDP checksum с pseudo-header
            ushort udpChecksum = ComputeUdpChecksum(bytes, ipOffset, udpOffset, udpLength);
            // В IPv4 UDP checksum МОЖЕТ быть 0, но мы считаем честно
            bytes[udpOffset + 6] = (byte)(udpChecksum >> 8);
            bytes[udpOffset + 7] = (byte)(udpChecksum & 0xFF);

            Console.WriteLine($"[RIP] Sent update on {interfaceId} ({entries.Count} routes, IPck=0x{ipChecksum:X4}, UDPck=0x{udpChecksum:X4})");

            // Отправляем уже подготовленный массив байт
            inj.SendPacket(bytes);
        }


        // ==================== Помощники ====================

        private static ushort ComputeIpHeaderChecksum(byte[] buffer, int offset, int headerLength)
        {
            uint sum = 0;

            for (int i = 0; i < headerLength; i += 2)
            {
                ushort word = (ushort)(buffer[offset + i] << 8);
                if (i + 1 < headerLength)
                    word |= buffer[offset + i + 1];

                sum += word;
                // складываем переносы
                if ((sum & 0xFFFF0000) != 0)
                    sum = (sum & 0xFFFF) + (sum >> 16);
            }

            // финальный перенос
            while ((sum >> 16) != 0)
                sum = (sum & 0xFFFF) + (sum >> 16);

            ushort result = (ushort)~sum;
            if (result == 0) result = 0xFFFF; // по стандарту 0 обычно заменяют на 0xFFFF
            return result;
        }

        private static ushort ComputeUdpChecksum(byte[] buffer, int ipOffset, int udpOffset, int udpLength)
        {
            uint sum = 0;

            // ----- PSEUDO-HEADER -----
            // Source IP
            sum += (ushort)((buffer[ipOffset + 12] << 8) | buffer[ipOffset + 13]);
            sum += (ushort)((buffer[ipOffset + 14] << 8) | buffer[ipOffset + 15]);

            // Destination IP
            sum += (ushort)((buffer[ipOffset + 16] << 8) | buffer[ipOffset + 17]);
            sum += (ushort)((buffer[ipOffset + 18] << 8) | buffer[ipOffset + 19]);

            // zero + protocol (UDP = 17)
            sum += (ushort)17; // 0x0011

            // UDP length (16 бит)
            sum += (ushort)udpLength;

            // ----- UDP HEADER + DATA -----
            for (int i = 0; i < udpLength; i += 2)
            {
                ushort word = (ushort)(buffer[udpOffset + i] << 8);
                if (i + 1 < udpLength)
                    word |= buffer[udpOffset + i + 1];

                sum += word;
                if ((sum & 0xFFFF0000) != 0)
                    sum = (sum & 0xFFFF) + (sum >> 16);
            }

            // финальный перенос
            while ((sum >> 16) != 0)
                sum = (sum & 0xFFFF) + (sum >> 16);

            ushort result = (ushort)~sum;
            if (result == 0) result = 0xFFFF;
            return result;
        }







        


        private List<(IPAddress net, IPAddress mask, int metric, string learnedOnIf)>
            BuildRipEntriesForInterface(string interfaceIdRaw)
        {
            // Нормализуем GUID интерфейса, на который отправляем update
            var interfaceId = NormalizeGuid(interfaceIdRaw);

            var list = new List<(IPAddress, IPAddress, int, string)>();

            // 1) АНОНС ПРЯМОПОДКЛЮЧЁННЫХ СЕТЕЙ
            //    - отправляем все connected-сети
            //    - КРОМЕ сети интерфейса, на который сейчас шлём RIP (X)
            foreach (var ni in _interfaces.GetAll())
            {
                var niId = NormalizeGuid(ni.Guid ?? string.Empty);
                if (string.IsNullOrWhiteSpace(niId))
                    continue;

                // ❌ Не шлём сеть самого интерфейса X назад на X
                if (string.Equals(niId, interfaceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!IPAddress.TryParse(ni.Ip?.ToString(), out var ip) ||
                    !IPAddress.TryParse(ni.Mask?.ToString(), out var mask))
                    continue;

                var net = ApplyMask(ip, mask);
                int prefix = MaskToPrefix(mask);
                if (prefix < 0) continue;

                // Метрика 1 — для соседа
                list.Add((net, mask, 1, niId));
            }

            // 2) АНОНС ВЫУЧЕННЫХ RIP-МАРШРУТОВ
            //    Split Horizon:
            //    - НЕ отправляем маршрут назад на тот интерфейс,
            //      через который он был выучен (LearnedOnInterfaceId == X)
            foreach (var kv in _ripRoutes)
            {
                var r = kv.Value;

                // ❌ split horizon: не шлём обратно на интерфейс, через который выучили
                if (string.Equals(
                        NormalizeGuid(r.LearnedOnInterfaceId),
                        interfaceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Недостижимые маршруты не шлём
                if (r.Metric >= 16 && !r.InHolddown) continue;

                var mask = PrefixToMask(r.PrefixLength);
                if (mask == null) continue;

                int metricOut;
                if (r.InHolddown)
                {
                    // в hold-down всегда шлём 16
                    metricOut = 16;
                }
                else
                {
                    metricOut = Math.Min(16, r.Metric + 1);
                }

                // Здесь learnedOnIf — откуда мы его когда-то выучили, это ок
                list.Add((r.Destination, mask, metricOut, r.LearnedOnInterfaceId));
            }

            return list;
        }



        private static byte[] BuildRipResponsePayload(List<(IPAddress net, IPAddress mask, int metric, string learnedOnIf)> entries)
        {
            // Header 4 bytes + 20 bytes per entry
            var bytes = new byte[4 + entries.Count * 20];

            bytes[0] = 2; // command = response
            bytes[1] = 2; // version = 2
            bytes[2] = 0;
            bytes[3] = 0;

            int offset = 4;
            foreach (var e in entries)
            {
                // AFI = 2
                bytes[offset + 0] = 0;
                bytes[offset + 1] = 2;

                // Route tag = 0
                bytes[offset + 2] = 0;
                bytes[offset + 3] = 0;

                // IP
                WriteIp(bytes, offset + 4, e.net);

                // Mask
                WriteIp(bytes, offset + 8, e.mask);

                // Next-hop = 0.0.0.0 (пусть соседи считают, что next-hop = sender)
                WriteIp(bytes, offset + 12, IPAddress.Any);

                // Metric
                var m = (uint)e.metric;
                bytes[offset + 16] = (byte)((m >> 24) & 0xFF);
                bytes[offset + 17] = (byte)((m >> 16) & 0xFF);
                bytes[offset + 18] = (byte)((m >> 8) & 0xFF);
                bytes[offset + 19] = (byte)(m & 0xFF);

                offset += 20;
            }

            return bytes;
        }

        // ==================== Помощники ====================

        private static string NormalizeGuid(string guid)
            => guid?.Trim('{', '}').ToUpperInvariant() ?? string.Empty;

        private static ushort ReadUInt16(byte[] buf, int offset)
            => (ushort)((buf[offset] << 8) | buf[offset + 1]);

        private static uint ReadUInt32(byte[] buf, int offset)
            => (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);

        private static IPAddress ReadIp(byte[] buf, int offset)
            => new IPAddress(new byte[]
            {
                buf[offset],
                buf[offset + 1],
                buf[offset + 2],
                buf[offset + 3]
            });

        private static void WriteIp(byte[] buf, int offset, IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            buf[offset] = b[0];
            buf[offset + 1] = b[1];
            buf[offset + 2] = b[2];
            buf[offset + 3] = b[3];
        }

        private static int MaskToPrefix(IPAddress mask)
        {
            var b = mask.GetAddressBytes();
            int prefix = 0;
            bool zeroMet = false;
            foreach (var x in b)
            {
                for (int i = 7; i >= 0; i--)
                {
                    bool bit = (x & (1 << i)) != 0;
                    if (bit)
                    {
                        if (zeroMet) return -1; // non-contiguous mask
                        prefix++;
                    }
                    else
                    {
                        zeroMet = true;
                    }
                }
            }
            return prefix;
        }

        private static IPAddress ApplyMask(IPAddress ip, IPAddress mask)
        {
            var ipBytes = ip.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            var netBytes = new byte[ipBytes.Length];

            for (int i = 0; i < ipBytes.Length; i++)
                netBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);

            return new IPAddress(netBytes);
        }

        private static IPAddress? PrefixToMask(int prefix)
        {
            if (prefix < 0 || prefix > 32) return null;
            uint v = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
            var b = new byte[]
            {
                (byte)((v >> 24) & 0xFF),
                (byte)((v >> 16) & 0xFF),
                (byte)((v >> 8) & 0xFF),
                (byte)(v & 0xFF)
            };
            return new IPAddress(b);
        }

        private bool TryGetInterfaceIp(string guidUpper, out IPAddress ip)
        {
            ip = IPAddress.Any;
            var ni = _interfaces.GetAll()
                .FirstOrDefault(i =>
                    string.Equals(
                        i.Guid?.Trim('{', '}').ToUpperInvariant(),
                        guidUpper,
                        StringComparison.OrdinalIgnoreCase));

            if (ni == null) return false;
            return IPAddress.TryParse(ni.Ip?.ToString(), out ip);
        }

        private bool TryGetInterfaceMac(string guidUpper, out PhysicalAddress mac)
        {
            return PacketBridgeWorker.TryGetNicMacStatic(guidUpper, out mac);
        }
    }
}
