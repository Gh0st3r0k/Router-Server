using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Management;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
public static class NicDebugger
{
    public static void PrintPhysicalEthernet()
    {
        Console.WriteLine("=== ФИЗИЧЕСКИЕ ПРОВОДНЫЕ ETHERNET (включая неактивные) ===");

        // Карта GUID -> (PhysicalAdapter, ConnectorPresent, PNPDeviceID)
        var meta = BuildAdapterMeta();

        int shown = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var guid = nic.Id.Trim('{', '}').ToUpperInvariant();

            // 1) Только Ethernet (не Wireless80211 и т.п.)
            if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet)
                continue;

            // 2) Есть метаданные из WMI и это физика с разъёмом
            if (!meta.TryGetValue(guid, out var m) || !m.PhysicalAdapter || !m.ConnectorPresent)
                continue;

            // 3) Явно отрезаем Bluetooth PAN / Wi-Fi по PNP/описанию
            if (IsBluetooth(nic, m.PnpDeviceId) || IsWireless(nic))
                continue;

            shown++;
            Console.WriteLine("----------------------------------------");
            Console.WriteLine($"Имя:      {nic.Name}");
            Console.WriteLine($"Описание: {nic.Description}");
            Console.WriteLine($"GUID:     {nic.Id}");
            Console.WriteLine($"Тип:      {nic.NetworkInterfaceType}");
            Console.WriteLine($"MAC:      {nic.GetPhysicalAddress()}");
            Console.WriteLine($"Статус:   {nic.OperationalStatus}");
        }

        if (shown == 0)
        {
            Console.WriteLine("Ничего не найдено по строгому фильтру.");
            Console.WriteLine("— Диагностика: ВСЕ интерфейсы —");
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                Console.WriteLine($"{nic.Name} | {nic.Description} | {nic.NetworkInterfaceType} | GUID={nic.Id} | MAC={nic.GetPhysicalAddress()} | {nic.OperationalStatus}");
            Console.WriteLine("— Конец диагностики —");
        }

        Console.WriteLine("========================================");
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

    // Собираем WMI-метаданные для GUIDов
    private static Dictionary<string, (bool PhysicalAdapter, bool ConnectorPresent, string? PnpDeviceId)> BuildAdapterMeta()
    {
        var dict = new Dictionary<string, (bool, bool, string?)>(StringComparer.OrdinalIgnoreCase);

        // MSFT_NetAdapter: даёт ConnectorPresent (есть физический разъём)
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
                bool hw  = (bool?)mo["HardwareInterface"] == true;        // физический адаптер
                bool conn= (bool?)mo["ConnectorPresent"] == true;         // есть разъём (RJ-45/USB и т.п.)
                var pnp  = mo["PNPDeviceID"] as string;
                dict[guid.ToUpperInvariant()] = (hw, conn, pnp);
            }
        }
        catch { /* на старых системах может не быть StandardCimv2 */ }

        // Дополняем PhysicalAdapter/PNP из Win32_NetworkAdapter (fallback/merge)
        using var s2 = new ManagementObjectSearcher(
            "SELECT GUID, PhysicalAdapter, PNPDeviceID FROM Win32_NetworkAdapter");
        foreach (ManagementObject mo in s2.Get())
        {
            var guid = (mo["GUID"] as string)?.Trim('{', '}');
            if (string.IsNullOrWhiteSpace(guid)) continue;
            bool physical = (bool?)mo["PhysicalAdapter"] == true;
            var pnp = mo["PNPDeviceID"] as string;

            if (dict.TryGetValue(guid.ToUpperInvariant(), out var ex))
                dict[guid.ToUpperInvariant()] = (ex.Item1 || physical, ex.Item2, ex.Item3 ?? pnp);
            else
                dict[guid.ToUpperInvariant()] = (physical, /*ConnectorPresent?*/ false, pnp);
        }

        return dict;
    }
}
