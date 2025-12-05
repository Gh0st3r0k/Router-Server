#nullable enable
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using PacketDotNet;
using WANT.Service.Packets;       // для PacketBridgeWorker.TryGetNicMacStatic
using WANT.Service.Dhcp;          // твои модели
using PacketDotNet.Dhcp;


namespace WANT.Service.Dhcp
{
    /// <summary>
    /// Реализация DHCP-сервиса + фоновой воркер для чистки истёкших lease.
    /// Пока здесь нет самой DORA-логики (формирование OFFER/ACK),
    /// но есть:
    /// - хранение глобальных настроек,
    /// - хранение пулов,
    /// - manual reservations,
    /// - lease-таблица + таймеры.
    /// </summary>
    public sealed class DhcpService : BackgroundService, IDhcpService
    {
        private readonly object _lock = new();

        private DhcpGlobalSettings _settings = new();

        // Пулы по Id
        private readonly ConcurrentDictionary<string, DhcpPool> _pools =
            new(StringComparer.OrdinalIgnoreCase);

        // Лизы в памяти
        private readonly ConcurrentDictionary<string, DhcpLease> _leases =
            new(StringComparer.OrdinalIgnoreCase);

        public DhcpService()
        {
            // Можно задать какие-то дефолтные значения, если нужно.
        }

        // ───────────────── Глобальные настройки ─────────────────

        public DhcpGlobalSettings GetGlobalSettings()
        {
            lock (_lock)
            {
                // Возвращаем копию, чтобы снаружи не меняли напрямую
                return new DhcpGlobalSettings
                {
                    DefaultDynamicLeaseTime = _settings.DefaultDynamicLeaseTime,
                    CleanupInterval = _settings.CleanupInterval
                };
            }
        }

        public void UpdateGlobalSettings(DhcpGlobalSettings settings)
        {
            if (settings is null) return;

            lock (_lock)
            {
                _settings.DefaultDynamicLeaseTime = settings.DefaultDynamicLeaseTime;
                _settings.CleanupInterval = settings.CleanupInterval;
            }
        }

        // ───────────────── Пулы ─────────────────

        public IReadOnlyCollection<DhcpPool> GetPools()
        {
            return _pools.Values
                .Select(ClonePool)
                .OrderBy(p => p.Name)
                .ToArray();
        }

        public DhcpPool? GetPool(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (_pools.TryGetValue(id, out var pool))
                return ClonePool(pool);

            return null;
        }

        public DhcpPool CreatePool(DhcpPool pool)
        {
            if (pool is null) throw new ArgumentNullException(nameof(pool));

            // Если Id не задан – создаём
            if (string.IsNullOrWhiteSpace(pool.Id))
                pool.Id = Guid.NewGuid().ToString("D");

            var copy = ClonePool(pool);
            _pools[copy.Id] = copy;
            return ClonePool(copy);
        }

        public DhcpPool? UpdatePool(string id, DhcpPool pool)
        {
            if (string.IsNullOrWhiteSpace(id) || pool is null) return null;

            if (!_pools.ContainsKey(id))
                return null;

            // Гарантируем, что Id не меняется
            pool.Id = id;
            var copy = ClonePool(pool);
            _pools[id] = copy;
            return ClonePool(copy);
        }

        public bool DeletePool(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;

            var removed = _pools.TryRemove(id, out _);

            if (removed)
            {
                // Удаляем все лизы этого пула
                foreach (var kv in _leases.ToArray())
                {
                    if (string.Equals(kv.Value.PoolId, id, StringComparison.OrdinalIgnoreCase))
                        _leases.TryRemove(kv.Key, out _);
                }
            }

            return removed;
        }

        // ───────────────── Manual reservations ─────────────────

        public bool AddOrUpdateManualReservation(
            string poolId,
            PhysicalAddress mac,
            IPAddress ip)
        {
            if (string.IsNullOrWhiteSpace(poolId)) return false;
            if (mac == null || mac.Equals(PhysicalAddress.None)) return false;
            if (ip == null || ip.Equals(IPAddress.Any)) return false;

            if (!_pools.TryGetValue(poolId, out var pool))
                return false;

            var macStr = mac.ToString();

            lock (_lock)
            {
                pool.ManualReservations[macStr] = ip;
            }

            return true;
        }

        public bool RemoveManualReservation(string poolId, PhysicalAddress mac)
        {
            if (string.IsNullOrWhiteSpace(poolId)) return false;
            if (mac == null || mac.Equals(PhysicalAddress.None)) return false;

            if (!_pools.TryGetValue(poolId, out var pool))
                return false;

            var macStr = mac.ToString();

            lock (_lock)
            {
                return pool.ManualReservations.Remove(macStr);
            }
        }

        // ───────────────── Leases ─────────────────

        public IReadOnlyCollection<DhcpLease> GetLeases()
        {
            return _leases.Values
                .Select(CloneLease)
                .OrderBy(l => l.InterfaceId)
                .ThenBy(l => l.Address.ToString())
                .ToArray();
        }

        public IReadOnlyCollection<DhcpLease> GetLeasesByPool(string poolId)
        {
            if (string.IsNullOrWhiteSpace(poolId))
                return Array.Empty<DhcpLease>();

            return _leases.Values
                .Where(l => string.Equals(l.PoolId, poolId, StringComparison.OrdinalIgnoreCase))
                .Select(CloneLease)
                .OrderBy(l => l.Address.ToString())
                .ToArray();
        }

        // ───────────────── Обработка DHCP-пакетов ─────────────────

        /// <summary>
        /// Здесь в будущем будет полноценная реализация DORA:
        /// - разбор DHCP-пакета (Discover/Request/Release)
        /// - выбор пула по интерфейсу
        /// - выбор IP (manual/automatic/dynamic)
        /// - формирование OFFER/ACK/NAK и отправка через PacketBridge.
        /// 
        /// Сейчас метод оставлен "заглушкой", чтобы всё собиралось,
        /// а конфиг и контроллер уже можно было использовать.
        /// </summary>
        public EthernetPacket? HandleIncomingDhcpPacket(
            string ingressInterfaceId,
            EthernetPacket eth,
            IPv4Packet ip,
            UdpPacket udp)
        {
            try
            {
                // Пытаемся взять DHCP-полезную нагрузку из UdpPacket
                byte[]? raw = udp.PayloadData;

                // В твоей версии PacketDotNet payload может быть не в PayloadData, а только в udp.Bytes
                if (raw == null || raw.Length == 0)
                {
                    var udpBytes = udp.Bytes; // заголовок UDP (8 байт) + payload
                    if (udpBytes != null && udpBytes.Length > 8)
                    {
                        raw = new byte[udpBytes.Length - 8];
                        Buffer.BlockCopy(udpBytes, 8, raw, 0, raw.Length); // скипаем 8 байт заголовка UDP
                    }
                }

                if (raw == null || raw.Length < 240)
                {
                    Console.WriteLine($"[DHCP] invalid UDP payload: null or too short (len={raw?.Length ?? 0})");
                    return null;
                }

                var dhcp = new DhcpPacket(raw);
                var msgType = dhcp.GetMsgType();

                Console.WriteLine($"[DHCP] {msgType} from {eth.SourceHardwareAddress} on {ingressInterfaceId}");

                if (PacketBridgeWorker.TryGetNicMacStatic(ingressInterfaceId, out var nicMac))
                {
                    if (eth.SourceHardwareAddress.Equals(nicMac))
                    {
                        Console.WriteLine($"[DHCP] ignoring packet from own MAC on iface {ingressInterfaceId}");
                        return null;
                    }
                }
    

                // 1) Находим пул, связанный с этим интерфейсом
                var pool = _pools.Values.FirstOrDefault(p =>
                    p.Enabled &&
                    p.InterfaceId.Equals(ingressInterfaceId, StringComparison.OrdinalIgnoreCase));

                if (pool == null)
                {
                    Console.WriteLine("[DHCP] No pool found for interface, ignoring.");
                    return null;
                }

                // 2) MAC клиента
                var clientMac = eth.SourceHardwareAddress;
                var macStr = clientMac.ToString();

                // 3) Если есть manual reservation — взять её
                IPAddress? reservedIp = null;
                if (pool.ManualReservations.TryGetValue(macStr, out var resIp))
                    reservedIp = resIp;

                // 3.1. Ищем уже существующий lease для этого MAC
                var existingLease = _leases.Values.FirstOrDefault(
                    l => l.PoolId == pool.Id && l.MacAddress.Equals(clientMac));

                // 4-) Обработчик RELEASE → удаляем lease и не отвечаем
                if (msgType == DhcpMessageType.Release)
                {
                    Console.WriteLine("[DHCP] RELEASE received");

                    if (existingLease != null)
                    {
                        if (_leases.TryRemove(existingLease.Id, out _))
                        {
                            Console.WriteLine($"[DHCP] lease {existingLease.Address} for MAC {macStr} removed by RELEASE");
                        }
                        else
                        {
                            Console.WriteLine($"[DHCP] RELEASE: failed to remove lease {existingLease.Address} for MAC {macStr}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("[DHCP] RELEASE for unknown lease (ignored)");
                    }

                    // На RELEASE по стандарту не отвечаем
                    return null;
                }


                // 4) Обработчик DISCOVER → OFFER
                if (msgType == DhcpMessageType.Discover)
                {
                    Console.WriteLine("[DHCP] DISCOVER received");

                    IPAddress? offeredIp = null;

                    // ── Приоритет режимов: MANUAL → AUTOMATIC → DYNAMIC ──

                    // 1) MANUAL – только если включён флаг ManualEnabled и есть резервация
                    if (pool.ManualEnabled && reservedIp != null)
                    {
                        offeredIp = reservedIp;
                        Console.WriteLine($"[DHCP] using MANUAL reservation {offeredIp} for MAC {macStr}");
                    }
                    else
                    {
                        // 2) AUTOMATIC – если разрешён AutomaticEnabled
                        if (pool.AutomaticEnabled)
                        {
                            if (existingLease != null)
                            {
                                offeredIp = existingLease.Address;
                                Console.WriteLine($"[DHCP] using existing AUTOMATIC lease {offeredIp} for MAC {macStr}");
                            }
                            else
                            {
                                offeredIp = FindFreeDynamic(pool);
                                Console.WriteLine($"[DHCP] picked new AUTOMATIC IP {offeredIp} for MAC {macStr}");
                            }
                        }
                        // 3) DYNAMIC – если AutomaticDisabled, но DynamicEnabled = true
                        else if (pool.DynamicEnabled)
                        {
                            if (existingLease != null)
                            {
                                offeredIp = existingLease.Address;
                                Console.WriteLine($"[DHCP] using existing DYNAMIC lease {offeredIp} for MAC {macStr}");
                            }
                            else
                            {
                                offeredIp = FindFreeDynamic(pool);
                                Console.WriteLine($"[DHCP] picked new DYNAMIC IP {offeredIp} for MAC {macStr}");
                            }
                        }
                    }

                    if (offeredIp == null)
                    {
                        Console.WriteLine("[DHCP] DISCOVER: no allocation mode available (flags / reservations), ignoring.");
                        return null;
                    }

                    var reply = BuildDhcpReply(
                        DhcpMessageType.Offer,
                        eth,
                        ip,
                        udp,
                        pool,
                        offeredIp,
                        ingressInterfaceId,
                        dhcp);

                    Console.WriteLine($"[DHCP] OFFER built for {offeredIp}");
                    return reply;
                }


                // 5) Обработчик REQUEST → ACK
                if (msgType == DhcpMessageType.Request)
                {
                    Console.WriteLine("[DHCP] REQUEST received");

                    // 1) Пытаемся взять IP из option 50 (Requested IP Address)
                    var requestedIp = dhcp.GetRequestedIpAddress() ?? reservedIp;

                    // 2) Если опции 50 нет — пробуем CiAddr (renew по уже назначенному IP)
                    if (requestedIp == null || requestedIp.Equals(IPAddress.Any))
                    {
                        if (!dhcp.CiAddr.Equals(IPAddress.Any))
                        {
                            requestedIp = dhcp.CiAddr;
                            Console.WriteLine($"[DHCP] REQUEST (renew) via CiAddr = {requestedIp}");
                        }
                    }

                    // 3) Если есть manual-reservation для этого MAC, можно приоритетно использовать её
                    if (requestedIp == null && reservedIp != null)
                    {
                        requestedIp = reservedIp;
                        Console.WriteLine($"[DHCP] REQUEST: using manual reservation {requestedIp} for MAC {macStr}");
                    }

                    if (requestedIp == null || requestedIp.Equals(IPAddress.Any))
                    {
                        Console.WriteLine("[DHCP] REQUEST with no requested IP and no CiAddr, ignoring.");
                        return null;
                    }


                    // 3.1. Если это manual-резервация и включён MANUAL → разрешаем даже вне диапазона
                    bool isManualForThisMac =
                        pool.ManualEnabled &&
                        reservedIp != null &&
                        requestedIp.Equals(reservedIp);

                    if (!isManualForThisMac)
                    {
                        // Для automatic/dynamic – IP обязан быть в пуле
                        if (!IsIpInPool(pool, requestedIp))
                        {
                            Console.WriteLine($"[DHCP] REQUEST: IP {requestedIp} is not in pool range/network, ignoring.");
                            return null;
                        }
                    }

                    // 3.2. Проверка, что IP не занят другим MAC (живой lease)
                    var now = DateTimeOffset.UtcNow;
                    var foreignLease = _leases.Values.FirstOrDefault(l =>
                        l.PoolId == pool.Id &&
                        !l.MacAddress.Equals(clientMac) &&
                        l.Address.Equals(requestedIp) &&
                        (l.ExpiresAt == null || l.ExpiresAt > now));

                    if (foreignLease != null)
                    {
                        Console.WriteLine($"[DHCP] REQUEST: IP {requestedIp} already leased to {foreignLease.MacAddress}, ignoring.");
                        // здесь можно было бы слать NAK, но для проекта хватит ignore
                        return null;
                    }

                    // Создаём/обновляем lease
                    CreateOrUpdateLease(pool, clientMac, requestedIp, ingressInterfaceId);

                    var reply = BuildDhcpReply(
                        DhcpMessageType.Ack,
                        eth,
                        ip,
                        udp,
                        pool,
                        requestedIp,
                        ingressInterfaceId,
                        dhcp);

                    Console.WriteLine($"[DHCP] ACK built for {requestedIp}");
                    return reply;
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DHCP] ERR {ex}");
                return null;
            }
        }

        // ───────────────── BackgroundService: чистка истёкших lease ─────────────────

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[DHCP] background worker started");

            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan delay;
                lock (_lock)
                {
                    delay = _settings.CleanupInterval;
                    if (delay <= TimeSpan.Zero)
                        delay = TimeSpan.FromSeconds(5);
                }

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                CleanupExpiredLeases();
            }

            Console.WriteLine("[DHCP] background worker stopped");
        }

        private void CleanupExpiredLeases()
        {
            var now = DateTimeOffset.UtcNow;
            int removed = 0;

            foreach (var kv in _leases.ToArray())
            {
                var l = kv.Value;
                if (l.AllocationType != DhcpAllocationType.Dynamic) 
                    continue;

                if (l.ExpiresAt is null) 
                    continue;

                if (l.ExpiresAt.Value <= now)
                {
                    if (_leases.TryRemove(kv.Key, out _))
                        removed++;
                }
            }

            if (removed > 0)
                Console.WriteLine($"[DHCP] cleanup: removed {removed} expired leases");
        }

        // ───────────────── Helpers: клоны ─────────────────

        private static DhcpPool ClonePool(DhcpPool p)
        {
            return new DhcpPool
            {
                Id = p.Id,
                Name = p.Name,
                InterfaceId = p.InterfaceId,
                Network = p.Network,
                Mask = p.Mask,
                RangeStart = p.RangeStart,
                RangeEnd = p.RangeEnd,
                Gateway = p.Gateway,
                Enabled = p.Enabled,
                DynamicEnabled = p.DynamicEnabled,
                AutomaticEnabled = p.AutomaticEnabled,
                ManualEnabled = p.ManualEnabled,
                DynamicLeaseTimeOverride = p.DynamicLeaseTimeOverride,
                ManualReservations = new Dictionary<string, IPAddress>(p.ManualReservations, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static DhcpLease CloneLease(DhcpLease l)
        {
            return new DhcpLease
            {
                Id = l.Id,
                PoolId = l.PoolId,
                Address = l.Address,
                MacAddress = l.MacAddress,
                AllocationType = l.AllocationType,
                InterfaceId = l.InterfaceId,
                CreatedAt = l.CreatedAt,
                ExpiresAt = l.ExpiresAt
            };
        }

        // ───────────────── Публичный helper (пригодится позже, когда будешь делать DORA) ─────────────────

        /// <summary>
        /// Создать новый dynamic-lease (без реальной логики выбора IP).
        /// Этот метод можно будет использовать, когда начнёшь писать DORA.
        /// </summary>
        public DhcpLease CreateDynamicLeaseSkeleton(string poolId, IPAddress address, PhysicalAddress mac, string ifaceId)
        {
            if (string.IsNullOrWhiteSpace(poolId))
                throw new ArgumentNullException(nameof(poolId));

            if (!_pools.TryGetValue(poolId, out var pool))
                throw new InvalidOperationException($"Pool '{poolId}' not found");

            var leaseTime = pool.DynamicLeaseTimeOverride ?? _settings.DefaultDynamicLeaseTime;

            var lease = new DhcpLease
            {
                Id = Guid.NewGuid().ToString("D"),
                PoolId = poolId,
                Address = address,
                MacAddress = mac,
                AllocationType = DhcpAllocationType.Dynamic,
                InterfaceId = ifaceId,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.Add(leaseTime)
            };

            _leases[lease.Id] = lease;

            return CloneLease(lease);
        }

        private IPAddress? FindFreeDynamic(DhcpPool pool)
        {
            var now = DateTimeOffset.UtcNow;

            // Занятые адреса — только у тех, чей lease ещё актуален
            var used = _leases.Values
                .Where(l => l.PoolId == pool.Id &&
                            (l.ExpiresAt == null || l.ExpiresAt > now))
                .Select(l => l.Address)
                .ToHashSet();

            // + все IP из manual reservations тоже считаем занятыми
            foreach (var resIp in pool.ManualReservations.Values)
                used.Add(resIp);

            uint start = BitConverter.ToUInt32(pool.RangeStart.GetAddressBytes().Reverse().ToArray());
            uint end   = BitConverter.ToUInt32(pool.RangeEnd.GetAddressBytes().Reverse().ToArray());

            for (uint i = start; i <= end; i++)
            {
                var ip = new IPAddress(BitConverter.GetBytes(i).Reverse().ToArray());
                if (!used.Contains(ip))
                    return ip;
            }

            return null;
        }



        private void CreateOrUpdateLease(
            DhcpPool pool,
            PhysicalAddress mac,
            IPAddress ip,
            string iface)
        {
            var macKey = mac.ToString();

            var existing = _leases.Values.FirstOrDefault(
                l => l.PoolId == pool.Id && l.MacAddress.Equals(mac));

            // --- Определяем режим выделения с приоритетом MANUAL → AUTOMATIC → DYNAMIC

            DhcpAllocationType mode;
            DateTimeOffset? expires = null;

            // 1) MANUAL – только если включён флаг и есть резервация для этого MAC
            if (pool.ManualEnabled && pool.ManualReservations.ContainsKey(macKey))
            {
                mode = DhcpAllocationType.Manual;
                expires = null; // перманентно
            }
            // 2) AUTOMATIC – если разрешён automatic
            else if (pool.AutomaticEnabled)
            {
                mode = DhcpAllocationType.Automatic;
                expires = null; // тоже перманентно
            }
            // 3) DYNAMIC – если разрешён dynamic
            else if (pool.DynamicEnabled)
            {
                mode = DhcpAllocationType.Dynamic;
                var leaseTime = pool.DynamicLeaseTimeOverride ?? _settings.DefaultDynamicLeaseTime;
                expires = DateTimeOffset.UtcNow.Add(leaseTime);
            }
            else
            {
                Console.WriteLine($"[DHCP] no allocation mode enabled in pool {pool.Id} for MAC {macKey}, lease not created.");
                return;
            }

            if (existing != null)
            {
                existing.Address = ip;
                existing.InterfaceId = iface;
                existing.CreatedAt = DateTimeOffset.UtcNow;
                existing.AllocationType = mode;
                existing.ExpiresAt = expires;
                return;
            }

            var lease = new DhcpLease
            {
                Id = Guid.NewGuid().ToString(),
                PoolId = pool.Id,
                Address = ip,
                MacAddress = mac,
                InterfaceId = iface,
                AllocationType = mode,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = expires
            };

            _leases[lease.Id] = lease;
        }




        private EthernetPacket BuildDhcpReply(
            DhcpMessageType type,
            EthernetPacket eth,
            IPv4Packet ip,
            UdpPacket udp,
            DhcpPool pool,
            IPAddress offeredIp,
            string iface,
            DhcpPacket dhcpReq)   // ← важный параметр
        {
            // MAC сервера (наш MAC на этом интерфейсе)
            PacketBridgeWorker.TryGetNicMacStatic(iface, out var serverMac);
            var serverIp = pool.Gateway;


            bool isRenewAck =
                type == DhcpMessageType.Ack &&
                !dhcpReq.CiAddr.Equals(IPAddress.Any);
            
            // Адрес назначения по умолчанию — broadcast
            var dstIp  = (IPAddress)IPAddress.Broadcast;
            var dstMac = PacketBridgeWorker.BroadcastMac;

            if (isRenewAck)
            {
                // Клиент уже живёт с этим IP → шлём unicast
                dstIp  = offeredIp;                 // это тот же адрес, что и CiAddr
                dstMac = eth.SourceHardwareAddress; // MAC клиента
                Console.WriteLine($"[DHCP] using unicast ACK to {dstIp} ({dstMac})");
            }
            else
            {
                Console.WriteLine("[DHCP] using broadcast for reply");
            }

            // Сборка DHCP-ответа
            var dhcp = new DhcpPacket();
            dhcp.Op = 2;      // BOOTREPLY
            dhcp.Htype = 1;   // Ethernet
            dhcp.Hlen = 6;
            dhcp.Xid = dhcpReq.Xid; // тот же XID, что и у клиента
            dhcp.ChAddr = eth.SourceHardwareAddress.GetAddressBytes();
            dhcp.YiAddr = offeredIp;

            dhcp.SetMsgType(type);
            dhcp.SetOptionSubnetMask(pool.Mask);
            dhcp.SetOptionRouter(pool.Gateway);
            dhcp.SetOptionLeaseTime(
                (uint)((pool.DynamicLeaseTimeOverride ?? _settings.DefaultDynamicLeaseTime).TotalSeconds));
            dhcp.SetOptionServerIdentifier(serverIp);

            // ---------- UDP ----------
            var udpOut = new UdpPacket(67, 68)
            {
                PayloadData = dhcp.Bytes,
                Checksum = 0
            };
            udpOut.UpdateCalculatedValues();   // длина

            // ---------- IP ----------
            var ipOut = new IPv4Packet(serverIp, dstIp)
            {
                TimeToLive = 64,
                Protocol = ProtocolType.Udp
            };
            ipOut.PayloadData = udpOut.Bytes;
            ipOut.UpdateCalculatedValues();
            ipOut.UpdateIPChecksum();

            // ---------- UDP checksum (ручной расчёт) ----------
            var checksum = ComputeUdpChecksum(ipOut, udpOut);
            udpOut.Checksum = checksum;

            ipOut.PayloadData = udpOut.Bytes;
            ipOut.UpdateIPChecksum();

            // ---------- Ethernet ----------
            var ethOut = new EthernetPacket(
                serverMac,
                dstMac,
                EthernetType.IPv4);

            ethOut.PayloadData = ipOut.Bytes;
            ethOut.UpdateCalculatedValues();

            Console.WriteLine($"[DHCP] Sending {type} for {offeredIp} on {iface}");

            return ethOut;
        }




        private static ushort ComputeUdpChecksum(IPv4Packet ip, UdpPacket udp)
        {
            var src = ip.SourceAddress.GetAddressBytes();
            var dst = ip.DestinationAddress.GetAddressBytes();
            var udpBytes = udp.Bytes;
            int udpLen = udpBytes.Length;

            var buf = new List<byte>(12 + udpLen + 1);

            buf.AddRange(src);
            buf.AddRange(dst);
            buf.Add(0);
            buf.Add(17); // UDP
            buf.Add((byte)(udpLen >> 8));
            buf.Add((byte)(udpLen & 0xFF));

            buf.AddRange(udpBytes);

            if (buf.Count % 2 == 1)
                buf.Add(0);

            uint sum = 0;
            for (int i = 0; i < buf.Count; i += 2)
            {
                ushort word = (ushort)((buf[i] << 8) | buf[i + 1]);
                sum += word;
                while ((sum >> 16) != 0)
                    sum = (sum & 0xFFFF) + (sum >> 16);
            }

            ushort result = (ushort)~sum;
            if (result == 0)
                result = 0xFFFF;
            return result;
        }





        private static bool IsIpInPool(DhcpPool pool, IPAddress ip)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return false;

            var ipInt    = BitConverter.ToUInt32(ip.GetAddressBytes().Reverse().ToArray());
            var startInt = BitConverter.ToUInt32(pool.RangeStart.GetAddressBytes().Reverse().ToArray());
            var endInt   = BitConverter.ToUInt32(pool.RangeEnd.GetAddressBytes().Reverse().ToArray());

            // проверка диапазона
            if (ipInt < startInt || ipInt > endInt)
                return false;

            // проверка, что IP действительно в сети pool.Network/Mask
            var maskInt = BitConverter.ToUInt32(pool.Mask.GetAddressBytes().Reverse().ToArray());
            var netInt  = BitConverter.ToUInt32(pool.Network.GetAddressBytes().Reverse().ToArray());

            return (ipInt & maskInt) == (netInt & maskInt);
        }




    }
}
