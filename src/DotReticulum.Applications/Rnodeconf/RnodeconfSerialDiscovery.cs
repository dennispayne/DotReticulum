using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace DotReticulum.Applications.Rnodeconf;

public sealed record RnodeconfSerialPort(string Port, string? UsbSerial, string Description);

/// <summary>Native USB serial identity discovery; no ports are opened or queried.</summary>
public static class RnodeconfSerialDiscovery
{
    public static IReadOnlyList<RnodeconfSerialPort> Enumerate()
    {
        if (OperatingSystem.IsWindows()) return EnumerateWindows();
        if (OperatingSystem.IsLinux()) return EnumerateLinux();
        if (OperatingSystem.IsMacOS()) return EnumerateMac();
        return SerialPort.GetPortNames().Order(StringComparer.Ordinal).Select(p => new RnodeconfSerialPort(p, null, "Serial port; USB identity unavailable")).ToArray();
    }

    private static IReadOnlyList<RnodeconfSerialPort> EnumerateLinux()
    {
        var result = new List<RnodeconfSerialPort>();
        foreach (var port in SerialPort.GetPortNames().Take(1024))
        {
            string? serial = null;
            var name = Path.GetFileName(port);
            var link = new DirectoryInfo(Path.Combine("/sys/class/tty", name, "device"));
            var directory = link.Exists ? link.ResolveLinkTarget(true) as DirectoryInfo : null;
            for (var count = 0; directory is not null && count < 8; count++, directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "serial");
                if (File.Exists(path) && new FileInfo(path).Length < 4096)
                {
                    serial = File.ReadAllText(path).Trim();
                    break;
                }
            }
            result.Add(new(port, serial, name));
        }
        return result.OrderBy(p => p.Port, StringComparer.Ordinal).ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<RnodeconfSerialPort> EnumerateWindows()
    {
        var result = new List<RnodeconfSerialPort>();
        var available = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var enumeration = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
        foreach (var bus in new[] { "USB", "FTDIBUS" })
        {
            using var devices = enumeration?.OpenSubKey(bus);
            foreach (var deviceName in devices?.GetSubKeyNames().Take(2048) ?? [])
            {
                using var device = devices?.OpenSubKey(deviceName);
                foreach (var instanceName in device?.GetSubKeyNames().Take(256) ?? [])
                {
                    using var instance = device?.OpenSubKey(instanceName);
                    using var parameters = instance?.OpenSubKey("Device Parameters");
                    if (parameters?.GetValue("PortName") is not string port || !available.Contains(port)) continue;
                    var deviceId = $@"{bus}\{deviceName}\{instanceName}";
                    if (CM_Locate_DevNode(out _, deviceId, 0) != 0) continue;
                    var serial = WindowsUsbSerial(deviceId, WindowsParent);
                    result.Add(new(port, serial, instance?.GetValue("FriendlyName") as string ?? deviceName));
                }
            }
        }
        foreach (var port in available)
            if (!result.Any(p => p.Port.Equals(port, StringComparison.OrdinalIgnoreCase))) result.Add(new(port, null, "Serial port"));
        return result.DistinctBy(p => p.Port, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Port, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string? WindowsUsbSerial(string deviceId, Func<string, string?> parent)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth < 8 && visited.Add(deviceId); depth++)
        {
            var parts = deviceId.Split('\\');
            if (parts.Length == 3 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase) &&
                parts[1].StartsWith("VID_", StringComparison.OrdinalIgnoreCase) &&
                !parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase) &&
                parts[2].Length > 0 && !parts[2].Contains('&'))
                return parts[2];
            var ancestor = parent(deviceId);
            if (ancestor is null) return null;
            deviceId = ancestor;
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsParent(string deviceId)
    {
        if (CM_Locate_DevNode(out var device, deviceId, 0) != 0 ||
            CM_Get_Parent(out var parent, device, 0) != 0) return null;
        var id = new StringBuilder(1024);
        var status = CM_Get_Device_ID(parent, id, id.Capacity, 0);
        if (status != 0) throw new IOException($"Could not read the USB parent identity (CONFIGRET 0x{status:x}).");
        return id.ToString();
    }

    [SupportedOSPlatform("macos")]
    private static IReadOnlyList<RnodeconfSerialPort> EnumerateMac()
    {
        const uint utf8 = 0x08000100;
        var result = new List<RnodeconfSerialPort>();
        var matching = IOServiceMatching("IOSerialBSDClient");
        if (matching == IntPtr.Zero || IOServiceGetMatchingServices(0, matching, out var iterator) != 0) return result;
        var portKey = CFStringCreateWithCString(IntPtr.Zero, "IOCalloutDevice", utf8);
        var serialKey = CFStringCreateWithCString(IntPtr.Zero, "USB Serial Number", utf8);
        try
        {
            uint service;
            while ((service = IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    var port = IORegistryEntryCreateCFProperty(service, portKey, IntPtr.Zero, 0);
                    var serial = IORegistryEntrySearchCFProperty(service, "IOService", serialKey, IntPtr.Zero, 3);
                    try
                    {
                        var name = Text(port);
                        if (name is not null) result.Add(new(name, Text(serial), "USB serial"));
                    }
                    finally { if (port != IntPtr.Zero) CFRelease(port); if (serial != IntPtr.Zero) CFRelease(serial); }
                }
                finally { IOObjectRelease(service); }
                if (result.Count >= 1024) break;
            }
        }
        finally
        {
            IOObjectRelease(iterator);
            if (portKey != IntPtr.Zero) CFRelease(portKey);
            if (serialKey != IntPtr.Zero) CFRelease(serialKey);
        }
        return result;

        static string? Text(IntPtr value)
        {
            if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID()) return null;
            var bytes = new byte[4096];
            if (CFStringGetCString(value, bytes, bytes.Length, utf8) == 0) return null;
            var end = Array.IndexOf(bytes, (byte)0);
            return end >= 0 ? System.Text.Encoding.UTF8.GetString(bytes.AsSpan(0, end)) : null;
        }
    }

    private const string Iokit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNode(out uint device, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint device, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID(uint device, StringBuilder id, int length, uint flags);
    [DllImport(Iokit)] private static extern IntPtr IOServiceMatching([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Iokit)] private static extern int IOServiceGetMatchingServices(uint port, IntPtr matching, out uint iterator);
    [DllImport(Iokit)] private static extern uint IOIteratorNext(uint iterator);
    [DllImport(Iokit)] private static extern int IOObjectRelease(uint value);
    [DllImport(Iokit)] private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);
    [DllImport(Iokit)] private static extern IntPtr IORegistryEntrySearchCFProperty(uint entry, [MarshalAs(UnmanagedType.LPUTF8Str)] string plane, IntPtr key, IntPtr allocator, uint options);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundation)] private static extern byte CFStringGetCString(IntPtr value, [Out] byte[] buffer, nint size, uint encoding);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(IntPtr value);
    [DllImport(CoreFoundation)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
}
