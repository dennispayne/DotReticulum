using System.IO.Ports;

namespace DotReticulum.Interfaces;

internal static class NativeSerialPort
{
    internal static SerialPort Create(string name, int baudRate, Parity parity, int dataBits, StopBits stopBits,
        int readTimeout = SerialPort.InfiniteTimeout, int writeTimeout = SerialPort.InfiniteTimeout) =>
        new(name, baudRate, parity, dataBits, stopBits)
        {
            Handshake = Handshake.None,
            // pyserial disables handshake but still asserts both control lines by default.
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = readTimeout,
            WriteTimeout = writeTimeout,
        };
}
