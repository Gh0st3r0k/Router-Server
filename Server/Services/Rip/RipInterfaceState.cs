// WANT.Service/Rip/RipInterfaceState.cs
namespace WANT.Service.Rip
{
    /// <summary>
    /// Состояние RIP на конкретном интерфейсе (по GUID).
    /// </summary>
    public sealed class RipInterfaceState
    {
        /// <summary>
        /// GUID интерфейса (как в InterfaceScanner / PacketBridgeWorker), без фигурных скобок, UpperInvariant.
        /// </summary>
        public string InterfaceId { get; set; } = string.Empty;

        /// <summary>
        /// Включён ли RIP на этом интерфейсе.
        /// </summary>
        public bool Enabled { get; set; }
    }
}
