namespace MobileEssControl.Constants;

public static class SlaveIds
{
    public const byte Ems = 1;
    public const byte Inverter1 = 2;
    public const byte Inverter2 = 3;
    public const byte BatteryPack1 = 6;
    public const byte BatteryPack2 = 7;
}

public static class EmsOffsets
{
    // Complete ESS Information 시작 주소 = 31001

    public const ushort SystemVoltage = 10;  // 31011
    public const ushort SystemCurrent = 11;  // 31012
    public const ushort SystemSoc = 16;      // 31017

    public const ushort SystemStatus1 = 21;  // 31022
    public const ushort SystemStatus2 = 22;  // 31023

    public const ushort SystemAlarm1 = 23;   // 31024
    public const ushort SystemAlarm2 = 24;   // 31025
}

public static class InverterOffsets
{
    // 40001~40006 / 41001~41006
    public const ushort APhaseVoltage = 0;
    public const ushort APhaseCurrent = 1;
    public const ushort BPhaseVoltage = 2;
    public const ushort BPhaseCurrent = 3;
    public const ushort CPhaseVoltage = 4;
    public const ushort CPhaseCurrent = 5;

    // 40007~40009 / 41007~41009
    public const ushort AbLineVoltage = 6;
    public const ushort BcLineVoltage = 7;
    public const ushort CaLineVoltage = 8;

    // 40016 / 41016
    public const ushort AcFrequency = 15;

    // 40018 / 41018
    public const ushort TotalActivePower = 17;

    // 40021 / 41021
    public const ushort DcVoltage = 20;

    // 40022 / 41022
    public const ushort DcCurrent = 21;

    // 40029 / 41029
    public const ushort WorkingMode = 28;

    // 40030 / 41030
    // 0 = Start, 1 = Shutdown
    public const ushort PowerOnOff = 29;

    // 40039 / 41039
    public const ushort DcLinkVoltageSet = 38;

    // 40040 / 41040
    public const ushort DcCurrentSet = 39;

    // 40042 / 41042
    public const ushort AcActivePowerSet = 41;
}
