using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
                    if (parameters?.GetValue("PortName") is not string port) continue;
                    var serial = instanceName.Contains('&') ? null : instanceName;
                    result.Add(new(port, serial, instance?.GetValue("FriendlyName") as string ?? deviceName));
                }
            }
        }
        foreach (var port in SerialPort.GetPortNames())
            if (!result.Any(p => p.Port.Equals(port, StringComparison.OrdinalIgnoreCase))) result.Add(new(port, null, "Serial port"));
        return result.OrderBy(p => p.Port, StringComparer.OrdinalIgnoreCase).ToArray();
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
