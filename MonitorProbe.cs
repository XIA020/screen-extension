using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace TabletDisplay
{
    public static class MonitorProbe
    {
        private const int AttachedToDesktop = 1;
        private const int PrimaryDevice = 4;
        private const int Active = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public int State;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;

            public static DisplayDevice Create()
            {
                return new DisplayDevice { Size = Marshal.SizeOf(typeof(DisplayDevice)) };
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayDevices(string name, uint index, ref DisplayDevice device, uint flags);

        public static string FindExtendedParsec()
        {
            return FindExtendedVirtualDisplay("Parsec");
        }

        public static string FindExtendedVirtualDisplay(string provider)
        {
            foreach (string display in ExtendedVirtualDisplays(provider)) return display;
            return null;
        }

        public static IEnumerable<string> ExtendedVirtualDisplays(string provider)
        {
            for (uint index = 0; ; index++)
            {
                var adapter = DisplayDevice.Create();
                if (!EnumDisplayDevices(null, index, ref adapter, 0))
                    yield break;

                // A cloned monitor shares its source with the primary display.
                if ((adapter.State & AttachedToDesktop) == 0 || (adapter.State & PrimaryDevice) != 0)
                    continue;

                if (provider == "MttVDD")
                {
                    if (adapter.Id != null && adapter.Id.IndexOf("MTTVDD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        string.Equals(adapter.Description, "Virtual Display Driver", StringComparison.OrdinalIgnoreCase))
                        yield return adapter.Name;
                    continue;
                }

                for (uint monitorIndex = 0; ; monitorIndex++)
                {
                    var monitor = DisplayDevice.Create();
                    if (!EnumDisplayDevices(adapter.Name, monitorIndex, ref monitor, 0))
                        break;

                    if ((monitor.State & Active) != 0 &&
                        monitor.Id.StartsWith(@"MONITOR\PSCCDD", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return adapter.Name;
                        break;
                    }
                }
            }
        }
    }
}
