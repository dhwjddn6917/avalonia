using System.Collections.Generic;

namespace MobileEssControl.Services.Modbus;

public class FakeModbusService
{
    private readonly Dictionary<byte, Dictionary<ushort, ushort>> _slaveRegisters = new();

    public FakeModbusService()
    {
        InitializeSlaveRegisters();
    }

    private void InitializeSlaveRegisters()
    {
        // Slave 1 : EMS
        _slaveRegisters[1] = new Dictionary<ushort, ushort>();

        // Slave 2 : 인버터 1
        _slaveRegisters[2] = new Dictionary<ushort, ushort>();

        // Slave 3 : 인버터 2
        _slaveRegisters[3] = new Dictionary<ushort, ushort>();

        // Slave 4 : DC-DC 1
        _slaveRegisters[4] = new Dictionary<ushort, ushort>();

        // Slave 5 : DC-DC 2
        _slaveRegisters[5] = new Dictionary<ushort, ushort>();

        // Slave 6 : Battery Pack 1
        _slaveRegisters[6] = new Dictionary<ushort, ushort>();

        // Slave 7 : Battery Pack 2
        _slaveRegisters[7] = new Dictionary<ushort, ushort>();

        // Slave 8 : EVCC
        _slaveRegisters[8] = new Dictionary<ushort, ushort>();
    }

    public ushort ReadRegister(byte slaveId, ushort address)
    {
        if (!_slaveRegisters.TryGetValue(slaveId, out var registers))
        {
            return 0;
        }

        return registers.TryGetValue(address, out ushort value)
            ? value
            : (ushort)0;
    }

    public void WriteRegister(byte slaveId, ushort address, ushort value)
    {
        if (!_slaveRegisters.ContainsKey(slaveId))
        {
            _slaveRegisters[slaveId] = new Dictionary<ushort, ushort>();
        }

        _slaveRegisters[slaveId][address] = value;
    }

    public ushort[] ReadHoldingRegisters(
        byte slaveId,
        ushort startAddress,
        ushort quantity)
    {
        var values = new ushort[quantity];

        for (ushort i = 0; i < quantity; i++)
        {
            values[i] = ReadRegister(slaveId, (ushort)(startAddress + i));
        }

        return values;
    }

    public void WriteMultipleRegisters(
        byte slaveId,
        ushort startAddress,
        ushort[] values)
    {
        for (ushort i = 0; i < values.Length; i++)
        {
            WriteRegister(slaveId, (ushort)(startAddress + i), values[i]);
        }
    }
}