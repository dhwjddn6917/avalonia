using System;
using System.Threading;
using System.Threading.Tasks;
using MobileEssControl.Services.Interfaces;

namespace MobileEssControl.Services.Modbus;

/// <summary>
/// 실제 시리얼 포트에 접근하지 않는 IModbusService 구현입니다.
/// EMS/CAN-over-Bluetooth 연동이 준비되기 전까지, 화면 UI만 확인할 수 있도록
/// 항상 미연결 상태로 동작합니다.
/// </summary>
public class NullModbusService : IModbusService
{
    public event Action<string>? CommunicationLog;

    public bool IsConnected => false;

    public string? ConnectedPortName => null;

    public Task<bool> ConnectAsync(
        string portName,
        int baudRate = 115200,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task DisconnectAsync()
    {
        return Task.CompletedTask;
    }

    public Task<string?> FindAndConnectEmsAsync(
        int baudRate = 115200,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<ushort[]> ReadHoldingRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort numberOfPoints,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("EMS 연동이 준비되지 않았습니다.");
    }

    public Task WriteSingleCoilAsync(
        byte slaveId,
        ushort address,
        bool value,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("EMS 연동이 준비되지 않았습니다.");
    }

    public Task WriteSingleRegisterAsync(
        byte slaveId,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("EMS 연동이 준비되지 않았습니다.");
    }

    public Task WriteMultipleRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort[] values,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("EMS 연동이 준비되지 않았습니다.");
    }
}
