using System.Collections.Generic;

namespace MobileEssControl.Services.Ems;

/// <summary>
/// EMS System Status1(31037), System Status2(31038) Bit를
/// 화면 표시용 한글 문구로 변환합니다.
/// </summary>
public static class EmsStatusDecoder
{
    /// <summary>
    /// System Status1 Bit0~2
    /// 시스템 Fault Level을 해석합니다.
    /// </summary>
    public static string GetFaultLevelText(ushort systemStatus1)
    {
        int faultLevel = systemStatus1 & 0x0007;

        return faultLevel switch
        {
            0 => "정상",
            1 => "경고",
            2 => "고장",
            3 => "보호 동작",
            _ => "예약 상태"
        };
    }

    /// <summary>
    /// System Status1 Bit3~5
    /// 현재 EMS 운전 모드를 해석합니다.
    /// </summary>
    public static string GetOperationModeText(ushort systemStatus1)
    {
        int mode = (systemStatus1 >> 3) & 0x0007;

        return mode switch
        {
            0 => "대기",
            1 => "자동 충전",
            2 => "수동 제어",
            3 => "외부 전원 출력",
            4 => "계통 방전",
            5 => "DC 차량 급속충전",
            6 => "DC ESS 급속충전",
            7 => "계통 UPS 방전",
            _ => "알 수 없는 모드"
        };
    }

    /// <summary>
    /// System Status2 Bit13
    /// 시스템이 실제 운전 중인지 정지 상태인지 해석합니다.
    /// </summary>
    public static string GetRunStateText(ushort systemStatus2)
    {
        bool isRunning = IsBitOn(systemStatus2, 13);

        return isRunning
            ? "운전 중"
            : "정지";
    }

    /// <summary>
    /// Dashboard 상단 등에 표시할 짧은 요약 문구입니다.
    /// 예: 정상 · 외부 전원 출력 · 운전 중
    /// </summary>
    public static string BuildSummary(
        ushort systemStatus1,
        ushort systemStatus2)
    {
        string faultLevel = GetFaultLevelText(systemStatus1);
        string operationMode = GetOperationModeText(systemStatus1);
        string runState = GetRunStateText(systemStatus2);

        return $"{faultLevel} · {operationMode} · {runState}";
    }

    /// <summary>
    /// 현재 ON 상태인 주요 접촉기·출력 상태를 목록으로 만듭니다.
    /// 관리자 화면 상세 상태창에 사용합니다.
    /// </summary>
    public static List<string> GetActiveStatusItems(
        ushort systemStatus1,
        ushort systemStatus2)
    {
        List<string> items = new();

        // =====================================================
        // System Status1 : 31037
        // =====================================================

        if (IsBitOn(systemStatus1, 6))
        {
            items.Add("배터리 Pack 1 고전압 접촉기 ON");
        }

        if (IsBitOn(systemStatus1, 7))
        {
            items.Add("배터리 Pack 2 고전압 접촉기 ON");
        }

        if (IsBitOn(systemStatus1, 8))
        {
            items.Add("BMS 1 메인 출력 허가 ON");
        }

        if (IsBitOn(systemStatus1, 9))
        {
            items.Add("BMS 2 메인 출력 허가 ON");
        }

        if (IsBitOn(systemStatus1, 10))
        {
            items.Add("배터리 Pack 1 프리차지 ON");
        }

        if (IsBitOn(systemStatus1, 11))
        {
            items.Add("배터리 Pack 2 프리차지 ON");
        }

        if (IsBitOn(systemStatus1, 12))
        {
            items.Add("알람 램프 ON");
        }

        if (IsBitOn(systemStatus1, 13))
        {
            items.Add("고장 램프 ON");
        }

        if (IsBitOn(systemStatus1, 14))
        {
            items.Add("알람 부저 ON");
        }

        if (IsBitOn(systemStatus1, 15))
        {
            items.Add("AC 메인 접촉기 ON");
        }

        // =====================================================
        // System Status2 : 31038
        // =====================================================

        if (IsBitOn(systemStatus2, 0))
        {
            items.Add("AC 메인 N 접촉기 ON");
        }

        if (IsBitOn(systemStatus2, 2))
        {
            items.Add("DC 충전 고전압 + 접촉기 ON");
        }

        if (IsBitOn(systemStatus2, 3))
        {
            items.Add("DC 충전 고전압 - 접촉기 ON");
        }

        if (IsBitOn(systemStatus2, 4))
        {
            items.Add("DC 방전 고전압 + 접촉기 ON");
        }

        if (IsBitOn(systemStatus2, 5))
        {
            items.Add("DC 방전 고전압 - 접촉기 ON");
        }

        if (IsBitOn(systemStatus2, 6))
        {
            items.Add("인버터 1 출력 ON");
        }

        if (IsBitOn(systemStatus2, 7))
        {
            items.Add("인버터 2 출력 ON");
        }

        if (IsBitOn(systemStatus2, 8))
        {
            items.Add("DC/DC 1 출력 ON");
        }

        if (IsBitOn(systemStatus2, 9))
        {
            items.Add("DC/DC 2 출력 ON");
        }

        if (IsBitOn(systemStatus2, 10))
        {
            items.Add("비상정지 스위치 입력 ON");
        }

        if (IsBitOn(systemStatus2, 11))
        {
            items.Add("전원 스위치 ON");
        }

        if (IsBitOn(systemStatus2, 14))
        {
            items.Add("시스템 종료 요청 상태");
        }

        if (IsBitOn(systemStatus2, 15))
        {
            items.Add("시스템 리셋 진행 중");
        }

        if (items.Count == 0)
        {
            items.Add("활성 출력 및 접촉기 없음");
        }

        return items;
    }

    private static bool IsBitOn(
        ushort value,
        int bitPosition)
    {
        return (value & (1 << bitPosition)) != 0;
    }
}