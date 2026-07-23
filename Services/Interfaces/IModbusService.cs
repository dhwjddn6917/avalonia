using System.Threading;
using System.Threading.Tasks;
using System;

namespace MobileEssControl.Services.Interfaces;

public interface IModbusService
{
    event Action<string>? CommunicationLog;
    bool IsConnected { get; }

    string? ConnectedPortName { get; }

    Task<bool> ConnectAsync(
        string portName,
        int baudRate = 115200,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync();
    Task<string?> FindAndConnectEmsAsync(
    int baudRate = 115200,
    CancellationToken cancellationToken = default);
    Task<ushort[]> ReadHoldingRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort numberOfPoints,
        CancellationToken cancellationToken = default);

    Task WriteSingleCoilAsync(
        byte slaveId,
        ushort address,
        bool value,
        CancellationToken cancellationToken = default);

    Task WriteSingleRegisterAsync(
        byte slaveId,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default);

    Task WriteMultipleRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort[] values,
        CancellationToken cancellationToken = default);
}