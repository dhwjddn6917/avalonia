using System;

namespace MobileEssControl.Models.Admin;

/// <summary>
/// MobileESS Address Map의 Name / Remark 기준으로 Bit Field를 표시합니다.
/// AdminView.axaml의 DisplayValue는 고정폭 글꼴(Consolas 등)을 사용해야 ':' 위치가 맞습니다.
/// </summary>
public static class AdminBitFieldDecoder
{
    // 가장 긴 인버터 Alarm 항목까지 ':' 위치를 최대한 맞추기 위한 폭입니다.
    private const int RemarkNameWidth = 46;

    // =====================================================
    // System Status
    // =====================================================

    /// <summary>
    /// 31022 - System Status1
    /// </summary>
    public static string DecodeSystemStatus1(ushort raw)
    {
        int systemFaultLevel = GetBits(raw, 0, 3);
        int operatingMode = GetBits(raw, 3, 3);

        return JoinLines(
            FormatRemarkLine("System Fault Level", GetSystemFaultLevelRemark(systemFaultLevel)),
            FormatRemarkLine("Operating Mode", GetOperatingModeRemark(operatingMode)),
            FormatRemarkLine("Pack1_HvConSts", GetBitRemark(raw, 6, "Off", "On")),
            FormatRemarkLine("Pack2_HvConSts", GetBitRemark(raw, 7, "Off", "On")),
            FormatRemarkLine("Bms1Menable", GetBitRemark(raw, 8, "Off", "On")),
            FormatRemarkLine("Bms2Menable", GetBitRemark(raw, 9, "Off", "On")),
            FormatRemarkLine("LampAlarm", GetBitRemark(raw, 12, "Off", "On")),
            FormatRemarkLine("LampFault", GetBitRemark(raw, 13, "Off", "On")),
            FormatRemarkLine("LampOutput", GetBitRemark(raw, 14, "Off", "On")));
    }

    /// <summary>
    /// 31023 - System Status2
    /// </summary>
    public static string DecodeSystemStatus2(ushort raw)
    {
        return JoinLines(
            FormatRemarkLine("AcMc1_Status", GetBitRemark(raw, 0, "Off", "On")),
            FormatRemarkLine("AcMc1_Nstatus", GetBitRemark(raw, 1, "Off", "On")),
            FormatRemarkLine("DcQchgHvPConSts", GetBitRemark(raw, 2, "Off", "On")),
            FormatRemarkLine("DcQchgHvNConSts", GetBitRemark(raw, 3, "Off", "On")),
            FormatRemarkLine("DcDchgHvPConSts", GetBitRemark(raw, 4, "Off", "On")),
            FormatRemarkLine("DcDchgHvNConSts", GetBitRemark(raw, 5, "Off", "On")),
            FormatRemarkLine("DcAcInv1Sts", GetBitRemark(raw, 6, "Off", "On")),
            FormatRemarkLine("DcAcInv2Sts", GetBitRemark(raw, 7, "Off", "On")),
            FormatRemarkLine("DcDc1Sts", GetBitRemark(raw, 8, "Off", "On")),
            FormatRemarkLine("DcDc2Sts", GetBitRemark(raw, 9, "Off", "On")),
            FormatRemarkLine("EmStopSwitchSts", GetBitRemark(raw, 10, "Off", "On")),
            FormatRemarkLine("PowerSwitchSts", GetBitRemark(raw, 11, "Off", "On")),
            FormatRemarkLine("System ReadyEn", GetBitRemark(raw, 12, "Disable", "Enable")),
            FormatRemarkLine("System RunStop", GetBitRemark(raw, 13, "Stop", "Run")),
            FormatRemarkLine("System OffEn", GetBitRemark(raw, 14, "Disable", "Enable")),
            FormatRemarkLine("System_ResetStatus", GetBitRemark(raw, 15, "Standby", "Resetting")));
    }

    // =====================================================
    // System Alarms
    // =====================================================

    /// <summary>
    /// 31024 - System Alarms 1
    /// </summary>
    public static string DecodeSystemAlarm1(ushort raw)
    {
        return JoinLines(
            FormatRemarkLine("Pack1 Fault Level", GetFaultLevelRemark(GetBits(raw, 0, 2))),
            FormatRemarkLine("Pack2 Fault Level", GetFaultLevelRemark(GetBits(raw, 2, 2))),
            FormatRemarkLine("Inverter 1 Fault Level", GetFaultLevelRemark(GetBits(raw, 4, 2))),
            FormatRemarkLine("Inverter 2 Fault Level", GetFaultLevelRemark(GetBits(raw, 6, 2))),
            FormatRemarkLine("DcDc 1 Fault Level", GetFaultLevelRemark(GetBits(raw, 8, 2))),
            FormatRemarkLine("DcDc 2 Fault Level", GetFaultLevelRemark(GetBits(raw, 10, 2))),
            FormatRemarkLine("Pack1 CAN Com Fault", GetBitRemark(raw, 12, "Normal", "Fault")),
            FormatRemarkLine("Pack2 CAN Com Fault", GetBitRemark(raw, 13, "Normal", "Fault")),
            FormatRemarkLine("Inverter1 CAN Com Fault", GetBitRemark(raw, 14, "Normal", "Fault")),
            // 문서에 Bit14/15가 모두 Inverter1로 적혀 있으나, 구성상 Bit15는 Inverter2로 표시합니다.
            FormatRemarkLine("Inverter2 CAN Com Fault", GetBitRemark(raw, 15, "Normal", "Fault")));
    }

    /// <summary>
    /// 31025 - System Alarms 2
    /// </summary>
    public static string DecodeSystemAlarm2(ushort raw)
    {
        return JoinLines(
            FormatRemarkLine("DcDc 1 CAN Com Fault", GetBitRemark(raw, 0, "Normal", "Fault")),
            FormatRemarkLine("DcDc 2 CAN Com Fault", GetBitRemark(raw, 1, "Normal", "Fault")),
            FormatRemarkLine("EVCC CAN Com Fault", GetBitRemark(raw, 2, "Normal", "Fault")),
            FormatRemarkLine("EMU Com Fault", GetBitRemark(raw, 3, "Normal", "Fault")),
            FormatRemarkLine("AcMc1_Ctrl Fault", GetBitRemark(raw, 4, "Normal", "Fault")),
            FormatRemarkLine("AcMc1_N_Ctrl Fault", GetBitRemark(raw, 5, "Normal", "Fault")),
            FormatRemarkLine("DcQchgHvPConFault", GetBitRemark(raw, 6, "Normal", "Fault")),
            FormatRemarkLine("DcQchgHvNConFault", GetBitRemark(raw, 7, "Normal", "Fault")),
            FormatRemarkLine("DcDchgHvPConFault", GetBitRemark(raw, 8, "Normal", "Fault")),
            FormatRemarkLine("DcDchgHvNConFault", GetBitRemark(raw, 9, "Normal", "Fault")),
            FormatRemarkLine("EmStopFault", GetBitRemark(raw, 10, "Normal", "Fault")),
            FormatRemarkLine("Fault_OffGrid_OutputVoltageDet", GetBitRemark(raw, 11, "Normal", "Fault")),
            FormatRemarkLine("Fault_Reserved2", GetBitRemark(raw, 12, "Normal", "Fault")),
            FormatRemarkLine("Fault_Reserved3", GetBitRemark(raw, 13, "Normal", "Fault")),
            FormatRemarkLine("Fault_Reserved4", GetBitRemark(raw, 14, "Normal", "Fault")),
            FormatRemarkLine("Fault_Reserved5", GetBitRemark(raw, 15, "Normal", "Fault")));
    }

    // =====================================================
    // Battery Pack Alarms
    // Pack 1: 31026~31028, Pack 2: 31029~31031
    // 두 Pack은 같은 규칙을 사용합니다.
    // =====================================================

    /// <summary>
    /// Battery PackX Alarms 1
    /// </summary>
    public static string DecodePack1Alarm1(ushort raw)
    {
        return JoinLines(
            FormatRemarkLine("Iso Fault Level", GetIsoFaultLevelRemark(GetBits(raw, 0, 3))),
            FormatRemarkLine("PreChgFault", GetBitRemark(raw, 3, "Normal", "Fault")),
            FormatRemarkLine("PosRlyFault", GetBitRemark(raw, 4, "Normal", "Fault")),
            FormatRemarkLine("NegRlyFault", GetBitRemark(raw, 5, "Normal", "Fault")),
            FormatRemarkLine("PreChgRlyFault", GetBitRemark(raw, 6, "Normal", "Fault")),
            FormatRemarkLine("ChgerRlyFault", GetBitRemark(raw, 7, "Normal", "Fault")),
            FormatRemarkLine("Low Temp Fault", GetFaultLevelRemark(GetBits(raw, 8, 2))),
            FormatRemarkLine("High Temp Fault", GetFaultLevelRemark(GetBits(raw, 10, 2))),
            FormatRemarkLine("Delta Temp Fault", GetFaultLevelRemark(GetBits(raw, 12, 2))),
            FormatRemarkLine("HeaterRly Fault", GetBitRemark(raw, 14, "Normal", "Fault")),
            FormatRemarkLine("HeatDiffusion Fault", GetBitRemark(raw, 15, "Normal", "Fault")));
    }

    /// <summary>
    /// Battery PackX Alarms 2
    /// </summary>
    public static string DecodePack1Alarm2(ushort raw)
    {
        return JoinLines(
            FormatRemarkLine("OverChg Fault", GetFaultLevelRemark(GetBits(raw, 0, 2))),
            FormatRemarkLine("OverDchg Fault", GetFaultLevelRemark(GetBits(raw, 2, 2))),
            FormatRemarkLine("Cell High Volt Fault", GetFaultLevelRemark(GetBits(raw, 4, 2))),
            FormatRemarkLine("Cell Low Volt Fault", GetFaultLevelRemark(GetBits(raw, 6, 2))),
            FormatRemarkLine("Cell Delta Volt Fault", GetFaultLevelRemark(GetBits(raw, 8, 2))),
            FormatRemarkLine("Pack High Volt Fault", GetFaultLevelRemark(GetBits(raw, 10, 2))),
            FormatRemarkLine("Pack Low Volt Fault", GetFaultLevelRemark(GetBits(raw, 12, 2))),
            FormatRemarkLine("gun/BlockTempOver Fault", GetFaultLevelRemark(GetBits(raw, 14, 2))));
    }

    /// <summary>
    /// Battery PackX Alarms 3
    /// </summary>
    public static string DecodePack1Alarm3(ushort raw)
    {
        return JoinLines(
            FormatRemarkLine("HSOC Fault", GetFaultLevelRemark(GetBits(raw, 0, 2))),
            FormatRemarkLine("AuxBatt Fault", GetBitRemark(raw, 2, "Normal", "Fault")),
            FormatRemarkLine("Slave CAN Fault", GetFaultLevelRemark(GetBits(raw, 3, 2))),
            FormatRemarkLine("VCU CAN Fault", GetFaultLevelRemark(GetBits(raw, 5, 2))),
            FormatRemarkLine("Charger CAN Fault", GetBitRemark(raw, 7, "Normal", "Fault")),
            FormatRemarkLine("CoolSys Fault", GetBitRemark(raw, 8, "Normal", "Fault")),
            FormatRemarkLine("HeatSys Fault", GetBitRemark(raw, 9, "Normal", "Fault")),
            FormatRemarkLine("CellVoltSensor Fault", GetBitRemark(raw, 10, "Normal", "Fault")),
            FormatRemarkLine("CurrentSensor Fault", GetBitRemark(raw, 11, "Normal", "Fault")),
            FormatRemarkLine("TempSensor Fault", GetBitRemark(raw, 12, "Normal", "Fault")),
            FormatRemarkLine("PackVoltSensor Fault", GetBitRemark(raw, 13, "Normal", "Fault")),
            FormatRemarkLine("MoisSensor Fault", GetBitRemark(raw, 14, "Normal", "Fault")),
            FormatRemarkLine("HVIL Fault", GetBitRemark(raw, 15, "Normal", "Fault")));
    }

    // =====================================================
    // Battery Pack Status
    // Pack 1: 31032 / 31033, Pack 2: 31034 / 31035
    // =====================================================

    /// <summary>
    /// Battery PackX Status 1
    /// </summary>
    public static string DecodePackStatus1(ushort raw)
    {
        return JoinLines(
            FormatRemarkLine("AC_CC", GetBitRemark(raw, 0, "Disable", "Enable")),
            FormatRemarkLine("DC_CC", GetBitRemark(raw, 1, "Disable", "Enable")),
            FormatRemarkLine("BmsMode", GetBitRemark(raw, 2, "Discharge", "Charge")),
            FormatRemarkLine("SysFltLvl", GetFaultLevelRemark(GetBits(raw, 3, 3))),
            FormatRemarkLine("ChgGeneStatus", GetChargeGeneStatusRemark(GetBits(raw, 6, 2))),
            FormatRemarkLine("PreChgSts", GetBitRemark(raw, 8, "None", "Done")),
            FormatRemarkLine("HvConSts", GetBitRemark(raw, 9, "Off", "On")),
            FormatRemarkLine("SelfChkSts", GetSelfCheckStatusRemark(GetBits(raw, 10, 2))),
            FormatRemarkLine("HeatRlySts", GetBitRemark(raw, 12, "Off", "On")),
            FormatRemarkLine("ChgSts", GetChargeStatusRemark(GetBits(raw, 13, 2))),
            FormatRemarkLine("PosRlySts", GetBitRemark(raw, 15, "Off", "On")));
    }

    /// <summary>
    /// Battery PackX Status 2
    /// Reserved1~Reserved11은 의미 있는 Remark 값이 없어 표시하지 않습니다.
    /// </summary>
    public static string DecodePackStatus2(ushort raw)
    {
        return JoinLines(
            FormatRemarkLine("PreChgRlySts", GetBitRemark(raw, 0, "Off", "On")),
            FormatRemarkLine("NegRlySts", GetBitRemark(raw, 1, "Off", "On")),
            FormatRemarkLine("ChargerRlySts", GetBitRemark(raw, 2, "Off", "On")),
            FormatRemarkLine("BatteryType", GetBatteryTypeRemark(GetBits(raw, 3, 2))));
    }

    // =====================================================
    // Inverter Alarm Status
    // Inverter 1: 40032 / 40033, Inverter 2: 41032 / 41033
    // =====================================================

    /// <summary>
    /// InvX AlarmStatus 1
    /// </summary>
    public static string DecodeInverterAlarmStatus1(ushort raw)
    {
        return JoinLines(
            FormatRemarkLine("Module failure (red indicator light on)", GetBitRemark(raw, 0, "Normal", "Failure")),
            FormatRemarkLine("Module protection (yellow indicator light on)", GetBitRemark(raw, 1, "Normal", "Protection")),
            FormatRemarkLine("AC input phase loss", GetBitRemark(raw, 2, "Normal", "Phase loss")),
            FormatRemarkLine("Module internal SCI communication failure", GetBitRemark(raw, 3, "Normal", "Failure")),
            FormatRemarkLine("The AC side wiring is wrong phase", GetBitRemark(raw, 4, "Normal", "Failure")),
            FormatRemarkLine("Islanding alarm", GetBitRemark(raw, 5, "Normal", "Islanding")),
            FormatRemarkLine("Internal busbar over-voltage or under-voltage", GetBitRemark(raw, 6, "Normal", "Failure")),
            FormatRemarkLine("AC side undervoltage", GetBitRemark(raw, 7, "Normal", "Failure")),
            FormatRemarkLine("AC side overvoltage", GetBitRemark(raw, 8, "Normal", "Failure")),
            FormatRemarkLine("DC side overvoltage", GetBitRemark(raw, 9, "Normal", "Failure")),
            FormatRemarkLine("DC side undervoltage", GetBitRemark(raw, 10, "Normal", "Failure")),
            FormatRemarkLine("Phase lock error", GetBitRemark(raw, 11, "Normal", "Error")),
            FormatRemarkLine("Module working mode", GetInverterWorkingModeRemark(GetBits(raw, 12, 2))),
            FormatRemarkLine("U1 Overcurrent protection", GetBitRemark(raw, 14, "Normal", "Protection")),
            FormatRemarkLine("Fan failure", GetBitRemark(raw, 15, "Normal", "Failure")));
    }

    /// <summary>
    /// InvX AlarmStatus 2
    /// </summary>
    public static string DecodeInverterAlarmStatus2(ushort raw)
    {
        return JoinLines(
            FormatRemarkLine("CAN communication failure", GetBitRemark(raw, 0, "Normal", "Failure")),
            FormatRemarkLine("Unbalanced current between modules", GetBitRemark(raw, 1, "Normal", "Failure")),
            FormatRemarkLine("Duplicate address", GetBitRemark(raw, 2, "Normal", "Failure")),
            FormatRemarkLine("N line connected incorrectly", GetBitRemark(raw, 3, "Normal", "Failure")),
            FormatRemarkLine("Discharge failure", GetBitRemark(raw, 4, "Normal", "Failure")),
            FormatRemarkLine("U1 power on/off status", GetBitRemark(raw, 5, "power on", "power off")),
            FormatRemarkLine("U2 power on/off status", GetBitRemark(raw, 6, "power on", "power off")),
            FormatRemarkLine("Module power limit", GetBitRemark(raw, 7, "Disable", "Enable")),
            FormatRemarkLine("Temperature limit power", GetBitRemark(raw, 8, "Disable", "Enable")),
            FormatRemarkLine("AC power limit", GetBitRemark(raw, 9, "Disable", "Enable")),
            FormatRemarkLine("AC side underfrequency", GetBitRemark(raw, 10, "Normal", "Failure")),
            FormatRemarkLine("AC side overfrequency", GetBitRemark(raw, 11, "Normal", "Failure")),
            FormatRemarkLine("DC side short circuit", GetBitRemark(raw, 12, "Normal", "Failure")),
            FormatRemarkLine("Air duct overheating", GetBitRemark(raw, 13, "Normal", "Failure")),
            FormatRemarkLine("Module over temperature", GetBitRemark(raw, 14, "Normal", "Failure")),
            FormatRemarkLine("Ambient temperature over temperature", GetBitRemark(raw, 15, "Normal", "Failure")));
    }

    // =====================================================
    // Helper
    // =====================================================

    private static string JoinLines(params string[] lines)
    {
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatRemarkLine(string name, string value)
    {
        return $"{name.PadRight(RemarkNameWidth)} : {value}";
    }

    private static int GetBits(ushort raw, int bitPosition, int dataLength)
    {
        int mask = (1 << dataLength) - 1;
        return (raw >> bitPosition) & mask;
    }

    private static string GetBitRemark(ushort raw, int bitPosition, string zeroRemark, string oneRemark)
    {
        bool isSet = (raw & (1 << bitPosition)) != 0;
        return isSet ? oneRemark : zeroRemark;
    }

    private static string GetSystemFaultLevelRemark(int value)
    {
        return value switch
        {
            0 => "Normal",
            1 => "Warning",
            2 => "Fault",
            3 => "Protect",
            _ => "Reserved"
        };
    }

    private static string GetFaultLevelRemark(int value)
    {
        return value switch
        {
            0 => "Normal",
            1 => "Warning",
            2 => "Fault",
            3 => "Protect",
            _ => "Reserved"
        };
    }

    private static string GetIsoFaultLevelRemark(int value)
    {
        return value switch
        {
            0 => "Normal",
            1 => "Warning",
            2 => "Fault",
            3 => "Protect",
            4 => "Emergency",
            _ => "Reserved"
        };
    }

    private static string GetOperatingModeRemark(int value)
    {
        return value switch
        {
            0 => "Standby",
            1 => "AC 자동 충전",
            2 => "수동 제어",
            3 => "AC 외부 전원출력",
            4 => "계통 방전",
            5 => "DC 차량 급속충전",
            6 => "DC ESS 급속충전",
            7 => "계통 UPS방전",
            _ => "Reserved"
        };
    }

    private static string GetChargeGeneStatusRemark(int value)
    {
        return value switch
        {
            0 => "Plug Out",
            1 => "Plug In",
            2 => "Charing",
            3 => "Charge Done",
            _ => "Reserved"
        };
    }

    private static string GetSelfCheckStatusRemark(int value)
    {
        return value switch
        {
            0 => "SelfTesting",
            1 => "SelfTestDone",
            2 => "SelfTestFail",
            3 => "Reserved",
            _ => "Reserved"
        };
    }

    private static string GetChargeStatusRemark(int value)
    {
        return value switch
        {
            0 => "SOC < 20[%]",
            1 => "SOC < 80[%]",
            2 => "SOC ≥ 80[%]",
            3 => "No Charge",
            _ => "Reserved"
        };
    }

    private static string GetBatteryTypeRemark(int value)
    {
        return value switch
        {
            0 => "LiFePo4",
            1 => "Li-ion(LMO)",
            2 => "Li-ion(NCA)",
            3 => "Li-ion(NCM)",
            _ => "Reserved"
        };
    }

    private static string GetInverterWorkingModeRemark(int value)
    {
        return value switch
        {
            0 => "Grid-connected",
            1 => "Off-grid",
            2 => "Rectifier",
            3 => "Reserved",
            _ => "Reserved"
        };
    }
}
