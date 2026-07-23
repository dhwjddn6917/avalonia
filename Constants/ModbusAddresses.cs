namespace MobileEssControl.Constants;

/// <summary>
/// 주소맵의 Absolute Address 표기용 상수입니다.
/// 실제 Modbus RTU 요청에는 ModbusOffsets.cs의 Relative Address를 사용합니다.
/// </summary>
public static class ModbusAddresses
{
    // EMS System (Slave 1)
    public const ushort SystemVoltage = 31011;
    public const ushort SystemCurrent = 31012;
    public const ushort SystemSoc = 31017;

    public const ushort SystemStatus1 = 31022;
    public const ushort SystemStatus2 = 31023;

    public const ushort SystemAlarm1 = 31024;
    public const ushort SystemAlarm2 = 31025;

    // Battery Pack 1 (Slave 6)
    public const ushort Pack1Voltage = 32001;
    public const ushort Pack1Current = 32002;
    public const ushort Pack1Soc = 32005;

    // Battery Pack 2 (Slave 7)
    public const ushort Pack2Voltage = 33001;
    public const ushort Pack2Current = 33002;
    public const ushort Pack2Soc = 33005;

    // Inverter 1 (Slave 2)
    public const ushort Inverter1ABLineVoltage = 40007;
    public const ushort Inverter1BCLineVoltage = 40008;
    public const ushort Inverter1CALineVoltage = 40009;
    public const ushort Inverter1AcFrequency = 40016;
    public const ushort Inverter1ActivePower = 40018;
    public const ushort Inverter1DcVoltage = 40021;
    public const ushort Inverter1DcCurrent = 40022;
    public const ushort Inverter1WorkingMode = 40029;
    public const ushort Inverter1PowerOnOff = 40030;
    public const ushort Inverter1DcLinkVoltageSet = 40039;
    public const ushort Inverter1DcCurrentSet = 40040;
    public const ushort Inverter1AlarmStatus1 = 40032;
    public const ushort Inverter1AlarmStatus2 = 40033;

    // Inverter 2 (Slave 3)
    public const ushort Inverter2ABLineVoltage = 41007;
    public const ushort Inverter2BCLineVoltage = 41008;
    public const ushort Inverter2CALineVoltage = 41009;
    public const ushort Inverter2AcFrequency = 41016;
    public const ushort Inverter2ActivePower = 41018;
    public const ushort Inverter2DcVoltage = 41021;
    public const ushort Inverter2DcCurrent = 41022;
    public const ushort Inverter2WorkingMode = 41029;
    public const ushort Inverter2PowerOnOff = 41030;
    public const ushort Inverter2DcLinkVoltageSet = 41039;
    public const ushort Inverter2DcCurrentSet = 41040;
    public const ushort Inverter2AlarmStatus1 = 41032;
    public const ushort Inverter2AlarmStatus2 = 41033;
}
