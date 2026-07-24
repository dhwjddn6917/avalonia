using System.Collections.Generic;
using System.Linq;

namespace MobileEssControl.Services.Ems;

/// <summary>
/// EMS System Alarms 1(31039), System Alarms 2(31040)를
/// 화면 표시용 한글 알람 문구로 변환합니다.
/// </summary>
public static class EmsAlarmDecoder
{
    /// <summary>
    /// Dashboard의 AlarmStatus에 표시할 한 줄 요약입니다.
    /// </summary>
    public static string BuildSummary(
        ushort systemAlarm1,
        ushort systemAlarm2)
    {
        List<string> alarmItems =
            GetActiveAlarmItems(systemAlarm1, systemAlarm2);

        return alarmItems.Count == 0
            ? "정상"
            : string.Join(" · ", alarmItems);
    }

    /// <summary>
    /// 현재 발생 중인 시스템 알람 목록입니다.
    /// </summary>
    public static List<string> GetActiveAlarmItems(
        ushort systemAlarm1,
        ushort systemAlarm2)
    {
        List<string> items = new();

        // =====================================================
        // 31039 : System Alarms 1
        // =====================================================

        AddFaultLevel(
            items,
            "배터리 Pack 1",
            Get2BitValue(systemAlarm1, 0));

        AddFaultLevel(
            items,
            "배터리 Pack 2",
            Get2BitValue(systemAlarm1, 2));

        AddFaultLevel(
            items,
            "인버터 1",
            Get2BitValue(systemAlarm1, 4));

        AddFaultLevel(
            items,
            "인버터 2",
            Get2BitValue(systemAlarm1, 6));

        AddFaultLevel(
            items,
            "DC/DC 1",
            Get2BitValue(systemAlarm1, 8));

        AddFaultLevel(
            items,
            "DC/DC 2",
            Get2BitValue(systemAlarm1, 10));

        if (IsBitOn(systemAlarm1, 12))
        {
            items.Add("배터리 Pack 1 CAN 통신 이상");
        }

        if (IsBitOn(systemAlarm1, 13))
        {
            items.Add("배터리 Pack 2 CAN 통신 이상");
        }

        if (IsBitOn(systemAlarm1, 14))
        {
            items.Add("인버터 1 CAN 통신 이상");
        }

        if (IsBitOn(systemAlarm1, 15))
        {
            // 주소맵 원본에는 Bit14/15가 모두 Inverter1로 표기되어 있지만,
            // 구성상 Bit15는 Inverter2 CAN 통신 이상으로 해석합니다.
            items.Add("인버터 2 CAN 통신 이상");
        }

        // =====================================================
        // 31040 : System Alarms 2
        // =====================================================

        if (IsBitOn(systemAlarm2, 0))
        {
            items.Add("DC/DC 1 CAN 통신 이상");
        }

        if (IsBitOn(systemAlarm2, 1))
        {
            items.Add("DC/DC 2 CAN 통신 이상");
        }

        if (IsBitOn(systemAlarm2, 2))
        {
            items.Add("EVCC CAN 통신 이상");
        }

        if (IsBitOn(systemAlarm2, 3))
        {
            items.Add("EMU 통신 이상");
        }

        if (IsBitOn(systemAlarm2, 4))
        {
            items.Add("AC 메인 접촉기 제어 이상");
        }

        if (IsBitOn(systemAlarm2, 5))
        {
            items.Add("AC 메인 N 접촉기 제어 이상");
        }

        if (IsBitOn(systemAlarm2, 6))
        {
            items.Add("DC 충전 + 접촉기 이상");
        }

        if (IsBitOn(systemAlarm2, 7))
        {
            items.Add("DC 충전 - 접촉기 이상");
        }

        if (IsBitOn(systemAlarm2, 8))
        {
            items.Add("DC 방전 + 접촉기 이상");
        }

        if (IsBitOn(systemAlarm2, 9))
        {
            items.Add("DC 방전 - 접촉기 이상");
        }

        if (IsBitOn(systemAlarm2, 10))
        {
            items.Add("비상정지 이상");
        }

        return items;
    }

    /// <summary>
    /// Dashboard에 너무 긴 문장이 표시되지 않도록
    /// 앞의 몇 개 알람만 보여주는 요약 함수입니다.
    /// </summary>
    public static string BuildShortSummary(
        ushort systemAlarm1,
        ushort systemAlarm2,
        int maxItems = 2)
    {
        List<string> alarmItems =
            GetActiveAlarmItems(systemAlarm1, systemAlarm2);

        if (alarmItems.Count == 0)
        {
            return "정상";
        }

        if (alarmItems.Count <= maxItems)
        {
            return string.Join(" · ", alarmItems);
        }

        return $"{string.Join(" · ", alarmItems.Take(maxItems))} 외 {alarmItems.Count - maxItems}건";
    }

    /// <summary>
    /// 2Bit Fault Level 해석
    /// 0=정상, 1=경고, 2=고장, 3=보호
    /// </summary>
    private static void AddFaultLevel(
        List<string> items,
        string deviceName,
        int faultLevel)
    {
        string? levelText = faultLevel switch
        {
            0 => null,
            1 => "경고",
            2 => "고장",
            3 => "보호 동작",
            _ => "알 수 없음"
        };

        if (!string.IsNullOrWhiteSpace(levelText))
        {
            items.Add($"{deviceName} {levelText}");
        }
    }

    private static int Get2BitValue(
        ushort value,
        int startBit)
    {
        return (value >> startBit) & 0x0003;
    }

    private static bool IsBitOn(
        ushort value,
        int bitPosition)
    {
        return (value & (1 << bitPosition)) != 0;
    }
}