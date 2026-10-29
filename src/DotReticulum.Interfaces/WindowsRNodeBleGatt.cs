using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DotReticulum.Interfaces;

/// <summary>Desktop Bluetooth GATT API; does not initiate pairing or install a driver.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRNodeBleGatt : IRNodeBleGatt
{
    private static readonly Guid DeviceInterface = new("781aee18-7733-4ce4-add0-91f41c67b592");
    private static readonly Guid ServiceInterface = new("6e3bb679-4372-40c8-9eaa-4509df260cd8");
    private static readonly PropertyKey BluetoothAddress = new(new("2bd67d8b-8beb-48d5-87e0-6cda3428040a"), 1);
    private static readonly PropertyKey BluetoothFlags = new(new("2bd67d8b-8beb-48d5-87e0-6cda3428040a"), 3);
    private static readonly PropertyKey BluetoothService = new(new("2bd67d8b-8beb-48d5-87e0-6cda3428040a"), 2);
    private static readonly PropertyKey FriendlyName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PropertyKey DeviceName = new(new("b725f130-47ef-101a-a5f1-02608c9eebac"), 10);
    private const uint LePaired = 0x10000;
    private const uint LeConnected = 0x1000000;
    private const int MoreData = unchecked((int)0x800700ea);
    private readonly SafeFileHandle _device;
    private readonly DeviceSet _devices;
    private readonly InterfaceData _interface;
    private readonly Characteristic _rx;
    private readonly Characteristic _tx;
    private readonly GattCallback _callback;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _nativeGate = new();
    private Task _monitor = Task.CompletedTask;
    private Task? _dispose;
    private nint _registration;
    private bool _disposed;

    private WindowsRNodeBleGatt(SafeFileHandle device, DeviceSet devices, InterfaceData info,
        Characteristic rx, Characteristic tx)
    {
        _device = device;
        _devices = devices;
        _interface = info;
        _rx = rx;
        _tx = tx;
        _callback = Notification;
    }

    // BluetoothGATT has no public negotiated-MTU getter. Twenty bytes fits the
    // mandatory ATT MTU (23 minus the write-command header) on every connection.
    public int MaxWritePayload => 20;
    public event Action<ReadOnlyMemory<byte>>? Received;
    public event Action<Exception>? Disconnected;

    internal static ValueTask<IRNodeBleGatt> OpenAsync(RNodeBleSelector selector, CancellationToken cancellationToken) =>
        new(Task.Run<IRNodeBleGatt>(() => Open(selector, cancellationToken), cancellationToken));

    private static WindowsRNodeBleGatt Open(RNodeBleSelector selector, CancellationToken cancellationToken)
    {
        var guid = DeviceInterface;
        var devices = Native.SetupDiGetClassDevsW(ref guid, null, 0, 0x12);
        if (devices.IsInvalid) throw NativeError("Enumerating Windows BLE devices");
        try
        {
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(devices, 0, ref guid, index, ref info))
                {
                    if (Marshal.GetLastPInvokeError() == 259) break;
                    throw NativeError("Enumerating Windows BLE interfaces");
                }
                // Microsoft documents this interface class as enumerating paired
                // BLE devices. Flags can further reject stale unpaired entries;
                // unavailable optional flags do not replace that pairing boundary.
                var flags = ReadFlags(devices, info);
                if (flags is { } knownFlags && (knownFlags & LePaired) == 0) continue;
                var address = ReadInterfaceProperty(devices, info, BluetoothAddress, 18);
                var addressText = address is null ? null : FormatAddress(Encoding.Unicode.GetString(address).TrimEnd('\0'));
                var devInfo = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                var path = GetPath(devices, info, ref devInfo);
                var name = ReadDeviceName(devices, devInfo, FriendlyName) ?? ReadDeviceName(devices, devInfo, DeviceName);
                if (!selector.Matches(name, addressText, bonded: true, uart: true)) continue;
                var device = Native.CreateFileW(path, 0xc0000000, 3, 0, 3, 0, 0);
                if (device.IsInvalid) { device.Dispose(); throw NativeError("Opening paired Windows BLE device"); }
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var service = GetServices(device).FirstOrDefault(s =>
                        s.Uuid.IsShort == 0 && s.Uuid.Long == RNodeBleSelector.UartService);
                    if (service.Handle == 0) continue;
                    SafeFileHandle? serviceDevice = OpenService(devInfo.DevInst, cancellationToken);
                    try
                    {
                        var characteristics = GetCharacteristics(serviceDevice, service);
                        var rx = characteristics.FirstOrDefault(c =>
                            c.Uuid.IsShort == 0 && c.Uuid.Long == RNodeBleSelector.UartReceive);
                        var tx = characteristics.FirstOrDefault(c =>
                            c.Uuid.IsShort == 0 && c.Uuid.Long == RNodeBleSelector.UartTransmit);
                        if (rx.WriteWithoutResponse == 0 || tx.Notifiable == 0)
                            throw new IOException("The paired Nordic UART device lacks write-without-response or notifications.");
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = new WindowsRNodeBleGatt(serviceDevice, devices, info, rx, tx);
                        serviceDevice = null;
                        devices = null!;
                        return result;
                    }
                    finally { serviceDevice?.Dispose(); }
                }
                finally { device?.Dispose(); }
            }
            throw new IOException("No matching paired Windows BLE Nordic UART device was found. Pair the RNode in Windows settings first; a working Bluetooth adapter is required.");
        }
        finally { devices?.Dispose(); }
    }

    internal static string? FormatAddress(string value)
    {
        var hex = value.Replace(":", "", StringComparison.Ordinal);
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit)) return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
    }

    private static string GetPath(DeviceSet devices, InterfaceData info, ref DeviceInfo devInfo)
    {
        _ = Native.SetupDiGetDeviceInterfaceDetailW(devices, ref info, 0, 0, out var required, ref devInfo);
        if (Marshal.GetLastPInvokeError() != 122 || required is < 6 or > 65536)
            throw NativeError("Reading Windows BLE device path length");
        var detail = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(detail, nint.Size == 8 ? 8 : 6);
            if (!Native.SetupDiGetDeviceInterfaceDetailW(devices, ref info, detail, required, out _, ref devInfo))
                throw NativeError("Reading Windows BLE device path");
            return Marshal.PtrToStringUni(detail + 4) ?? throw new IOException("Missing Windows BLE device path.");
        }
        finally { Marshal.FreeHGlobal(detail); }
    }

    private static SafeFileHandle OpenService(uint parent, CancellationToken cancellationToken)
    {
        var guid = ServiceInterface;
        using var services = Native.SetupDiGetClassDevsW(ref guid, null, 0, 0x12);
        if (services.IsInvalid) throw NativeError("Enumerating Windows BLE services");
        for (uint index = 0; ; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
            if (!Native.SetupDiEnumDeviceInterfaces(services, 0, ref guid, index, ref info))
            {
                if (Marshal.GetLastPInvokeError() == 259) break;
                throw NativeError("Enumerating Windows BLE service interfaces");
            }
            var serviceGuid = ReadInterfaceProperty(services, info, BluetoothService, 13);
            if (serviceGuid is not { Length: 16 } || new Guid(serviceGuid) != RNodeBleSelector.UartService) continue;
            var device = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
            var path = GetPath(services, info, ref device);
            if (Native.CM_Get_Parent(out var serviceParent, device.DevInst, 0) != 0 || serviceParent != parent) continue;
            var handle = Native.CreateFileW(path, 0xc0000000, 3, 0, 3, 0, 0);
            if (!handle.IsInvalid) return handle;
            handle.Dispose();
            throw NativeError("Opening the paired Nordic UART service");
        }
        throw new IOException("Windows has no Nordic UART service interface for the selected paired BLE device.");
    }

    public ValueTask StartNotificationsAsync(CancellationToken cancellationToken) =>
        new(Task.Run(() =>
        {
            lock (_nativeGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                var registration = new Registration { Count = 1, Characteristic = _tx };
                Check(Native.BluetoothGATTRegisterEvent(_device, 0, ref registration, _callback, 0, out _registration, 0),
                    "Registering Nordic UART notifications");
                var tx = _tx;
                var descriptors = GetDescriptors(_device, tx);
                var configuration = descriptors.FirstOrDefault(d => d.Type == 2);
                if (configuration.Handle == 0) throw new IOException("Nordic UART notifications lack a client configuration descriptor.");
                var value = new byte[80];
                // BTH_LE_GATT_DESCRIPTOR_VALUE: type, UUID, 48-byte union,
                // DataSize, trailing data; the CCCD union begins at byte 24.
                BinaryPrimitives.WriteInt32LittleEndian(value, 2);
                value[4] = 1;
                BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(8), 0x2902);
                value[24] = 1;
                Check(Native.BluetoothGATTSetDescriptorValue(_device, ref configuration, value, 0),
                    "Enabling Nordic UART notifications");
                cancellationToken.ThrowIfCancellationRequested();
                _monitor = MonitorAsync();
            }
        }, cancellationToken));

    public ValueTask WriteWithoutResponseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.Length is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(data));
        return new(Task.Run(() =>
        {
            lock (_nativeGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                var value = new byte[4 + data.Length];
                BinaryPrimitives.WriteInt32LittleEndian(value, data.Length);
                data.Span.CopyTo(value.AsSpan(4));
                var rx = _rx;
                Check(Native.BluetoothGATTSetCharacteristicValue(_device, ref rx, value, 0, 0x20),
                    "Writing Nordic UART without response");
                cancellationToken.ThrowIfCancellationRequested();
            }
        }, cancellationToken));
    }

    private async Task MonitorAsync()
    {
        var observedConnected = false;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), _stop.Token).ConfigureAwait(false);
                uint? flags;
                lock (_nativeGate)
                {
                    if (_disposed) return;
                    flags = ReadFlags(_devices, _interface);
                }
                if (flags is null) continue;
                if ((flags.Value & LePaired) == 0 || (observedConnected && (flags.Value & LeConnected) == 0))
                    throw new IOException("Windows reports the RNode BLE device disconnected or unpaired.");
                observedConnected |= (flags.Value & LeConnected) != 0;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) { Disconnected?.Invoke(exception); }
    }

    private void Notification(int eventType, nint parameters, nint context)
    {
        try
        {
            if (eventType != 0 || parameters == 0) throw new IOException("Invalid Windows BLE notification.");
            var value = Marshal.PtrToStructure<ValueChanged>(parameters);
            if (value.Value == 0 || value.Attribute != _tx.ValueHandle) return;
            var length = Marshal.ReadInt32(value.Value);
            if (length is < 0 or > 512 || value.Size < (nuint)(4 + length))
                throw new IOException("Invalid Windows BLE notification length.");
            var bytes = new byte[length];
            Marshal.Copy(value.Value + 4, bytes, 0, length);
            Received?.Invoke(bytes);
        }
        catch (Exception exception)
        {
            // Never propagate a managed exception across the native callback ABI.
            Disconnected?.Invoke(exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_nativeGate) return new(_dispose ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _stop.Cancel();
        await Task.Yield();
        await _monitor.ConfigureAwait(false);
        var status = 0;
        lock (_nativeGate)
        {
            if (_registration != 0)
            {
                status = Native.BluetoothGATTUnregisterEvent(_registration, 0);
                _registration = 0;
            }
            _device.Dispose();
            _devices.Dispose();
        }
        _stop.Dispose();
        GC.KeepAlive(_callback);
        Check(status, "Unregistering Nordic UART notifications");
    }

    private static Service[] GetServices(SafeFileHandle device)
    {
        var status = Native.BluetoothGATTGetServices(device, 0, null, out var count, 0);
        if (status != MoreData) { Check(status, "Counting Windows BLE services"); return []; }
        if (count > 256) throw new IOException("Too many Windows BLE services.");
        var services = new Service[count];
        Check(Native.BluetoothGATTGetServices(device, count, services, out var actual, 0), "Reading Windows BLE services");
        if (actual > count) throw new IOException("Windows BLE service list changed.");
        return services[..actual];
    }

    private static Characteristic[] GetCharacteristics(SafeFileHandle device, Service service)
    {
        var status = Native.BluetoothGATTGetCharacteristics(device, ref service, 0, null, out var count, 0);
        if (status != MoreData) { Check(status, "Counting Nordic UART characteristics"); return []; }
        if (count > 256) throw new IOException("Too many Nordic UART characteristics.");
        var characteristics = new Characteristic[count];
        Check(Native.BluetoothGATTGetCharacteristics(device, ref service, count, characteristics, out var actual, 0),
            "Reading Nordic UART characteristics");
        if (actual > count) throw new IOException("Windows BLE characteristic list changed.");
        return characteristics[..actual];
    }

    private static Descriptor[] GetDescriptors(SafeFileHandle device, Characteristic characteristic)
    {
        var status = Native.BluetoothGATTGetDescriptors(device, ref characteristic, 0, null, out var count, 0);
        if (status != MoreData) { Check(status, "Counting Nordic UART descriptors"); return []; }
        if (count > 256) throw new IOException("Too many Nordic UART descriptors.");
        var descriptors = new Descriptor[count];
        Check(Native.BluetoothGATTGetDescriptors(device, ref characteristic, count, descriptors, out var actual, 0),
            "Reading Nordic UART descriptors");
        if (actual > count) throw new IOException("Windows BLE descriptor list changed.");
        return descriptors[..actual];
    }

    private static uint? ReadFlags(DeviceSet devices, InterfaceData info)
    {
        var bytes = ReadInterfaceProperty(devices, info, BluetoothFlags, 7);
        return bytes is { Length: 4 } ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : null;
    }

    private static byte[]? ReadInterfaceProperty(DeviceSet devices, InterfaceData info, PropertyKey key, uint expectedType)
    {
        var buffer = new byte[1024];
        if (!Native.SetupDiGetDeviceInterfacePropertyW(devices, ref info, ref key, out var type,
            buffer, (uint)buffer.Length, out var required, 0)) return null;
        return type == expectedType && required <= buffer.Length ? buffer[..(int)required] : null;
    }

    private static string? ReadDeviceName(DeviceSet devices, DeviceInfo info, PropertyKey key)
    {
        var buffer = new byte[1024];
        if (!Native.SetupDiGetDevicePropertyW(devices, ref info, ref key, out var type,
            buffer, (uint)buffer.Length, out var required, 0) || type != 18 || required > buffer.Length) return null;
        return Encoding.Unicode.GetString(buffer, 0, (int)required).TrimEnd('\0');
    }

    private static void Check(int status, string operation)
    {
        if (status < 0) throw new IOException($"{operation} failed (HRESULT 0x{status:x8}).", Marshal.GetExceptionForHR(status));
    }
    private static IOException NativeError(string operation) =>
        new($"{operation} failed.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public uint Size; public Guid Guid; public uint Flags; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid Class; public uint DevInst; public nint Reserved; }
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct Uuid
    {
        [FieldOffset(0)] public byte IsShort;
        [FieldOffset(4)] public Guid Long;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Service { public Uuid Uuid; public ushort Handle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Characteristic
    {
        public ushort ServiceHandle;
        public Uuid Uuid;
        public ushort Handle, ValueHandle;
        public byte Broadcastable, Readable, Writable, WriteWithoutResponse, SignedWritable, Notifiable, Indicatable, Extended;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Descriptor
    {
        public ushort ServiceHandle, CharacteristicHandle;
        public int Type;
        public Uuid Uuid;
        public ushort Handle;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Registration { public ushort Count; public Characteristic Characteristic; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ValueChanged { public ushort Attribute; public nuint Size; public nint Value; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GattCallback(int eventType, nint parameters, nint context);

    private sealed class DeviceSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DeviceSet() : base(true) { }
        protected override bool ReleaseHandle() => Native.SetupDiDestroyDeviceInfoList(handle);
    }

    private static class Native
    {
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern DeviceSet SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, nint parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(DeviceSet devices, nint device, ref Guid guid, uint index, ref InterfaceData info);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(nint devices);
        [DllImport("cfgmgr32.dll")]
        internal static extern uint CM_Get_Parent(out uint parent, uint child, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetailW(DeviceSet devices, ref InterfaceData info, nint detail,
            uint size, out uint required, ref DeviceInfo deviceInfo);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfacePropertyW(DeviceSet devices, ref InterfaceData info, ref PropertyKey key,
            out uint type, [Out] byte[] value, uint size, out uint required, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDevicePropertyW(DeviceSet devices, ref DeviceInfo info, ref PropertyKey key,
            out uint type, [Out] byte[] value, uint size, out uint required, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
            uint creation, uint flags, nint template);
        [DllImport("bluetoothapis.dll")]
        internal static extern int BluetoothGATTGetServices(SafeFileHandle device, ushort count, [Out] Service[]? services,
            out ushort actual, uint flags);
        [DllImport("bluetoothapis.dll")]
        internal static extern int BluetoothGATTGetCharacteristics(SafeFileHandle device, ref Service service,
            ushort count, [Out] Characteristic[]? characteristics, out ushort actual, uint flags);
        [DllImport("bluetoothapis.dll")]
        internal static extern int BluetoothGATTGetDescriptors(SafeFileHandle device, ref Characteristic characteristic,
            ushort count, [Out] Descriptor[]? descriptors, out ushort actual, uint flags);
        [DllImport("bluetoothapis.dll")]
        internal static extern int BluetoothGATTSetDescriptorValue(SafeFileHandle device, ref Descriptor descriptor, byte[] value, uint flags);
        [DllImport("bluetoothapis.dll")]
        internal static extern int BluetoothGATTSetCharacteristicValue(SafeFileHandle device, ref Characteristic characteristic,
            byte[] value, ulong context, uint flags);
        [DllImport("bluetoothapis.dll")]
        internal static extern int BluetoothGATTRegisterEvent(SafeFileHandle device, int eventType, ref Registration registration,
            GattCallback callback, nint context, out nint eventHandle, uint flags);
        [DllImport("bluetoothapis.dll")]
        internal static extern int BluetoothGATTUnregisterEvent(nint eventHandle, uint flags);
    }
}
