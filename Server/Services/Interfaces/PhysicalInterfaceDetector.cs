#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Management;
using System.Runtime.Versioning;

namespace WANT.Service.Interface
{
    [SupportedOSPlatform("windows")]
    public static class PhysicalInterfaceDetector
    {
        public static IReadOnlyList<NetInterface> GetAllPhysicalEthernet(bool onlyUp = false)
        {
            var meta = BuildAdapterMeta();
            var list = new List<NetInterface>();

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet)
                    continue;

                // ⬅️ вот фильтр: пропускать всё что не UP, когда onlyUp = true
                if (onlyUp && nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                var guid = nic.Id.Trim('{', '}').ToUpperInvariant();
                if (!meta.TryGetValue(guid, out var m) || !m.PhysicalAdapter || !m.ConnectorPresent)
                    continue;

                var osName   = nic.Name ?? string.Empty;
                var friendly = m.NetConnectionId ?? osName;

                list.Add(new NetInterface(
                    Guid: guid,
                    Description: nic.Description ?? string.Empty,
                    OSName: osName,
                    FriendlyName: friendly,
                    Type: NicType.Ethernet,
                    Mac: nic.GetPhysicalAddress()?.ToString() ?? string.Empty,
                    Status: nic.OperationalStatus == OperationalStatus.Up ? NicStatus.Up
                        : nic.OperationalStatus == OperationalStatus.Down ? NicStatus.Down
                        : NicStatus.Unknown,
                    UserName: friendly,
                    Ip: null,
                    Mask: null,
                    PrefixLen: null,
                    TrackState: TrackState.Active
                ));
            }

            return list;
        }

        private static bool IsWireless(NetworkInterface nic)
        {
            var d = (nic.Description ?? "") + " " + (nic.Name ?? "");
            return d.Contains("wi-fi", StringComparison.OrdinalIgnoreCase)
                || d.Contains("wifi", StringComparison.OrdinalIgnoreCase)
                || d.Contains("wireless", StringComparison.OrdinalIgnoreCase)
                || d.Contains("802.11", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBluetooth(NetworkInterface nic, string? pnp)
        {
            var d = (nic.Description ?? "") + " " + (nic.Name ?? "");
            if (!string.IsNullOrEmpty(pnp) && pnp.IndexOf("BTH", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return d.Contains("bluetooth", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, (bool PhysicalAdapter, bool ConnectorPresent, string? PnpDeviceId, string? NetConnectionId)> BuildAdapterMeta()
        {
            var dict = new Dictionary<string, (bool, bool, string?, string?)>(StringComparer.OrdinalIgnoreCase);

            // MSFT_NetAdapter (как было) — оставляем
            try
            {
                var scope = new ManagementScope(@"\\.\root\StandardCimv2");
                scope.Connect();
                using var s = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT InterfaceGuid, HardwareInterface, ConnectorPresent, PNPDeviceID FROM MSFT_NetAdapter"));
                foreach (ManagementObject mo in s.Get())
                {
                    var guid = (mo["InterfaceGuid"] as string)?.Trim('{', '}');
                    if (string.IsNullOrWhiteSpace(guid)) continue;
                    bool hw   = (bool?)mo["HardwareInterface"] == true;
                    bool conn = (bool?)mo["ConnectorPresent"] == true;
                    var pnp   = mo["PNPDeviceID"] as string;
                    dict[guid.ToUpperInvariant()] = (hw, conn, pnp, /*NetConnectionId*/ null);
                }
            }
            catch { /* ok */ }

            // Win32_NetworkAdapter — добавим NetConnectionID (дружелюбное имя)
            using var s2 = new ManagementObjectSearcher(
                "SELECT GUID, PhysicalAdapter, PNPDeviceID, NetConnectionID FROM Win32_NetworkAdapter");
            foreach (ManagementObject mo in s2.Get())
            {
                var guid = (mo["GUID"] as string)?.Trim('{', '}');
                if (string.IsNullOrWhiteSpace(guid)) continue;
                bool physical = (bool?)mo["PhysicalAdapter"] == true;
                var pnp = mo["PNPDeviceID"] as string;
                var netConnId = mo["NetConnectionID"] as string; // <-- вот он FriendlyName

                if (dict.TryGetValue(guid.ToUpperInvariant(), out var ex))
                    dict[guid.ToUpperInvariant()] = (ex.Item1 || physical, ex.Item2, ex.Item3 ?? pnp, ex.Item4 ?? netConnId);
                else
                    dict[guid.ToUpperInvariant()] = (physical, false, pnp, netConnId);
            }

            return dict;
        }
    }
}
