#nullable enable
using System;
using System.Runtime.Versioning;

namespace WANT.Service.Interface
{
    public static class InterfaceSnapshotUpdater
    {
        public static void UpdateOnce(WANT.Service.InterfaceScanner store)
        {
            if (!OperatingSystem.IsWindows())
                return;

            UpdateOnceWindowsOnly(store);
        }

        [SupportedOSPlatform("windows")]
        private static void UpdateOnceWindowsOnly(WANT.Service.InterfaceScanner store)
        {
            var latest = PhysicalInterfaceDetector.GetAllPhysicalEthernet(onlyUp: true);
            InterfaceManager.Sync(latest, store, hardDeleteMissing: true); // ⬅️ удаляем полностью
        }
    }
}
