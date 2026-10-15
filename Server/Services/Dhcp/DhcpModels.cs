#nullable enable
using System.Net.NetworkInformation;
using System.Net;


namespace WANT.Service.Dhcp
{
    /// <summary>
    /// Тип выделения адреса (как в задании).
    /// </summary>
    public enum DhcpAllocationType
    {
        Manual = 0,      // ручная привязка MAC -> IP (reservation)
        Automatic = 1,   // автоматическая постоянная привязка
        Dynamic = 2      // временная аренда с lease timer
    }

    /// <summary>
    /// Глобальные настройки DHCP-сервера.
    /// </summary>
    public sealed class DhcpGlobalSettings
    {
        /// <summary>
        /// Стандартный lease time для dynamic-аренд.
        /// </summary>
        public TimeSpan DefaultDynamicLeaseTime { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Период, с которым фоновый воркер чистит истекшие lease.
        /// </summary>
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Конфиг одного DHCP-пула (scope).
    /// Привязывается к конкретному интерфейсу роутера.
    /// </summary>
    public sealed class DhcpPool
    {
        /// <summary>
        /// Внутренний идентификатор пула (GUID/строка).
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("D");

        /// <summary>
        /// Человеческое имя (для UI).
        /// </summary>
        public string Name { get; set; } = "default";

        /// <summary>
        /// Interface GUID, uppercase without braces, as returned by discovery.
        /// </summary>
        public string InterfaceId { get; set; } = string.Empty;

        /// <summary>
        /// Сеть пула (network address).
        /// </summary>
        public IPAddress Network { get; set; } = IPAddress.Parse("10.0.0.0");

        /// <summary>
        /// Маска сети.
        /// </summary>
        public IPAddress Mask { get; set; } = IPAddress.Parse("255.255.255.0");

        /// <summary>
        /// Диапазон выдаваемых адресов: начало.
        /// </summary>
        public IPAddress RangeStart { get; set; } = IPAddress.Parse("10.0.0.50");

        /// <summary>
        /// Диапазон выдаваемых адресов: конец.
        /// </summary>
        public IPAddress RangeEnd { get; set; } = IPAddress.Parse("10.0.0.200");

        /// <summary>
        /// Default gateway, который мы отдаем в опции Router.
        /// Обычно это IP нашего интерфейса в этой сети.
        /// </summary>
        public IPAddress Gateway { get; set; } = IPAddress.Parse("10.0.0.1");

        /// <summary>
        /// Включен ли пул.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Разрешены ли dynamic-аренды в этом пуле.
        /// </summary>
        public bool DynamicEnabled { get; set; } = true;

        /// <summary>
        /// Разрешены ли automatic-аренды.
        /// </summary>
        public bool AutomaticEnabled { get; set; } = true;

        /// <summary>
        /// Разрешены ли manual-аренды (резервации).
        /// </summary>
        public bool ManualEnabled { get; set; } = true;

        /// <summary>
        /// Lease time для dynamic-выдачи (если не задан, используется глобальный).
        /// </summary>
        public TimeSpan? DynamicLeaseTimeOverride { get; set; }

        /// <summary>
        /// Статические резервации: MAC -> IP.
        /// </summary>
        public Dictionary<string, IPAddress> ManualReservations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Текущая аренда (lease) IP-адреса.
    /// </summary>
    public sealed class DhcpLease
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("D");

        public string PoolId { get; set; } = string.Empty;

        public IPAddress Address { get; set; } = IPAddress.Any;

        public PhysicalAddress MacAddress { get; set; } = PhysicalAddress.None;

        /// <summary>
        /// MAC как строка (для удобства сериализации/поиска).
        /// </summary>
        public string MacString
        {
            get => MacAddress.ToString();
            set
            {
                if (PhysicalAddress.TryParse(value, out var mac))
                    MacAddress = mac;
            }
        }

        /// <summary>
        /// Тип выделения (manual / automatic / dynamic).
        /// </summary>
        public DhcpAllocationType AllocationType { get; set; } = DhcpAllocationType.Dynamic;

        /// <summary>
        /// Интерфейс, через который этот lease был выдан.
        /// </summary>
        public string InterfaceId { get; set; } = string.Empty;

        /// <summary>
        /// Время выдачи.
        /// </summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Время истечения аренды (для dynamic).
        /// null для manual/automatic (постоянные).
        /// </summary>
        public DateTimeOffset? ExpiresAt { get; set; }

        /// <summary>
        /// Сколько осталось до конца аренды (расчитывается на лету).
        /// </summary>
        public TimeSpan? Remaining
        {
            get
            {
                if (ExpiresAt is null) return null;
                var left = ExpiresAt.Value - DateTimeOffset.UtcNow;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                return left;
            }
        }

    }
}
