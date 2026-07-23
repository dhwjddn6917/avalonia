using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MobileEssControl.Constants;

/// <summary>
/// EMS Slave 1의 Control ESS 영역(30001~30012) 주소입니다.
/// 현재 ModbusService는 주소맵의 Absolute Address를 그대로 사용합니다.
/// </summary>
public static class EmsControlAddresses
{
    // =========================================================
    // Control ESS
    // =========================================================

    // 30001 : 운전 모드 / Pack·Inverter 출력 허가 / System Run
    public const ushort ControlWord1 = 30001;

    // 30002 : 접촉기, 릴레이, 부저 등 수동 출력 제어
    // 일반 운전 화면에서는 사용하지 않고 관리자 화면에서만 사용 예정
    public const ushort ControlWord2 = 30002;

    // =========================================================
    // 운전 제한값
    // =========================================================

    // 30003 : 충전 최대 전압, 단위 10 mV
    public const ushort ChargingMaxLimitVoltage = 30003;

    // 30004 : 팩당 충전 최대 전류, 단위 0.01 A
    public const ushort ChargingMaxLimitCurrentPerPack = 30004;

    // 30005 : 방전 최소 전압, 단위 10 mV
    public const ushort DischargingMinLimitVoltage = 30005;

    // 30006 : 팩당 방전 최대 전류, 단위 0.01 A
    public const ushort DischargingMaxLimitCurrentPerPack = 30006;

    // 30007 : 설치 모듈 수
    public const ushort InstalledModuleCount = 30007;

    // 30008 : 충전 목표 SOC
    public const ushort TargetChargeSoc = 30008;

    // 30009 : 방전 목표 SOC
    public const ushort TargetDischargeSoc = 30009;

    // 30010 : 계통 충전 최대 전력, 단위 0.01 kW
    public const ushort AcMaxChargePowerOnGrid = 30010;

    // 30011 : 계통 방전 최대 전력, 단위 0.01 kW
    public const ushort AcMaxDischargePowerOnGrid = 30011;

    // 30012 : 오프그리드 방전 최대 전력, 단위 0.01 kW
    public const ushort AcMaxDischargePowerOffGrid = 30012;
}

/// <summary>
/// 30001의 Bit0~Bit2 운전 모드 값입니다.
/// </summary>
public enum EmsOperationMode : ushort
{
    Standby = 0,

    AutoCharge = 1,

    ManualControl = 2,

    ExternalOutput = 3,

    GridDischarge = 4,

    DcEvFastCharge = 5,

    DcEssFastCharge = 6,

    GridUpsDischarge = 7
}

/// <summary>
/// 30001 : Set EMS Digital Output1 Ctrl Bit 정의입니다.
/// </summary>
public static class EmsControlWord1
{
    // Bit0 ~ Bit2
    public const ushort ModeMask = 0x0007;

    // Bit3 ~ Bit6
    public const ushort BatteryPack1Power = 1 << 3;
    public const ushort BatteryPack2Power = 1 << 4;
    public const ushort Inverter1Output = 1 << 5;
    public const ushort Inverter2Output = 1 << 6;

    // Bit7 ~ Bit8
    // 현재 일반 ESS 화면에서는 사용하지 않음
    public const ushort DcDc1Output = 1 << 7;
    public const ushort DcDc2Output = 1 << 8;

    // Bit12 ~ Bit14
    public const ushort SystemRun = 1 << 12;
    public const ushort SystemOff = 1 << 13;
    public const ushort SystemResetEnable = 1 << 14;

    /// <summary>
    /// 일반 ESS 운전용 30001 명령값을 조합합니다.
    /// 기본값은 Pack1·Pack2·Inv1·Inv2 전체 사용입니다.
    /// </summary>
    public static ushort BuildRunCommand(
        EmsOperationMode mode,
        bool usePack1 = true,
        bool usePack2 = true,
        bool useInverter1 = true,
        bool useInverter2 = true)
    {
        ushort command = (ushort)mode;

        if (usePack1)
        {
            command |= BatteryPack1Power;
        }

        if (usePack2)
        {
            command |= BatteryPack2Power;
        }

        if (useInverter1)
        {
            command |= Inverter1Output;
        }

        if (useInverter2)
        {
            command |= Inverter2Output;
        }

        command |= SystemRun;

        return command;
    }
    /// <summary>
    /// 31022 System Status1 해석용
    /// </summary>
    public static class EmsSystemStatus1
    {
        // 31022 Bit0~2
        public const ushort SystemFaultLevelMask = 0x0007;

        // 31022 Bit3~5
        public const ushort OperatingModeMask = 0x0038;
        public const int OperatingModeShift = 3;

        public static ushort GetSystemFaultLevel(ushort value)
        {
            return (ushort)(value & SystemFaultLevelMask);
        }

        public static EmsOperationMode GetOperatingMode(ushort value)
        {
            return (EmsOperationMode)((value & OperatingModeMask) >> OperatingModeShift);
        }
    }

    /// <summary>
    /// 31023 System Status2 해석용
    /// </summary>
    public static class EmsSystemStatus2
    {
        // 31023 Bit13
        public const ushort SystemRunStop = 1 << 13;

        public static bool IsRunning(ushort value)
        {
            return (value & SystemRunStop) != 0;
        }
    }

    /// <summary>
    /// 31024 System Alarms1 해석용
    /// </summary>
    public static class EmsSystemAlarms1
    {
        public static ushort GetPack1FaultLevel(ushort value)
        {
            return (ushort)((value >> 0) & 0x0003);
        }

        public static ushort GetPack2FaultLevel(ushort value)
        {
            return (ushort)((value >> 2) & 0x0003);
        }

        public static ushort GetInverter1FaultLevel(ushort value)
        {
            return (ushort)((value >> 4) & 0x0003);
        }

        public static ushort GetInverter2FaultLevel(ushort value)
        {
            return (ushort)((value >> 6) & 0x0003);
        }

        public static bool HasPack1CanFault(ushort value)
        {
            return (value & (1 << 12)) != 0;
        }

        public static bool HasPack2CanFault(ushort value)
        {
            return (value & (1 << 13)) != 0;
        }

        public static bool HasInverter1CanFault(ushort value)
        {
            return (value & (1 << 14)) != 0;
        }

        public static bool HasInverter2CanFault(ushort value)
        {
            return (value & (1 << 15)) != 0;
        }

        public static bool HasPackOrInverterFault(ushort value)
        {
            return GetPack1FaultLevel(value) != 0 ||
                   GetPack2FaultLevel(value) != 0 ||
                   GetInverter1FaultLevel(value) != 0 ||
                   GetInverter2FaultLevel(value) != 0;
        }

        public static bool HasCanFault(ushort value)
        {
            return HasPack1CanFault(value) ||
                   HasPack2CanFault(value) ||
                   HasInverter1CanFault(value) ||
                   HasInverter2CanFault(value);
        }
    }
    /// <summary>
    /// 일반 운전 화면의 정지 명령입니다.
    /// SystemOff가 아닌 Standby 명령을 사용합니다.
    /// </summary>
    public static ushort BuildStandbyCommand()
    {
        return (ushort)EmsOperationMode.Standby;
    }

    /// <summary>
    /// 관리자 전용 시스템 종료 명령입니다.
    /// </summary>
    public static ushort BuildSystemOffCommand()
    {
        return SystemOff;
    }
}

/// <summary>
/// 30002 : Set EMS Digital Output2 Ctrl Bit 정의입니다.
/// 관리자 수동 제어용 출력이며, Read-Modify-Write로 변경해야
/// 선택하지 않은 비트와 Reserved 비트가 유지됩니다.
/// </summary>
public static class EmsControlWord2
{
    public const ushort Bms1ManualEnable = 1 << 0;
    public const ushort Bms2ManualEnable = 1 << 1;

    public const ushort EssChargeNegativeRelay = 1 << 2;
    public const ushort EssChargePositiveRelay = 1 << 3;

    // Bit4는 주소맵에서 Bit2와 중복 표기되어 있으므로 확인 전까지 사용하지 않습니다.
    public const ushort ReservedBit4 = 1 << 4;

    public const ushort EvChargePositiveRelay = 1 << 5;
    public const ushort EvChargeNegativeRelay = 1 << 6;

    public const ushort AcMainContactor = 1 << 7;
    public const ushort AcNeutralSwitch = 1 << 8;

    public const ushort AlarmLamp = 1 << 9;
    public const ushort FaultLamp = 1 << 10;
    public const ushort Buzzer = 1 << 11;

    // 관리자 화면에서 변경 가능한 Bit0~3, Bit5~11
    // Bit4와 Bit12~15는 기존값을 보존합니다.
    public const ushort EditableMask = 0x0FEF;
}
