using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class VirtualDisplayDevice
{
    private const string HardwareId = @"Root\MttVDD";
    private const uint RegisterDevice = 0x19;
    private const uint RemoveDevice = 0x05;
    private static readonly Guid DisplayClass = new Guid("4D36E968-E325-11CE-BFC1-08002BE10318");

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo
    {
        public int Size;
        public Guid ClassGuid;
        public int Instance;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr parent);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name, ref Guid classGuid,
        string description, IntPtr parent, uint flags, ref DeviceInfo info);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref DeviceInfo info, uint property,
        byte[] data, int size);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint operation, IntPtr set, ref DeviceInfo info);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr parent, string hardwareId,
        string inf, uint flags, out bool needsRestart);

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static bool Install(string inf)
    {
        Guid deviceClass = DisplayClass;
        IntPtr set = SetupDiCreateDeviceInfoList(ref deviceClass, IntPtr.Zero);
        Check(set != new IntPtr(-1));
        try
        {
            var device = new DeviceInfo { Size = Marshal.SizeOf(typeof(DeviceInfo)) };
            Check(SetupDiCreateDeviceInfo(set, "MttVDD", ref deviceClass, null, IntPtr.Zero, 1, ref device));
            byte[] ids = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
            Check(SetupDiSetDeviceRegistryProperty(set, ref device, 1, ids, ids.Length));
            Check(SetupDiCallClassInstaller(RegisterDevice, set, ref device));
            try
            {
                bool needsRestart;
                // Allow Windows to display its publisher confirmation on a clean PC.
                Check(UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, inf, 0, out needsRestart));
                return needsRestart;
            }
            catch
            {
                SetupDiCallClassInstaller(RemoveDevice, set, ref device);
                throw;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
