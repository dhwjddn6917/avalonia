using System;

namespace MobileEssControl.Models.System;

public class EssStatusData
{
    // EMS 통합 상태
    public double Soc { get; set; }

    public double BatteryVoltage { get; set; }

    public double BatteryCurrent { get; set; }

    public ushort SystemStatus1 { get; set; }

    public ushort SystemStatus2 { get; set; }

    public ushort AlarmStatus1 { get; set; }

    public ushort AlarmStatus2 { get; set; }

    // 인버터 1
    public double Inverter1PowerKw { get; set; }

    // 40001/40003/40005: A/B/C상 전압
    public double Inverter1PhaseAVoltage { get; set; }

    public double Inverter1PhaseBVoltage { get; set; }

    public double Inverter1PhaseCVoltage { get; set; }

    // 기존 Inverter1Voltage는 AB 선간전압입니다.
    public double Inverter1Voltage { get; set; }

    public double Inverter1BcVoltage { get; set; }

    public double Inverter1CaVoltage { get; set; }

    public double Inverter1PhaseACurrent { get; set; }

    public double Inverter1PhaseBCurrent { get; set; }

    public double Inverter1PhaseCCurrent { get; set; }

    public double Inverter1Frequency { get; set; }

    public double Inverter1DcVoltage { get; set; }

    public double Inverter1DcCurrent { get; set; }

    public ushort Inverter1WorkingMode { get; set; }

    public ushort Inverter1PowerOnOff { get; set; }

    // 인버터 2
    public double Inverter2PowerKw { get; set; }

    // 41001/41003/41005: A/B/C상 전압
    public double Inverter2PhaseAVoltage { get; set; }

    public double Inverter2PhaseBVoltage { get; set; }

    public double Inverter2PhaseCVoltage { get; set; }

    // 기존 Inverter2Voltage는 AB 선간전압입니다.
    public double Inverter2Voltage { get; set; }

    public double Inverter2BcVoltage { get; set; }

    public double Inverter2CaVoltage { get; set; }

    public double Inverter2PhaseACurrent { get; set; }

    public double Inverter2PhaseBCurrent { get; set; }

    public double Inverter2PhaseCCurrent { get; set; }

    public double Inverter2Frequency { get; set; }

    public double Inverter2DcVoltage { get; set; }

    public double Inverter2DcCurrent { get; set; }

    public ushort Inverter2WorkingMode { get; set; }

    public ushort Inverter2PowerOnOff { get; set; }

    // 화면에서 바로 쓰기 위한 합산값
    public double TotalPowerKw =>
        Inverter1PowerKw + Inverter2PowerKw;

    public double AcVoltage =>
        Inverter1Voltage > 0
            ? Inverter1Voltage
            : Inverter2Voltage;

    public double Frequency =>
        Inverter1Frequency > 0
            ? Inverter1Frequency
            : Inverter2Frequency;

    // 40029/41029: 0 = Grid-connected
    // 40030/41030: 0 = Startup, 1 = Shutdown
    public bool IsInverter1ParticipatingInGridDischarge =>
        Inverter1WorkingMode == 0 &&
        Inverter1PowerOnOff == 0;

    public bool IsInverter2ParticipatingInGridDischarge =>
        Inverter2WorkingMode == 0 &&
        Inverter2PowerOnOff == 0;

    // 계통방전 대표값
    // 전압/주파수는 참여 인버터의 평균, 전류는 참여 인버터의 합계입니다.
    public double? RepresentativeAbVoltage =>
        AverageParticipatingValues(
            Inverter1Voltage,
            IsInverter1ParticipatingInGridDischarge,
            Inverter2Voltage,
            IsInverter2ParticipatingInGridDischarge);

    public double? RepresentativeBcVoltage =>
        AverageParticipatingValues(
            Inverter1BcVoltage,
            IsInverter1ParticipatingInGridDischarge,
            Inverter2BcVoltage,
            IsInverter2ParticipatingInGridDischarge);

    public double? RepresentativeCaVoltage =>
        AverageParticipatingValues(
            Inverter1CaVoltage,
            IsInverter1ParticipatingInGridDischarge,
            Inverter2CaVoltage,
            IsInverter2ParticipatingInGridDischarge);

    public double? RepresentativePhaseACurrent =>
        SumParticipatingValues(
            Inverter1PhaseACurrent,
            IsInverter1ParticipatingInGridDischarge,
            Inverter2PhaseACurrent,
            IsInverter2ParticipatingInGridDischarge);

    public double? RepresentativePhaseBCurrent =>
        SumParticipatingValues(
            Inverter1PhaseBCurrent,
            IsInverter1ParticipatingInGridDischarge,
            Inverter2PhaseBCurrent,
            IsInverter2ParticipatingInGridDischarge);

    public double? RepresentativePhaseCCurrent =>
        SumParticipatingValues(
            Inverter1PhaseCCurrent,
            IsInverter1ParticipatingInGridDischarge,
            Inverter2PhaseCCurrent,
            IsInverter2ParticipatingInGridDischarge);

    public double? RepresentativeFrequency =>
        AverageParticipatingValues(
            Inverter1Frequency,
            IsInverter1ParticipatingInGridDischarge,
            Inverter2Frequency,
            IsInverter2ParticipatingInGridDischarge);

    private static double? AverageParticipatingValues(
        double inverter1Value,
        bool inverter1Participating,
        double inverter2Value,
        bool inverter2Participating)
    {
        double total = 0;
        int count = 0;

        if (inverter1Participating && IsFinite(inverter1Value))
        {
            total += inverter1Value;
            count++;
        }

        if (inverter2Participating && IsFinite(inverter2Value))
        {
            total += inverter2Value;
            count++;
        }

        return count > 0
            ? total / count
            : null;
    }

    private static double? SumParticipatingValues(
        double inverter1Value,
        bool inverter1Participating,
        double inverter2Value,
        bool inverter2Participating)
    {
        double total = 0;
        bool hasParticipatingInverter = false;

        if (inverter1Participating && IsFinite(inverter1Value))
        {
            total += inverter1Value;
            hasParticipatingInverter = true;
        }

        if (inverter2Participating && IsFinite(inverter2Value))
        {
            total += inverter2Value;
            hasParticipatingInverter = true;
        }

        return hasParticipatingInverter
            ? total
            : null;
    }

    private static bool IsFinite(double value)
    {
        return
            !double.IsNaN(value) &&
            !double.IsInfinity(value);
    }
}
