using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MobileEssControl.Constants;
using MobileEssControl.Models.Admin;
using MobileEssControl.Services.Ems;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MobileEssControl.ViewModels.Admin;

/// <summary>
/// MobileESS 주소맵의 상태 레지스터를 Poll하여 표시하는 관리자 화면입니다.
/// 시스템 EMS/배터리/인버터 탭은 상태 확인용이며, ESS 제어 탭은 30001~30012 Write를 지원합니다.
/// </summary>
public partial class AdminViewModel : ViewModelBase, IDisposable
{
    // 관리자 EMS 배열은 Relative Address 0부터 읽습니다.
    // Relative 0 = Absolute 31001
    private const ushort EmsStartAddress = 0;
    private const ushort PackStartAddress = 0;
    private const ushort InverterStartAddress = 0;

    private readonly Action? _exitAdminAction;

    private readonly EmsService _emsService;
    private readonly DispatcherTimer _refreshTimer;
    private bool _isRefreshing;
    private bool _isConnectionChanging;
    private bool _disposed;

    public ObservableCollection<AdminRegisterRow> SystemRows { get; } = new();

    // Pack 1 / Pack 2는 합산하지 않고 같은 줄에서 비교 표시합니다.
    public ObservableCollection<AdminComparisonRow> BatteryRows { get; } = new();

    // Inverter 1 / Inverter 2도 같은 항목끼리 비교 표시합니다.
    public ObservableCollection<AdminComparisonRow> InverterRows { get; } = new();

    // EEP Parameter 스타일의 관리자 제어 테이블입니다.
    // R.Value는 FC03 Read 결과, W.Value는 사용자가 입력한 Write 값입니다.
    public ObservableCollection<AdminControlTableRow> EssControlTableRows { get; } = new();
    public ObservableCollection<AdminControlTableRow> InverterControlTableRows { get; } = new();

    // 제어표는 30001처럼 같은 Register 안에 여러 Bit 행이 있습니다.
    // 같은 주소를 행마다 계속 Read하면 EMS 반영 타이밍 때문에 값이 흔들릴 수 있으므로,
    // 마지막으로 한 번 읽은 전체 Register 값을 주소별로 보관하고 Write의 기준값으로 사용합니다.
    private readonly Dictionary<ushort, ushort> _controlTableShadowRawByAddress = new();

    private readonly StringBuilder _communicationLogBuilder = new();

    private string _communicationLogText = string.Empty;
    public string CommunicationLogText
    {
        get => _communicationLogText;
        set
        {
            _communicationLogText = value;
            OnPropertyChanged();
        }
    }

    public ICommand ClearLogCommand { get; }


    [ObservableProperty]
    private string communicationText = "통신 확인 중";

    [ObservableProperty]
    private string lastUpdatedText = "수신 대기";

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string essControlTableStatusText = "Read All 또는 체크 항목 Read를 눌러주세요.";

    [ObservableProperty]
    private bool isEssControlTableBusy;

    [ObservableProperty]
    private string inverterControlTableStatusText = "Read All 또는 체크 항목 Read를 눌러주세요.";

    [ObservableProperty]
    private bool isInverterControlTableBusy;

    // =====================================================
    // Control ESS (30001 ~ 30012) 관리자 제어 탭
    // 실제 Write는 사용자가 "Write Enable"을 체크한 뒤에만 허용한다.
    // =====================================================
    [ObservableProperty]
    private string controlEssStatusText = "현재 설정을 읽어주세요.";

    [ObservableProperty]
    private string controlWord1RawText = "0x0000";

    [ObservableProperty]
    private string controlWord2RawText = "0x0000";

    [ObservableProperty]
    private bool isControlEssLoaded;

    [ObservableProperty]
    private bool isControlWriteEnabled;

    [ObservableProperty]
    private bool isControlEssBusy;

    [ObservableProperty]
    private int selectedOperationModeIndex;

    [ObservableProperty]
    private bool pack1PowerOn;

    [ObservableProperty]
    private bool pack2PowerOn;

    [ObservableProperty]
    private bool inverter1OutputOn;

    [ObservableProperty]
    private bool inverter2OutputOn;

    [ObservableProperty]
    private bool dcDc1OutputOn;

    [ObservableProperty]
    private bool dcDc2OutputOn;

    [ObservableProperty]
    private bool systemRunOn;

    [ObservableProperty]
    private bool bms1ManualEnable;

    [ObservableProperty]
    private bool bms2ManualEnable;

    [ObservableProperty]
    private bool essChargeNegativeRelayOn;

    [ObservableProperty]
    private bool essChargePositiveRelayOn;

    [ObservableProperty]
    private bool evChargePositiveRelayOn;

    [ObservableProperty]
    private bool evChargeNegativeRelayOn;

    [ObservableProperty]
    private bool acMainContactorOn;

    [ObservableProperty]
    private bool acNeutralSwitchOn;

    [ObservableProperty]
    private bool alarmLampOn;

    [ObservableProperty]
    private bool faultLampOn;

    [ObservableProperty]
    private bool buzzerOn;

    [ObservableProperty]
    private string chargingMaxLimitVoltageText = "340.8";

    [ObservableProperty]
    private string chargingMaxCurrentText = "65.0";

    [ObservableProperty]
    private string dischargingMinLimitVoltageText = "340.8";

    [ObservableProperty]
    private string dischargingCurrentText = "65.0";

    [ObservableProperty]
    private string installedModuleCountText = "2";

    [ObservableProperty]
    private string targetChargeSocText = "90.0";

    [ObservableProperty]
    private string targetDischargeSocText = "20.0";

    [ObservableProperty]
    private string acMaxChargePowerText = "10.0";

    [ObservableProperty]
    private string acMaxDischargePowerOnGridText = "10.0";

    [ObservableProperty]
    private string acMaxDischargePowerOffGridText = "10.0";

    private ushort _controlWord1Raw;
    private ushort _controlWord2Raw;

    // =====================================================
    // Inverter SET (40024 ~ 40063 / 41024 ~ 41063)
    // 관리자 설정 탭. 실제 Write는 현재값을 읽고 Write Enable을 체크한 뒤에만 허용한다.
    // 모드 변경(40029/41029)은 인버터 Shutdown 확인 후에만 허용한다.
    // =====================================================
    private const ushort InverterSettingFirstOffset = 23;
    private const ushort InverterSettingLastOffset = 62;

    [ObservableProperty]
    private string inverterSettingsStatusText = "현재 설정을 읽어주세요.";

    [ObservableProperty]
    private bool isInverterSettingsLoaded;

    [ObservableProperty]
    private bool isInverterWriteEnabled;

    [ObservableProperty]
    private bool isInverterSettingsBusy;

    [ObservableProperty]
    private int selectedInverterTargetIndex;

    [ObservableProperty]
    private bool isInverterModePowerOffConfirmed;

    [ObservableProperty]
    private string inverterAltitudeText = "1000";

    [ObservableProperty]
    private string inverterGroupNumberText = "0";

    [ObservableProperty]
    private int selectedInverterAddressAllocationIndex = 1;

    [ObservableProperty]
    private int selectedInverterWorkingModeIndex;

    [ObservableProperty]
    private string inverterDcLinkVoltageText = "0.0";

    [ObservableProperty]
    private string inverterDcCurrentText = "0.00";

    [ObservableProperty]
    private int selectedAcSidePowerControlIndex;

    [ObservableProperty]
    private string inverterAcActivePowerText = "0";

    [ObservableProperty]
    private string inverterAcReactivePowerText = "0";

    [ObservableProperty]
    private string inverterPowerFactorText = "0.00";

    [ObservableProperty]
    private int selectedReactivePowerTypeIndex;

    [ObservableProperty]
    private string inverterRatedPhaseVoltageText = "0.0";

    [ObservableProperty]
    private string inverterRatedAcFrequencyText = "60.000";

    [ObservableProperty]
    private bool isPhaseErrorAllowed;

    [ObservableProperty]
    private bool isIslandDetectionDisabled;

    [ObservableProperty]
    private string inverterDcUnderVoltageProtectionText = "0.0";

    [ObservableProperty]
    private string inverterDcOverVoltageProtectionText = "0.0";

    [ObservableProperty]
    private string inverterAcUnderVoltageProtectionText = "0.0";

    [ObservableProperty]
    private string inverterAcUnderVoltageTimeText = "0.00";

    [ObservableProperty]
    private string inverterAcOverVoltageProtectionText = "0.0";

    [ObservableProperty]
    private string inverterAcOverVoltageTimeText = "0.00";

    [ObservableProperty]
    private string inverterAcUnderFrequency1Text = "0.00";

    [ObservableProperty]
    private string inverterAcUnderFrequency1TimeText = "0.00";

    [ObservableProperty]
    private string inverterAcOverFrequency1Text = "0.00";

    [ObservableProperty]
    private string inverterAcOverFrequency1TimeText = "0.00";

    [ObservableProperty]
    private string inverterAcUnderFrequency2Text = "0.00";

    [ObservableProperty]
    private string inverterAcUnderFrequency2TimeText = "0.00";

    [ObservableProperty]
    private string inverterAcOverFrequency2Text = "0.00";

    [ObservableProperty]
    private string inverterAcOverFrequency2TimeText = "0.00";

    private ushort _inverter1WorkingModeRaw;
    private ushort _inverter2WorkingModeRaw;
    private ushort _inverter1PowerCommandRaw;
    private ushort _inverter2PowerCommandRaw;

    public string[] InverterSettingTargetOptions { get; } = new[]
    {
        "Inverter 1 · 40024 ~ 40063",
        "Inverter 2 · 41024 ~ 41063",
        "Inverter 1 + 2 동시 적용"
    };

    public string[] InverterWorkingModes { get; } = new[]
    {
        "0 · Grid-connected",
        "1 · Off-grid",
        "2 · Rectifier"
    };

    public string[] InverterAddressAllocationModes { get; } = new[]
    {
        "0 · Automatic allocation",
        "1 · Dial setting"
    };

    public string[] AcSidePowerControlModes { get; } = new[]
    {
        "0 · DC Side Power Control",
        "1 · AC Side Power Control"
    };

    public string[] ReactivePowerTypes { get; } = new[]
    {
        "0x00A0 · Disable reactive power output",
        "0x00A1 · Use PF setting",
        "0x00A2 · Use reactive power setting"
    };

    public string InverterSettingsRawSummaryText =>
        $"INV1 Mode=0x{_inverter1WorkingModeRaw:X4}, PowerCmd=0x{_inverter1PowerCommandRaw:X4}    /    " +
        $"INV2 Mode=0x{_inverter2WorkingModeRaw:X4}, PowerCmd=0x{_inverter2PowerCommandRaw:X4}";

    partial void OnSelectedInverterTargetIndexChanged(int value)
    {
        IsInverterSettingsLoaded = false;
        IsInverterModePowerOffConfirmed = false;
        InverterSettingsStatusText = "대상을 변경했습니다. 현재 설정을 다시 읽어주세요.";
    }

    public string[] OperationModes { get; } = new[]
    {
        "0 · Standby",
        "1 · AC 자동 충전",
        "2 · 수동 제어",
        "3 · AC 외부 전원출력",
        "4 · 계통 방전",
        "5 · DC 차량 급속충전",
        "6 · DC ESS 급속충전",
        "7 · 계통 UPS방전"
    };

    public string ControlEssRawSummaryText =>
        $"30001 = {ControlWord1RawText}    /    30002 = {ControlWord2RawText}";


    public AdminViewModel(EmsService emsService, Action? exitAdminAction = null)
    {
        ClearLogCommand = new RelayCommand(ClearLog);

        _emsService = emsService;
        _emsService.CommunicationLogReceived +=
            EmsService_CommunicationLogReceived;

        _exitAdminAction = exitAdminAction;

        BuildStatusRows();
        BuildControlTableRows();


        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)

        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();

        _ = RefreshStatusCoreAsync(isManualRefresh: false);
        AddLog("관리자 모니터 화면 시작");
    }


    private async Task RefreshStatusCoreAsync(bool isManualRefresh)
    {
        if (_isRefreshing || _disposed)
        {
            return;
        }

        IsConnected = _emsService.IsConnected;

        if (!IsConnected)
        {
            CommunicationText = "EMS 통신 미연결";

            if (isManualRefresh)
            {
                AddLog("상태값 읽기 취소 · EMS 통신 미연결");
            }

            return;
        }

        _isRefreshing = true;

        try
        {
            if (isManualRefresh)
            {
                AddLog("상태값 읽기 요청");
            }

            AdminStatusSnapshot snapshot =
                await _emsService.ReadAdminStatusAsync();

            ApplyRows(SystemRows, snapshot.EmsValues, EmsStartAddress);
            ApplyRows(BatteryRows, snapshot.Pack1Values, snapshot.Pack2Values, PackStartAddress);
            ApplyRows(InverterRows, snapshot.Inverter1Values, snapshot.Inverter2Values, InverterStartAddress);

            int receivedGroupCount = 0;

            if (snapshot.EmsValues.Length > 0)
            {
                receivedGroupCount++;
            }

            if (snapshot.Pack1Values.Length > 0)
            {
                receivedGroupCount++;
            }

            if (snapshot.Pack2Values.Length > 0)
            {
                receivedGroupCount++;
            }

            if (snapshot.Inverter1Values.Length > 0)
            {
                receivedGroupCount++;
            }

            if (snapshot.Inverter2Values.Length > 0)
            {
                receivedGroupCount++;
            }

            if (receivedGroupCount == 5)
            {
                CommunicationText = "EMS 상태값 수신 정상 · Read Only";
            }
            else if (receivedGroupCount > 0)
            {
                CommunicationText =
                    $"EMS 일부 수신 · {receivedGroupCount}/5 그룹";
            }
            else
            {
                // COM 포트는 열려 있으므로 연결 해제하지 않는다.
                // 1초 타이머가 다음 Poll에서 다시 읽기를 시도한다.
                CommunicationText = "EMS 응답 없음 · 자동 재시도 중";
            }

            LastUpdatedText =
                $"마지막 시도  {DateTime.Now:HH:mm:ss}";

            if (isManualRefresh)
            {
                AddLog(
                    $"상태값 수신 성공 · " +
                    $"EMS {snapshot.EmsValues.Length}개 / " +
                    $"Pack1 {snapshot.Pack1Values.Length}개 / " +
                    $"Pack2 {snapshot.Pack2Values.Length}개 / " +
                    $"INV1 {snapshot.Inverter1Values.Length}개 / " +
                    $"INV2 {snapshot.Inverter2Values.Length}개");
            }
        }
        catch (Exception ex)
        {
            IsConnected = _emsService.IsConnected;
            CommunicationText = "EMS 상태값 수신 실패";
            LastUpdatedText = "통신 오류";

            AddLog(
                $"상태값 수신 실패 · " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
        }
    }
    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (_isConnectionChanging)
        {
            return;
        }

        if (_emsService.IsConnected)
        {
            IsConnected = true;
            CommunicationText = "EMS 통신 연결됨";
            LastUpdatedText = "상태값 자동 갱신 중";

            AddLog("연결 요청 무시 · EMS가 이미 연결되어 있습니다.");

            return;
        }

        _isConnectionChanging = true;

        try
        {
            IsConnected = false;

            CommunicationText = "EMS 자동 연결 중...";
            LastUpdatedText = "USB 장치 검색 중";

            AddLog("EMS 자동 연결 요청");

            // VID/PID 기준으로 EMS USB 장치를 찾아
            // 해당 COM 포트에 자동 연결한다.
            string? portName =
                await _emsService.FindAndConnectAsync();

            IsConnected = _emsService.IsConnected;

            if (!IsConnected || string.IsNullOrWhiteSpace(portName))
            {
                CommunicationText = "EMS 자동 연결 실패";
                LastUpdatedText = "수신 대기";

                AddLog("EMS 자동 연결 실패");

                return;
            }

            CommunicationText = "EMS 연결됨 · 상태 확인 중";
            LastUpdatedText = "EMS 응답 대기";

            AddLog("EMS 자동 연결 성공");

            // 연결 직후 현재 상태를 한 번 읽는다.
            await RefreshStatusCoreAsync(isManualRefresh: true);
        }
        catch (Exception ex)
        {
            IsConnected = false;

            CommunicationText = "EMS 연결 오류";
            LastUpdatedText = "수신 대기";

            AddLog(
                $"EMS 자동 연결 오류 · " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _isConnectionChanging = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (_isConnectionChanging)
        {
            return;
        }

        if (!_emsService.IsConnected)
        {
            IsConnected = false;

            CommunicationText = "EMS 통신 미연결";
            LastUpdatedText = "수신 대기";

            AddLog("연결 해제 요청 무시 · EMS가 이미 미연결 상태입니다.");

            return;
        }

        _isConnectionChanging = true;

        try
        {
            CommunicationText = "EMS 연결 해제 중...";
            LastUpdatedText = "통신 종료 중";

            AddLog("EMS 연결 해제 요청");

            await _emsService.DisconnectAsync();

            IsConnected = false;

            CommunicationText = "EMS 통신 미연결";
            LastUpdatedText = "수신 대기";

            AddLog("EMS 통신 연결 해제 완료");
        }
        catch (Exception ex)
        {
            IsConnected = _emsService.IsConnected;

            CommunicationText = "EMS 연결 해제 오류";
            LastUpdatedText = "통신 상태 확인 필요";

            AddLog(
                $"EMS 연결 해제 오류 · " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _isConnectionChanging = false;
        }
    }



    [RelayCommand]
    private async Task ReadControlEssAsync()
    {
        if (IsControlEssBusy)
        {
            return;
        }

        if (!_emsService.IsConnected)
        {
            ControlEssStatusText = "EMS 통신 미연결 · Control ESS를 읽을 수 없습니다.";
            AddLog("Control ESS Read 취소 · EMS 통신 미연결");
            return;
        }

        IsControlEssBusy = true;

        try
        {
            ControlEssStatusText = "Control ESS 설정값 읽는 중...";

            ushort[] values = await _emsService.ReadControlEssAsync();

            if (values.Length < 12)
            {
                throw new InvalidOperationException(
                    $"Control ESS 수신 Register 수가 부족합니다. 수신={values.Length}, 필요=12");
            }

            ApplyControlEssValues(values);

            IsControlEssLoaded = true;
            ControlEssStatusText = "Control ESS 30001 ~ 30012 수신 완료";
            AddLog(
                $"Control ESS Read 완료 · " +
                $"30001={ControlWord1RawText} · 30002={ControlWord2RawText}");
        }
        catch (Exception ex)
        {
            IsControlEssLoaded = false;
            ControlEssStatusText = "Control ESS Read 실패";
            AddLog($"Control ESS Read 실패 · {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsControlEssBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyPrimaryControlAsync()
    {
        if (!CanWriteControlEss())
        {
            return;
        }

        IsControlEssBusy = true;

        try
        {
            // Bit0~8과 Bit12만 화면 값으로 변경합니다.
            // SystemOff / ResetEnable / Reserved 비트는 Write 직전 실제 30001 값에서 보존됩니다.
            const ushort editableMask = 0x11FF;

            ushort desiredValue = 0;
            desiredValue |= (ushort)(SelectedOperationModeIndex & 0x0007);
            desiredValue = SetBit(desiredValue, 3, Pack1PowerOn);
            desiredValue = SetBit(desiredValue, 4, Pack2PowerOn);
            desiredValue = SetBit(desiredValue, 5, Inverter1OutputOn);
            desiredValue = SetBit(desiredValue, 6, Inverter2OutputOn);
            desiredValue = SetBit(desiredValue, 7, DcDc1OutputOn);
            desiredValue = SetBit(desiredValue, 8, DcDc2OutputOn);
            desiredValue = SetBit(desiredValue, 12, SystemRunOn);

            ushort confirmedValue =
                await _emsService.UpdateControlEssWordAsync(
                    EmsControlAddresses.ControlWord1,
                    editableMask,
                    desiredValue,
                    "관리자 30001 운전 제어");

            AddLog(
                $"Control ESS 30001 적용 · " +
                $"화면기준=0x{desiredValue:X4} · ReadBack=0x{confirmedValue:X4}");

            await RefreshControlEssAfterWriteAsync();
            ControlEssStatusText = "30001 운전 제어 적용 및 Readback 완료";
        }
        catch (Exception ex)
        {
            ControlEssStatusText = "30001 운전 제어 적용 실패";
            AddLog($"Control ESS 30001 Write 실패 · {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsControlWriteEnabled = false;
            IsControlEssBusy = false;
        }
    }
    [RelayCommand]
    private async Task ApplyAuxiliaryControlAsync()
    {
        if (!CanWriteControlEss())
        {
            return;
        }

        IsControlEssBusy = true;

        try
        {
            // Bit0~3, Bit5~11만 화면 값으로 변경합니다.
            // 주소맵 확인 전인 Bit4와 Reserved Bit12~15는 실제 30002 값에서 보존됩니다.
            ushort desiredValue = 0;
            desiredValue = SetBit(desiredValue, 0, Bms1ManualEnable);
            desiredValue = SetBit(desiredValue, 1, Bms2ManualEnable);
            desiredValue = SetBit(desiredValue, 2, EssChargeNegativeRelayOn);
            desiredValue = SetBit(desiredValue, 3, EssChargePositiveRelayOn);
            desiredValue = SetBit(desiredValue, 5, EvChargePositiveRelayOn);
            desiredValue = SetBit(desiredValue, 6, EvChargeNegativeRelayOn);
            desiredValue = SetBit(desiredValue, 7, AcMainContactorOn);
            desiredValue = SetBit(desiredValue, 8, AcNeutralSwitchOn);
            desiredValue = SetBit(desiredValue, 9, AlarmLampOn);
            desiredValue = SetBit(desiredValue, 10, FaultLampOn);
            desiredValue = SetBit(desiredValue, 11, BuzzerOn);

            ushort confirmedValue =
                await _emsService.UpdateControlEssWordAsync(
                    EmsControlAddresses.ControlWord2,
                    EmsControlWord2.EditableMask,
                    desiredValue,
                    "관리자 30002 보조 제어");

            AddLog(
                $"Control ESS 30002 적용 · " +
                $"화면기준=0x{desiredValue:X4} · ReadBack=0x{confirmedValue:X4}");

            await RefreshControlEssAfterWriteAsync();
            ControlEssStatusText = "30002 보조 제어 적용 및 Readback 완료";
        }
        catch (Exception ex)
        {
            ControlEssStatusText = "30002 보조 제어 적용 실패";
            AddLog($"Control ESS 30002 Write 실패 · {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsControlWriteEnabled = false;
            IsControlEssBusy = false;
        }
    }
    [RelayCommand]
    private async Task ApplyLimitSettingsAsync()
    {
        if (!CanWriteControlEss())
        {
            return;
        }

        IsControlEssBusy = true;

        try
        {
            ushort[] values = BuildControlEssLimitValues();

            await _emsService.WriteControlEssLimitsAsync(values);
            AddLog("Control ESS 30003 ~ 30012 제한값 적용 요청 완료");

            await RefreshControlEssAfterWriteAsync();
            ControlEssStatusText = "30003 ~ 30012 제한값 적용 및 Readback 완료";
        }
        catch (Exception ex)
        {
            ControlEssStatusText = "제한값 적용 실패 · 입력 범위와 통신 상태를 확인하세요.";
            AddLog($"Control ESS 제한값 Write 실패 · {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsControlWriteEnabled = false;
            IsControlEssBusy = false;
        }
    }

    [RelayCommand]
    private async Task RequestSystemOffAsync()
    {
        if (!CanWriteControlEss())
        {
            return;
        }

        IsControlEssBusy = true;

        try
        {
            ushort confirmedValue =
                await _emsService.UpdateControlEssWordAsync(
                    EmsControlAddresses.ControlWord1,
                    EmsControlWord1.SystemOff,
                    EmsControlWord1.SystemOff,
                    "관리자 System Off 요청");

            AddLog(
                $"System Off 요청 · " +
                $"30001 ReadBack=0x{confirmedValue:X4}");

            await RefreshControlEssAfterWriteAsync();
            ControlEssStatusText = "System Off 요청 전송 및 Readback 완료";
        }
        catch (Exception ex)
        {
            ControlEssStatusText = "System Off 요청 실패";
            AddLog($"System Off 요청 실패 · {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsControlWriteEnabled = false;
            IsControlEssBusy = false;
        }
    }
    [RelayCommand]
    private async Task RequestSystemResetEnableAsync()
    {
        if (!CanWriteControlEss())
        {
            return;
        }

        IsControlEssBusy = true;

        try
        {
            ushort confirmedValue =
                await _emsService.UpdateControlEssWordAsync(
                    EmsControlAddresses.ControlWord1,
                    EmsControlWord1.SystemResetEnable,
                    EmsControlWord1.SystemResetEnable,
                    "관리자 System Reset Enable 요청");

            AddLog(
                $"System Reset Enable 요청 · " +
                $"30001 ReadBack=0x{confirmedValue:X4}");

            await RefreshControlEssAfterWriteAsync();
            ControlEssStatusText = "System Reset Enable 요청 전송 및 Readback 완료";
        }
        catch (Exception ex)
        {
            ControlEssStatusText = "System Reset Enable 요청 실패";
            AddLog($"System Reset Enable 요청 실패 · {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsControlWriteEnabled = false;
            IsControlEssBusy = false;
        }
    }
    private async Task RefreshControlEssAfterWriteAsync()
    {
        ushort[] values = await _emsService.ReadControlEssAsync();

        if (values.Length < 12)
        {
            throw new InvalidOperationException(
                $"Control ESS Readback Register 수가 부족합니다. 수신={values.Length}, 필요=12");
        }

        ApplyControlEssValues(values);
        IsControlEssLoaded = true;
    }

    private bool CanWriteControlEss()
    {
        if (!_emsService.IsConnected)
        {
            ControlEssStatusText = "EMS 통신 미연결 · Write할 수 없습니다.";
            AddLog("Control ESS Write 취소 · EMS 통신 미연결");
            return false;
        }

        if (!IsControlEssLoaded)
        {
            ControlEssStatusText = "먼저 현재 설정을 읽은 뒤 Write 하세요.";
            AddLog("Control ESS Write 취소 · 현재 설정 Read 필요");
            return false;
        }

        if (!IsControlWriteEnabled)
        {
            ControlEssStatusText = "Write Enable을 체크한 뒤 적용하세요.";
            AddLog("Control ESS Write 취소 · Write Enable 미체크");
            return false;
        }

        return true;
    }

    private void ApplyControlEssValues(ushort[] values)
    {
        _controlWord1Raw = values[0];
        _controlWord2Raw = values[1];

        ControlWord1RawText = $"0x{_controlWord1Raw:X4}";
        ControlWord2RawText = $"0x{_controlWord2Raw:X4}";
        OnPropertyChanged(nameof(ControlEssRawSummaryText));

        SelectedOperationModeIndex = _controlWord1Raw & 0x0007;
        Pack1PowerOn = GetBit(_controlWord1Raw, 3);
        Pack2PowerOn = GetBit(_controlWord1Raw, 4);
        Inverter1OutputOn = GetBit(_controlWord1Raw, 5);
        Inverter2OutputOn = GetBit(_controlWord1Raw, 6);
        DcDc1OutputOn = GetBit(_controlWord1Raw, 7);
        DcDc2OutputOn = GetBit(_controlWord1Raw, 8);
        SystemRunOn = GetBit(_controlWord1Raw, 12);

        Bms1ManualEnable = GetBit(_controlWord2Raw, 0);
        Bms2ManualEnable = GetBit(_controlWord2Raw, 1);
        EssChargeNegativeRelayOn = GetBit(_controlWord2Raw, 2);
        EssChargePositiveRelayOn = GetBit(_controlWord2Raw, 3);
        EvChargePositiveRelayOn = GetBit(_controlWord2Raw, 5);
        EvChargeNegativeRelayOn = GetBit(_controlWord2Raw, 6);
        AcMainContactorOn = GetBit(_controlWord2Raw, 7);
        AcNeutralSwitchOn = GetBit(_controlWord2Raw, 8);
        AlarmLampOn = GetBit(_controlWord2Raw, 9);
        FaultLampOn = GetBit(_controlWord2Raw, 10);
        BuzzerOn = GetBit(_controlWord2Raw, 11);

        ChargingMaxLimitVoltageText = FormatControlValue(values[2], 0.01, 2);
        ChargingMaxCurrentText = FormatControlValue(ToInt16(values[3]), 0.01, 2);
        DischargingMinLimitVoltageText = FormatControlValue(values[4], 0.01, 2);
        DischargingCurrentText = FormatControlValue(ToInt16(values[5]), 0.01, 2);
        InstalledModuleCountText = values[6].ToString(CultureInfo.InvariantCulture);
        TargetChargeSocText = FormatControlValue(values[7], 0.1, 1);
        TargetDischargeSocText = FormatControlValue(values[8], 0.1, 1);
        AcMaxChargePowerText = FormatControlValue(ToInt16(values[9]), 0.01, 2);
        AcMaxDischargePowerOnGridText = FormatControlValue(ToInt16(values[10]), 0.01, 2);
        AcMaxDischargePowerOffGridText = FormatControlValue(ToInt16(values[11]), 0.01, 2);
    }

    private ushort[] BuildControlEssLimitValues()
    {
        return new ushort[]
        {
            ToUnsignedRaw(ChargingMaxLimitVoltageText, "Charging Max Limit Voltage", 0.01, 0, 655.35),
            ToSignedRaw(ChargingMaxCurrentText, "Charging Max Current", 0.01, 0, 327.67),
            ToUnsignedRaw(DischargingMinLimitVoltageText, "Discharging Min Limit Voltage", 0.01, 0, 655.35),
            ToSignedRaw(DischargingCurrentText, "Discharging Current", 0.01, 0, 327.67),
            ToUnsignedRaw(InstalledModuleCountText, "Number of Installed Modules", 1.0, 0, 8),
            ToUnsignedRaw(TargetChargeSocText, "Target Charge SOC", 0.1, 0, 100),
            ToUnsignedRaw(TargetDischargeSocText, "Target Discharge SOC", 0.1, 0, 100),
            ToSignedRaw(AcMaxChargePowerText, "AC Max Charge Power", 0.01, 0, 327.67),
            ToSignedRaw(AcMaxDischargePowerOnGridText, "AC Max Discharge Power On Grid", 0.01, 0, 327.67),
            ToSignedRaw(AcMaxDischargePowerOffGridText, "AC Max Discharge Power Off Grid", 0.01, 0, 327.67)
        };
    }

    private static ushort ToUnsignedRaw(
        string text,
        string fieldName,
        double scale,
        double minimum,
        double maximum)
    {
        double value = ParseControlValue(text, fieldName, minimum, maximum);
        int raw = checked((int)Math.Round(value / scale, MidpointRounding.AwayFromZero));

        if (raw is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                fieldName,
                $"{fieldName}의 Raw 값 범위를 벗어났습니다.");
        }

        return (ushort)raw;
    }

    private static ushort ToSignedRaw(
        string text,
        string fieldName,
        double scale,
        double minimum,
        double maximum)
    {
        double value = ParseControlValue(text, fieldName, minimum, maximum);
        int raw = checked((int)Math.Round(value / scale, MidpointRounding.AwayFromZero));

        if (raw is < short.MinValue or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                fieldName,
                $"{fieldName}의 Raw 값 범위를 벗어났습니다.");
        }

        return unchecked((ushort)(short)raw);
    }

    private static double ParseControlValue(
        string text,
        string fieldName,
        double minimum,
        double maximum)
    {
        bool parsed =
            double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out double value) ||
            double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);

        if (!parsed)
        {
            throw new FormatException(
                $"{fieldName} 값 '{text}'을 숫자로 읽을 수 없습니다.");
        }

        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                fieldName,
                $"{fieldName}은(는) {minimum:0.##} ~ {maximum:0.##} 범위여야 합니다.");
        }

        return value;
    }

    private static string FormatControlValue(
        double rawValue,
        double scale,
        int decimalPlaces)
    {
        return (rawValue * scale).ToString(
            $"F{decimalPlaces}",
            CultureInfo.InvariantCulture);
    }

    private static bool GetBit(ushort value, int bit)
        => (value & (1 << bit)) != 0;

    private static ushort SetBit(ushort value, int bit, bool isOn)
    {
        ushort mask = (ushort)(1 << bit);

        return isOn
            ? (ushort)(value | mask)
            : (ushort)(value & ~mask);
    }
    private static short ToInt16(ushort value)
    {
        return unchecked((short)value);
    }


    [RelayCommand]
    private async Task ReadInverterSettingsAsync()
    {
        if (IsInverterSettingsBusy)
        {
            return;
        }

        if (!_emsService.IsConnected)
        {
            InverterSettingsStatusText = "EMS 통신 미연결 · 인버터 설정을 읽을 수 없습니다.";
            AddLog("Inverter SET Read 취소 · EMS 통신 미연결");
            return;
        }

        IsInverterSettingsBusy = true;

        try
        {
            InverterSettingsStatusText = "인버터 설정값 읽는 중...";

            (ushort[] inverter1Values, ushort[] inverter2Values) =
                await _emsService.ReadInverterSettingsAsync();

            EnsureInverterSettingValues(inverter1Values, "Inverter 1");
            EnsureInverterSettingValues(inverter2Values, "Inverter 2");

            ApplyInverterSettingValues(inverter1Values, inverter2Values);

            IsInverterSettingsLoaded = true;
            IsInverterModePowerOffConfirmed = false;
            InverterSettingsStatusText =
                $"{GetInverterTargetName()} 설정값 Read 완료 · 40024~40063 / 41024~41063";

            AddLog(
                $"Inverter SET Read 완료 · " +
                $"INV1 {inverter1Values.Length}개 / INV2 {inverter2Values.Length}개");
        }
        catch (Exception ex)
        {
            IsInverterSettingsLoaded = false;
            InverterSettingsStatusText = "인버터 설정값 Read 실패";
            AddLog($"Inverter SET Read 실패 · {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsInverterSettingsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyInverterBasicSettingsAsync()
    {
        if (!CanWriteInverterSettings())
        {
            return;
        }

        if (IsInverterWorkingModeChanged() && !IsInverterModePowerOffConfirmed)
        {
            InverterSettingsStatusText =
                "운전 모드 변경은 인버터 Shutdown 후 '전원 OFF 확인'을 체크해야 적용할 수 있습니다.";
            AddLog("Inverter SET Write 취소 · 모드 변경 전 Shutdown 확인 필요");
            return;
        }

        IsInverterSettingsBusy = true;

        try
        {
            (ushort Offset, ushort Value, string Name)[] writes =
                BuildInverterBasicSettingWrites();

            await WriteInverterSettingListAsync(writes);
            await RefreshInverterSettingsAfterWriteAsync();

            InverterSettingsStatusText =
                $"{GetInverterTargetName()} 기본 설정 적용 및 Readback 완료";
        }
        catch (Exception ex)
        {
            InverterSettingsStatusText = "인버터 기본 설정 적용 실패";
            AddLog($"Inverter 기본 설정 Write 실패 · {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsInverterWriteEnabled = false;
            IsInverterSettingsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyInverterProtectionSettingsAsync()
    {
        if (!CanWriteInverterSettings())
        {
            return;
        }

        IsInverterSettingsBusy = true;

        try
        {
            (ushort Offset, ushort Value, string Name)[] writes =
                BuildInverterProtectionSettingWrites();

            await WriteInverterSettingListAsync(writes);
            await RefreshInverterSettingsAfterWriteAsync();

            InverterSettingsStatusText =
                $"{GetInverterTargetName()} 보호 설정 적용 및 Readback 완료";
        }
        catch (Exception ex)
        {
            InverterSettingsStatusText = "인버터 보호 설정 적용 실패";
            AddLog($"Inverter 보호 설정 Write 실패 · {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsInverterWriteEnabled = false;
            IsInverterSettingsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ShutdownInverterAsync()
    {
        await RequestInverterPowerAsync(shutdown: true);
    }

    [RelayCommand]
    private async Task StartInverterAsync()
    {
        await RequestInverterPowerAsync(shutdown: false);
    }

    [RelayCommand]
    private async Task ResetAcUnderVoltageAsync()
    {
        await RequestInverterResetAsync(26, "AC Under Voltage Reset");
    }

    [RelayCommand]
    private async Task ResetRectifierDcUnderVoltageAsync()
    {
        await RequestInverterResetAsync(27, "Rectifier DC Under Voltage Reset");
    }

    [RelayCommand]
    private async Task ResetDcOverVoltageAsync()
    {
        await RequestInverterResetAsync(30, "DC Over Voltage Reset");
    }

    [RelayCommand]
    private async Task ResetInverterDcUnderVoltageAsync()
    {
        await RequestInverterResetAsync(33, "Inverter DC Under Voltage Reset");
    }

    [RelayCommand]
    private async Task ResetShortCircuitAsync()
    {
        await RequestInverterResetAsync(35, "Short Circuit Reset");
    }

    private async Task RequestInverterPowerAsync(bool shutdown)
    {
        if (!CanWriteInverterSettings())
        {
            return;
        }

        IsInverterSettingsBusy = true;

        try
        {
            ushort powerCommand = shutdown ? (ushort)1 : (ushort)0;

            await _emsService.WriteInverterSettingAsync(
                SelectedInverterTargetIndex,
                offset: 29,
                logicalValue: powerCommand);

            AddLog(
                $"Inverter Power {(shutdown ? "Shutdown" : "Start")} 요청 · " +
                $"대상={GetInverterTargetName()}");

            await RefreshInverterSettingsAfterWriteAsync();

            IsInverterModePowerOffConfirmed = shutdown;
            InverterSettingsStatusText = shutdown
                ? "Shutdown 요청 및 Readback 완료 · 모드 변경 전 실제 인버터 정지 상태를 확인하세요."
                : "Start 요청 및 Readback 완료";
        }
        catch (Exception ex)
        {
            InverterSettingsStatusText =
                shutdown ? "Shutdown 요청 실패" : "Start 요청 실패";
            AddLog(
                $"Inverter Power {(shutdown ? "Shutdown" : "Start")} 실패 · " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsInverterWriteEnabled = false;
            IsInverterSettingsBusy = false;
        }
    }

    private async Task RequestInverterResetAsync(
        ushort offset,
        string resetName)
    {
        if (!CanWriteInverterSettings())
        {
            return;
        }

        IsInverterSettingsBusy = true;

        try
        {
            // 주소맵의 Reset 항목은 0=Disable / 1=Reset 이므로 1회 Write만 수행한다.
            // Reset 펄스 유지시간은 EMS/인버터 펌웨어 담당자 확인이 필요하다.
            await _emsService.WriteInverterSettingAsync(
                SelectedInverterTargetIndex,
                offset,
                logicalValue: 1);

            AddLog($"Inverter Reset 요청 · {resetName} · 대상={GetInverterTargetName()}");

            await RefreshInverterSettingsAfterWriteAsync();
            InverterSettingsStatusText = $"{resetName} 요청 및 Readback 완료";
        }
        catch (Exception ex)
        {
            InverterSettingsStatusText = $"{resetName} 요청 실패";
            AddLog($"Inverter Reset 실패 · {resetName} · {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsInverterWriteEnabled = false;
            IsInverterSettingsBusy = false;
        }
    }

    private bool CanWriteInverterSettings()
    {
        if (!_emsService.IsConnected)
        {
            InverterSettingsStatusText = "EMS 통신 미연결 · Write할 수 없습니다.";
            AddLog("Inverter SET Write 취소 · EMS 통신 미연결");
            return false;
        }

        if (!IsInverterSettingsLoaded)
        {
            InverterSettingsStatusText = "먼저 현재 설정을 읽은 뒤 Write 하세요.";
            AddLog("Inverter SET Write 취소 · 현재 설정 Read 필요");
            return false;
        }

        if (!IsInverterWriteEnabled)
        {
            InverterSettingsStatusText = "Write Enable을 체크한 뒤 적용하세요.";
            AddLog("Inverter SET Write 취소 · Write Enable 미체크");
            return false;
        }

        return true;
    }

    private bool IsInverterWorkingModeChanged()
    {
        ushort newMode = (ushort)SelectedInverterWorkingModeIndex;

        return SelectedInverterTargetIndex switch
        {
            0 => _inverter1WorkingModeRaw != newMode,
            1 => _inverter2WorkingModeRaw != newMode,
            2 => _inverter1WorkingModeRaw != newMode ||
                 _inverter2WorkingModeRaw != newMode,
            _ => true
        };
    }

    private (ushort Offset, ushort Value, string Name)[]
        BuildInverterBasicSettingWrites()
    {
        return new (ushort Offset, ushort Value, string Name)[]
        {
            ((ushort)23, ToUnsignedRaw(InverterAltitudeText, "Operating Altitude", 1.0, 0, 5000), "Operating Altitude"),
            ((ushort)24, ToUnsignedRaw(InverterGroupNumberText, "Group Number", 1.0, 0, 7), "Group Number"),
            ((ushort)25, (ushort)SelectedInverterAddressAllocationIndex, "Address Allocation"),
            ((ushort)28, (ushort)SelectedInverterWorkingModeIndex, "Working Mode"),
            ((ushort)38, ToUnsignedRaw(InverterDcLinkVoltageText, "DC Link Voltage", 0.1, 0, 6553.5), "DC Link Voltage"),
            ((ushort)39, ToSignedRaw(InverterDcCurrentText, "DC Current", 0.01, -327.68, 327.67), "DC Current"),
            ((ushort)40, (ushort)SelectedAcSidePowerControlIndex, "AC Side Power Control"),
            ((ushort)41, ToSignedRaw(InverterAcActivePowerText, "AC Active Power", 1.0, short.MinValue, short.MaxValue), "AC Active Power"),
            ((ushort)42, ToSignedRaw(InverterAcReactivePowerText, "AC Reactive Power", 10.0, -327680, 327670), "AC Reactive Power"),
            ((ushort)43, ToSignedRaw(InverterPowerFactorText, "AC Power Factor", 0.01, -327.68, 327.67), "AC Power Factor"),
            ((ushort)44, GetReactivePowerTypeRaw(SelectedReactivePowerTypeIndex), "Reactive Power Type"),
            ((ushort)45, ToSignedRaw(InverterRatedPhaseVoltageText, "Rated Phase Voltage", 0.1, 0, 3276.7), "Rated Phase Voltage"),
            ((ushort)46, ToUnsignedRaw(InverterRatedAcFrequencyText, "Rated AC Frequency", 0.001, 0, 65.535), "Rated AC Frequency"),
            ((ushort)47, IsPhaseErrorAllowed ? (ushort)1 : (ushort)0, "Phase Error Allow"),
            ((ushort)48, IsIslandDetectionDisabled ? (ushort)1 : (ushort)0, "Island Detection")
        };
    }

    private (ushort Offset, ushort Value, string Name)[]
        BuildInverterProtectionSettingWrites()
    {
        return new (ushort Offset, ushort Value, string Name)[]
        {
            ((ushort)49, ToSignedRaw(InverterDcUnderVoltageProtectionText, "DC Under Voltage Protection", 0.1, 0, 3276.7), "DC Under Voltage Protection"),
            ((ushort)50, ToSignedRaw(InverterDcOverVoltageProtectionText, "DC Over Voltage Protection", 0.1, 0, 3276.7), "DC Over Voltage Protection"),
            ((ushort)51, ToSignedRaw(InverterAcUnderVoltageProtectionText, "AC Under Voltage Protection", 0.1, 0, 3276.7), "AC Under Voltage Protection"),
            ((ushort)52, ToSignedRaw(InverterAcUnderVoltageTimeText, "AC Under Voltage Protection Time", 0.01, 0, 327.67), "AC Under Voltage Protection Time"),
            ((ushort)53, ToSignedRaw(InverterAcOverVoltageProtectionText, "AC Over Voltage Protection", 0.1, 0, 3276.7), "AC Over Voltage Protection"),
            ((ushort)54, ToSignedRaw(InverterAcOverVoltageTimeText, "AC Over Voltage Protection Time", 0.01, 0, 327.67), "AC Over Voltage Protection Time"),
            ((ushort)55, ToSignedRaw(InverterAcUnderFrequency1Text, "1st AC Under Frequency", 0.01, 0, 327.67), "1st AC Under Frequency"),
            ((ushort)56, ToSignedRaw(InverterAcUnderFrequency1TimeText, "1st AC Under Frequency Time", 0.01, 0, 327.67), "1st AC Under Frequency Time"),
            ((ushort)57, ToSignedRaw(InverterAcOverFrequency1Text, "1st AC Over Frequency", 0.01, 0, 327.67), "1st AC Over Frequency"),
            ((ushort)58, ToSignedRaw(InverterAcOverFrequency1TimeText, "1st AC Over Frequency Time", 0.01, 0, 327.67), "1st AC Over Frequency Time"),
            ((ushort)59, ToSignedRaw(InverterAcUnderFrequency2Text, "2nd AC Under Frequency", 0.01, 0, 327.67), "2nd AC Under Frequency"),
            ((ushort)60, ToSignedRaw(InverterAcUnderFrequency2TimeText, "2nd AC Under Frequency Time", 0.01, 0, 327.67), "2nd AC Under Frequency Time"),
            ((ushort)61, ToSignedRaw(InverterAcOverFrequency2Text, "2nd AC Over Frequency", 0.01, 0, 327.67), "2nd AC Over Frequency"),
            ((ushort)62, ToSignedRaw(InverterAcOverFrequency2TimeText, "2nd AC Over Frequency Time", 0.01, 0, 327.67), "2nd AC Over Frequency Time")
        };
    }

    private async Task WriteInverterSettingListAsync(
        (ushort Offset, ushort Value, string Name)[] writes)
    {
        foreach ((ushort offset, ushort value, string name) in writes)
        {
            await _emsService.WriteInverterSettingAsync(
                SelectedInverterTargetIndex,
                offset,
                value);

            AddLog(
                $"Inverter SET 적용 · {name} · " +
                $"Offset={offset} · Logical=0x{value:X4} · 대상={GetInverterTargetName()}");
        }
    }

    private async Task RefreshInverterSettingsAfterWriteAsync()
    {
        (ushort[] inverter1Values, ushort[] inverter2Values) =
            await _emsService.ReadInverterSettingsAsync();

        EnsureInverterSettingValues(inverter1Values, "Inverter 1");
        EnsureInverterSettingValues(inverter2Values, "Inverter 2");
        ApplyInverterSettingValues(inverter1Values, inverter2Values);
        IsInverterSettingsLoaded = true;
    }

    private void ApplyInverterSettingValues(
        ushort[] inverter1Values,
        ushort[] inverter2Values)
    {
        _inverter1WorkingModeRaw = GetInverterSettingValue(inverter1Values, 28);
        _inverter2WorkingModeRaw = GetInverterSettingValue(inverter2Values, 28);
        _inverter1PowerCommandRaw = GetInverterSettingValue(inverter1Values, 29);
        _inverter2PowerCommandRaw = GetInverterSettingValue(inverter2Values, 29);
        OnPropertyChanged(nameof(InverterSettingsRawSummaryText));

        ushort[] sourceValues = SelectedInverterTargetIndex == 1
            ? inverter2Values
            : inverter1Values;

        InverterAltitudeText = GetInverterSettingValue(sourceValues, 23)
            .ToString(CultureInfo.InvariantCulture);
        InverterGroupNumberText = (GetInverterSettingValue(sourceValues, 24) & 0x0007)
            .ToString(CultureInfo.InvariantCulture);
        SelectedInverterAddressAllocationIndex = NormalizeOptionIndex(
            GetInverterSettingValue(sourceValues, 25), 1);
        SelectedInverterWorkingModeIndex = NormalizeOptionIndex(
            GetInverterSettingValue(sourceValues, 28), 2);

        InverterDcLinkVoltageText = FormatControlValue(
            GetInverterSettingValue(sourceValues, 38), 0.1, 1);
        InverterDcCurrentText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 39)), 0.01, 2);
        SelectedAcSidePowerControlIndex = NormalizeOptionIndex(
            GetInverterSettingValue(sourceValues, 40), 1);
        InverterAcActivePowerText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 41)), 1.0, 0);
        InverterAcReactivePowerText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 42)), 10.0, 0);
        InverterPowerFactorText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 43)), 0.01, 2);
        SelectedReactivePowerTypeIndex = GetReactivePowerTypeIndex(
            GetInverterSettingValue(sourceValues, 44));
        InverterRatedPhaseVoltageText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 45)), 0.1, 1);
        InverterRatedAcFrequencyText = FormatControlValue(
            GetInverterSettingValue(sourceValues, 46), 0.001, 3);
        IsPhaseErrorAllowed = GetInverterSettingValue(sourceValues, 47) == 1;
        IsIslandDetectionDisabled = GetInverterSettingValue(sourceValues, 48) == 1;

        InverterDcUnderVoltageProtectionText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 49)), 0.1, 1);
        InverterDcOverVoltageProtectionText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 50)), 0.1, 1);
        InverterAcUnderVoltageProtectionText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 51)), 0.1, 1);
        InverterAcUnderVoltageTimeText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 52)), 0.01, 2);
        InverterAcOverVoltageProtectionText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 53)), 0.1, 1);
        InverterAcOverVoltageTimeText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 54)), 0.01, 2);
        InverterAcUnderFrequency1Text = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 55)), 0.01, 2);
        InverterAcUnderFrequency1TimeText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 56)), 0.01, 2);
        InverterAcOverFrequency1Text = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 57)), 0.01, 2);
        InverterAcOverFrequency1TimeText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 58)), 0.01, 2);
        InverterAcUnderFrequency2Text = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 59)), 0.01, 2);
        InverterAcUnderFrequency2TimeText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 60)), 0.01, 2);
        InverterAcOverFrequency2Text = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 61)), 0.01, 2);
        InverterAcOverFrequency2TimeText = FormatControlValue(
            ToInt16(GetInverterSettingValue(sourceValues, 62)), 0.01, 2);
    }

    private static void EnsureInverterSettingValues(
        ushort[] values,
        string inverterName)
    {
        int requiredCount =
            InverterSettingLastOffset - InverterSettingFirstOffset + 1;

        if (values.Length < requiredCount)
        {
            throw new InvalidOperationException(
                $"{inverterName} SET 수신 Register 수가 부족합니다. " +
                $"수신={values.Length}, 필요={requiredCount}");
        }
    }

    private static ushort GetInverterSettingValue(
        ushort[] values,
        ushort offset)
    {
        int index = offset - InverterSettingFirstOffset;

        if (index < 0 || index >= values.Length)
        {
            return 0;
        }

        return values[index];
    }

    private static int NormalizeOptionIndex(ushort rawValue, int maximumIndex)
    {
        return rawValue <= maximumIndex ? rawValue : 0;
    }

    private static ushort GetReactivePowerTypeRaw(int selectedIndex)
    {
        return selectedIndex switch
        {
            1 => 0x00A1,
            2 => 0x00A2,
            _ => 0x00A0
        };
    }

    private static int GetReactivePowerTypeIndex(ushort rawValue)
    {
        return rawValue switch
        {
            0x00A1 => 1,
            0x00A2 => 2,
            _ => 0
        };
    }

    private string GetInverterTargetName()
    {
        return SelectedInverterTargetIndex switch
        {
            0 => "Inverter 1",
            1 => "Inverter 2",
            2 => "Inverter 1 + 2",
            _ => "Inverter"
        };
    }

    [RelayCommand]
    private async Task ReadAllEssControlTableAsync()
    {
        await ReadControlTableRowsAsync(
            EssControlTableRows,
            onlyChecked: false,
            isEssTable: true);
    }

    [RelayCommand]
    private async Task ReadCheckedEssControlTableAsync()
    {
        await ReadControlTableRowsAsync(
            EssControlTableRows,
            onlyChecked: true,
            isEssTable: true);
    }

    [RelayCommand]
    private async Task WriteCheckedEssControlTableAsync()
    {
        await WriteControlTableRowsAsync(
            EssControlTableRows,
            isEssTable: true);
    }

    [RelayCommand]
    private void CheckAllEssControlTable()
    {
        foreach (AdminControlTableRow row in EssControlTableRows)
        {
            row.IsChecked = true;
        }
    }

    [RelayCommand]
    private void UncheckAllEssControlTable()
    {
        foreach (AdminControlTableRow row in EssControlTableRows)
        {
            row.IsChecked = false;
        }
    }

    [RelayCommand]
    private void ClearEssControlTableWValues()
    {
        foreach (AdminControlTableRow row in EssControlTableRows)
        {
            row.WValue = string.Empty;
        }

        EssControlTableStatusText = "ESS 제어 W.Value를 모두 지웠습니다.";
    }

    [RelayCommand]
    private async Task ReadAllInverterControlTableAsync()
    {
        await ReadControlTableRowsAsync(
            InverterControlTableRows,
            onlyChecked: false,
            isEssTable: false);
    }

    [RelayCommand]
    private async Task ReadCheckedInverterControlTableAsync()
    {
        await ReadControlTableRowsAsync(
            InverterControlTableRows,
            onlyChecked: true,
            isEssTable: false);
    }

    [RelayCommand]
    private async Task WriteCheckedInverterControlTableAsync()
    {
        await WriteControlTableRowsAsync(
            InverterControlTableRows,
            isEssTable: false);
    }

    [RelayCommand]
    private void CheckAllInverterControlTable()
    {
        foreach (AdminControlTableRow row in InverterControlTableRows)
        {
            row.IsChecked = true;
        }
    }

    [RelayCommand]
    private void UncheckAllInverterControlTable()
    {
        foreach (AdminControlTableRow row in InverterControlTableRows)
        {
            row.IsChecked = false;
        }
    }

    [RelayCommand]
    private void ClearInverterControlTableWValues()
    {
        foreach (AdminControlTableRow row in InverterControlTableRows)
        {
            row.WValue = string.Empty;
        }

        InverterControlTableStatusText = "인버터 제어 W.Value를 모두 지웠습니다.";
    }

    private async Task ReadControlTableRowsAsync(
        ObservableCollection<AdminControlTableRow> rows,
        bool onlyChecked,
        bool isEssTable)
    {
        if (!TryBeginControlTableOperation(isEssTable, isWrite: false))
        {
            return;
        }

        int readCount = 0;

        try
        {
            SetControlTableStatus(
                isEssTable,
                onlyChecked ? "체크된 항목 읽는 중..." : "전체 항목 읽는 중...");

            // 같은 Address를 가진 Bit 행들은 Register 하나를 공유합니다.
            // 예: 30001 B0~2, B3, B4, B5, B6 ...
            // 행마다 FC03을 날리면 30001을 여러 번 읽게 되고, EMS가 중간에 값을 갱신하면
            // 한 화면 안에서도 R.Value가 흔들립니다. 그래서 Address별로 한 번만 읽고,
            // 같은 주소의 모든 행에 같은 Raw를 적용합니다.
            Dictionary<ushort, bool> addressesToRead = new();

            foreach (AdminControlTableRow row in rows)
            {
                if (onlyChecked && !row.IsChecked)
                {
                    continue;
                }

                addressesToRead[row.Address] = true;
            }

            foreach (ushort address in addressesToRead.Keys)
            {
                ushort logicalRaw = await _emsService.ReadControlTableRegisterAsync(address);

                _controlTableShadowRawByAddress[address] = logicalRaw;
                RefreshControlTableRowsByAddress(rows, address, logicalRaw);

                foreach (AdminControlTableRow row in rows)
                {
                    if (row.Address == address && (!onlyChecked || row.IsChecked))
                    {
                        readCount++;
                    }
                }
            }

            SetControlTableStatus(
                isEssTable,
                $"Read 완료 · {readCount}개 항목");
            AddLog($"{GetControlTableName(isEssTable)} 테이블 Read 완료 · {readCount}개");
        }
        catch (Exception ex)
        {
            SetControlTableStatus(
                isEssTable,
                $"Read 실패 · {ex.Message}");
            AddLog($"{GetControlTableName(isEssTable)} 테이블 Read 실패 · {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            EndControlTableOperation(isEssTable);
        }
    }

    private async Task WriteControlTableRowsAsync(
        ObservableCollection<AdminControlTableRow> rows,
        bool isEssTable)
    {
        if (!TryBeginControlTableOperation(isEssTable, isWrite: true))
        {
            return;
        }

        int writeCount = 0;

        try
        {
            SetControlTableStatus(isEssTable, "체크된 W.Value 쓰는 중...");

            // BitField는 Address별로 묶어서 한 번만 전체 Register를 만들고 씁니다.
            // 30001/30002는 한 Register 안에 여러 항목이 들어 있으므로,
            // Write 직전 실제값을 읽고 선택한 항목의 Bit만 바꿔야 합니다.
            writeCount += await WriteCheckedBitFieldGroupsAsync(rows);

            foreach (AdminControlTableRow row in rows)
            {
                if (!row.IsWriteEnabled || !row.IsChecked || string.IsNullOrWhiteSpace(row.WValue))
                {
                    continue;
                }

                if (row.WriteKind == AdminControlWriteKind.BitField)
                {
                    // BitField는 위에서 Address별 그룹으로 이미 처리했습니다.
                    continue;
                }

                if (row.WriteKind == AdminControlWriteKind.Coil)
                {
                    bool coilValue = ParseControlTableBool(row.WValue, row.ItemName);

                    await _emsService.WriteControlTableCoilAsync(
                        row.Address,
                        coilValue);

                    row.RawValue = coilValue ? "0xFF00 (ON)" : "0x0000 (OFF)";
                    row.RValue = coilValue ? "1" : "0";
                }
                else
                {
                    ushort raw = BuildControlTableRaw(row);

                    await _emsService.WriteControlTableRegisterAsync(
                        row.Address,
                        raw);

                    _controlTableShadowRawByAddress[row.Address] = raw;
                    row.FormatReadValue(raw);
                }

                writeCount++;
            }

            SetControlTableStatus(
                isEssTable,
                $"Write 완료 · {writeCount}개 항목");
            AddLog($"{GetControlTableName(isEssTable)} 테이블 Write 완료 · {writeCount}개");
        }
        catch (Exception ex)
        {
            SetControlTableStatus(
                isEssTable,
                $"Write 실패 · {ex.Message}");
            AddLog($"{GetControlTableName(isEssTable)} 테이블 Write 실패 · {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            EndControlTableOperation(isEssTable);
        }
    }

    private async Task<int> WriteCheckedBitFieldGroupsAsync(
        ObservableCollection<AdminControlTableRow> rows)
    {
        Dictionary<ushort, List<AdminControlTableRow>> groups = new();

        foreach (AdminControlTableRow row in rows)
        {
            if (!row.IsWriteEnabled ||
                !row.IsChecked ||
                string.IsNullOrWhiteSpace(row.WValue) ||
                row.WriteKind != AdminControlWriteKind.BitField)
            {
                continue;
            }

            if (!groups.TryGetValue(row.Address, out List<AdminControlTableRow>? groupRows))
            {
                groupRows = new List<AdminControlTableRow>();
                groups[row.Address] = groupRows;
            }

            groupRows.Add(row);
        }

        int writeCount = 0;

        foreach (KeyValuePair<ushort, List<AdminControlTableRow>> group in groups)
        {
            // 가장 안전한 방식: Write 직전에 실제 EMS 값을 다시 읽고,
            // 체크된 BitField만 바꾼 뒤 같은 Address Register를 한 번만 씁니다.
            // 이렇게 해야 선택하지 않은 Bit가 이전 Shadow 값 때문에 꺼지거나 켜지지 않습니다.
            ushort baseRaw = await _emsService.ReadControlTableRegisterAsync(group.Key);
            ushort newRaw = baseRaw;

            foreach (AdminControlTableRow row in group.Value)
            {
                newRaw = BuildControlTableBitFieldRaw(row, newRaw);
            }

            AddLog(
                $"Control Table Bit Write Plan · Address={group.Key} · " +
                $"Before=0x{baseRaw:X4} · After=0x{newRaw:X4} · " +
                $"Selected={group.Value.Count}개 · " +
                $"UnselectedBits=Preserved · " +
                $"BitOrder=Standard · B0=0x0001, B8=0x0100");

            if (newRaw != baseRaw)
            {
                await _emsService.WriteControlTableRegisterAsync(group.Key, newRaw);
            }
            else
            {
                AddLog(
                    $"Control Table Bit Write 생략 · Address={group.Key} · 변경값 없음");
            }

            // 화면은 우리가 계산한 값이 아니라 EMS에서 다시 읽은 값으로 갱신합니다.
            // EMS가 내부 로직으로 값을 다시 바꾸면 여기서 바로 드러납니다.
            ushort confirmedRaw = await ReadControlTableRegisterWithRetryAsync(group.Key, newRaw);

            _controlTableShadowRawByAddress[group.Key] = confirmedRaw;
            RefreshControlTableRowsByAddress(rows, group.Key, confirmedRaw);

            AddLog(
                $"Control Table Bit Confirm · Address={group.Key} · " +
                $"Expected=0x{newRaw:X4} · ReadBack=0x{confirmedRaw:X4}");

            writeCount += group.Value.Count;
        }

        return writeCount;
    }

    private async Task<ushort> ReadControlTableRegisterWithRetryAsync(
        ushort address,
        ushort expectedRaw)
    {
        ushort lastRaw = 0;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(120);
            }

            lastRaw = await _emsService.ReadControlTableRegisterAsync(address);

            if (lastRaw == expectedRaw)
            {
                return lastRaw;
            }
        }

        return lastRaw;
    }

    private bool TryBeginControlTableOperation(bool isEssTable, bool isWrite)
    {
        if (!_emsService.IsConnected)
        {
            SetControlTableStatus(
                isEssTable,
                "EMS 통신 미연결 · 먼저 연결하세요.");
            return false;
        }

        if (isEssTable)
        {
            if (IsEssControlTableBusy)
            {
                return false;
            }

            IsEssControlTableBusy = true;
        }
        else
        {
            if (IsInverterControlTableBusy)
            {
                return false;
            }

            IsInverterControlTableBusy = true;
        }

        if (isWrite)
        {
            AddLog($"{GetControlTableName(isEssTable)} 테이블 Write 시작");
        }

        return true;
    }

    private void EndControlTableOperation(bool isEssTable)
    {
        if (isEssTable)
        {
            IsEssControlTableBusy = false;
        }
        else
        {
            IsInverterControlTableBusy = false;
        }
    }

    private void SetControlTableStatus(bool isEssTable, string statusText)
    {
        if (isEssTable)
        {
            EssControlTableStatusText = statusText;
        }
        else
        {
            InverterControlTableStatusText = statusText;
        }
    }

    private static string GetControlTableName(bool isEssTable)
        => isEssTable ? "ESS 제어" : "인버터 제어";

    private static ushort BuildControlTableRaw(AdminControlTableRow row)
    {
        bool parsed =
            double.TryParse(
                row.WValue,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out double displayValue) ||
            double.TryParse(
                row.WValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out displayValue);

        if (!parsed)
        {
            throw new FormatException(
                $"{row.ItemName} W.Value '{row.WValue}'를 숫자로 읽을 수 없습니다.");
        }

        double scale = row.Scale == 0 ? 1.0 : row.Scale;
        int raw = checked((int)Math.Round(displayValue / scale, MidpointRounding.AwayFromZero));

        if (row.IsSigned)
        {
            if (raw is < short.MinValue or > short.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    row.ItemName,
                    $"{row.ItemName} Raw 값이 INT16 범위를 벗어났습니다.");
            }

            return unchecked((ushort)(short)raw);
        }

        if (raw is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                row.ItemName,
                $"{row.ItemName} Raw 값이 UINT16 범위를 벗어났습니다.");
        }

        return (ushort)raw;
    }

    private static ushort BuildControlTableBitFieldRaw(AdminControlTableRow row, ushort currentRaw)
    {
        bool parsed =
            int.TryParse(
                row.WValue,
                NumberStyles.Integer,
                CultureInfo.CurrentCulture,
                out int fieldValue) ||
            int.TryParse(
                row.WValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out fieldValue);

        if (!parsed)
        {
            throw new FormatException(
                $"{row.ItemName} W.Value '{row.WValue}'를 정수로 읽을 수 없습니다.");
        }

        if (row.BitPosition < 0 || row.BitPosition > 15 ||
            row.BitLength <= 0 || row.BitLength > 16 ||
            row.BitPosition + row.BitLength > 16)
        {
            throw new InvalidOperationException(
                $"{row.ItemName} BitField 설정이 잘못되었습니다.");
        }

        return row.ApplyBitFieldValue(currentRaw, fieldValue);
    }

    private static void RefreshControlTableRowsByAddress(
        ObservableCollection<AdminControlTableRow> rows,
        ushort address,
        ushort logicalRaw)
    {
        foreach (AdminControlTableRow row in rows)
        {
            if (row.Address == address)
            {
                row.FormatReadValue(logicalRaw);
            }
        }
    }

    private static bool ParseControlTableBool(string text, string fieldName)
    {
        string normalized = text.Trim().ToLowerInvariant();

        if (normalized is "1" or "on" or "true" or "enable")
        {
            return true;
        }

        if (normalized is "0" or "off" or "false" or "disable")
        {
            return false;
        }

        throw new FormatException(
            $"{fieldName} W.Value는 0/1, on/off, enable/disable 중 하나여야 합니다.");
    }

    [RelayCommand]
    private void ExitAdmin()
    {
        _exitAdminAction?.Invoke();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshStatusCoreAsync(isManualRefresh: false);
    }

    private static void ApplyRows(
        ObservableCollection<AdminRegisterRow> rows,
        ushort[] values,
        ushort startAddress)
    {
        foreach (AdminRegisterRow row in rows)
        {
            row.Update(values, startAddress);
        }
    }

    private static void ApplyRows(
        ObservableCollection<AdminComparisonRow> rows,
        ushort[] leftValues,
        ushort[] rightValues,
        ushort startAddress)
    {
        foreach (AdminComparisonRow row in rows)
        {
            int index = row.RelativeAddress - startAddress;

            if (index >= 0 && index < leftValues.Length && index < rightValues.Length)
            {
                row.Update(leftValues[index], rightValues[index]);
            }
        }
    }

    private void BuildStatusRows()
    {
        // =====================================================
        // EMS: Absolute 31001 ~ 31049 / Relative 0 ~ 48
        // =====================================================

        Add(SystemRows, 0, "31001", "ChargingMaxLimitVoltage", "V", "UINT16", 0.01, false, 2);
        Add(SystemRows, 1, "31002", "Charging MaxCurrent Limit per Pack", "A", "INT16", 0.01, true, 2);
        Add(SystemRows, 2, "31003", "Discharging Min Limit Voltage", "V", "UINT16", 0.01, false, 2);
        Add(SystemRows, 3, "31004", "Discharging Current limit per Pack", "A", "INT16", 0.01, true, 2);

        Add(SystemRows, 4, "31005", "Number of Installed Pack1", "EA", "UINT16", 1.0, false, 0);
        Add(SystemRows, 5, "31006", "Number of Active pack2", "EA", "UINT16", 1.0, false, 0);

        Add(SystemRows, 6, "31007", "Target Chg SOC", "%", "UINT16", 0.1, false, 1);
        Add(SystemRows, 7, "31008", "Target Dchg SOC", "%", "UINT16", 0.1, false, 1);

        Add(SystemRows, 8, "31009", "Ac Max Chg Power Limit", "kW", "INT16", 0.01, true, 2);
        Add(SystemRows, 9, "31010", "Ac Max Dchg Power Limit", "kW", "INT16", 0.01, true, 2);

        Add(SystemRows, 10, "31011", "System Voltage", "V", "UINT16", 0.01, false, 2);
        Add(SystemRows, 11, "31012", "System Current", "A", "INT16", 0.01, true, 2);
        Add(SystemRows, 12, "31013", "System Maximum Temperature", "°C", "INT16", 0.1, true, 1);
        Add(SystemRows, 13, "31014", "System Minimum Temperature", "°C", "INT16", 0.1, true, 1);

        Add(SystemRows, 14, "31015", "System Usable Capacity", "Ah", "UINT16", 0.01, false, 2);
        Add(SystemRows, 15, "31016", "System Remaining Capacity", "Ah", "UINT16", 0.01, false, 2);

        Add(SystemRows, 16, "31017", "System SOC", "%", "UINT16", 0.1, false, 1);
        Add(SystemRows, 17, "31018", "System SOH", "%", "UINT16", 0.1, false, 1);

        Add(SystemRows, 18, "31019", "System Iso R", "kΩ", "UINT16", 10.0, false, 1);
        Add(SystemRows, 19, "31020", "Cell Max Volt Value of Active Pack", "mV", "UINT16", 1.0, false, 0);
        Add(SystemRows, 20, "31021", "Cell Min Volt Value of Active Pack", "mV", "UINT16", 1.0, false, 0);

        AddBit(SystemRows, 21, "31022", "System Status1", AdminBitFieldDecoder.DecodeSystemStatus1);
        AddBit(SystemRows, 22, "31023", "System Status2", AdminBitFieldDecoder.DecodeSystemStatus2);

        AddBit(SystemRows, 23, "31024", "System Alarms 1", AdminBitFieldDecoder.DecodeSystemAlarm1);
        AddBit(SystemRows, 24, "31025", "System Alarms 2", AdminBitFieldDecoder.DecodeSystemAlarm2);

        AddBit(SystemRows, 25, "31026", "Battery Pack 1 Alarms 1", AdminBitFieldDecoder.DecodePack1Alarm1);
        AddBit(SystemRows, 26, "31027", "Battery Pack 1 Alarms 2", AdminBitFieldDecoder.DecodePack1Alarm2);
        AddBit(SystemRows, 27, "31028", "Battery Pack 1 Alarms 3", AdminBitFieldDecoder.DecodePack1Alarm3);

        AddBit(SystemRows, 28, "31029", "Battery Pack 2 Alarms 1", AdminBitFieldDecoder.DecodePack1Alarm1);
        AddBit(SystemRows, 29, "31030", "Battery Pack 2 Alarms 2", AdminBitFieldDecoder.DecodePack1Alarm2);
        AddBit(SystemRows, 30, "31031", "Battery Pack 2 Alarms 3", AdminBitFieldDecoder.DecodePack1Alarm3);

        AddBit(SystemRows, 31, "31032", "Battery Pack 1 Status 1", AdminBitFieldDecoder.DecodePackStatus1);
        AddBit(SystemRows, 32, "31033", "Battery Pack 1 Status 2", AdminBitFieldDecoder.DecodePackStatus2);

        AddBit(SystemRows, 33, "31034", "Battery Pack 2 Status 1", AdminBitFieldDecoder.DecodePackStatus1);
        AddBit(SystemRows, 34, "31035", "Battery Pack 2 Status 2", AdminBitFieldDecoder.DecodePackStatus2);

        Add(SystemRows, 35, "31036", "Number of Chargeable Pack", "EA", "UINT16", 1.0, false, 0);
        Add(SystemRows, 36, "31037", "Number of Dischargeable Pack", "EA", "UINT16", 1.0, false, 0);

        // =====================================================
        // ESS Profile Information: 31050 ~ 31061
        // 문자열은 Register 1개당 2글자(상/하위 Byte)씩 담기고,
        // 전체 문자열은 Register 주소 역순으로 조합해야 합니다.
        // =====================================================

        AddText(
            SystemRows,
            49,
            "31050 ~ 31053",
            "Manufacturer Name",
            4,
            FormatAsciiText);

        AddText(
            SystemRows,
            53,
            "31054 ~ 31057",
            "Device Code",
            4,
            FormatAsciiText);

        AddFormatted(
            SystemRows,
            57,
            "31058",
            "Manufacturer Year, Month",
            "-",
            "UINT16",
            FormatManufacturerDate);

        Add(SystemRows, 58, "31059", "Serial Number", "-", "UINT16", 1.0, false, 0);

        AddFormatted(
            SystemRows,
            59,
            "31060",
            "Firmware Version of EMS",
            "Ver",
            "UINT16",
            FormatMajorMinorVersion);

        AddFormatted(
            SystemRows,
            60,
            "31061",
            "Hardware Version of EMS",
            "Ver",
            "UINT16",
            FormatMajorMinorVersion);

        // =====================================================
        // 비교 화면: Pack 1 / Pack 2, Inverter 1 / Inverter 2
        // =====================================================

        BuildBatteryComparisonRows();
        BuildInverterComparisonRows();
    }

    private void BuildControlTableRows()
    {
        // 30001 / 30002는 Bit Field Register입니다.
        // 사용자가 W.Value에 256 같은 Raw 값을 계산해서 넣지 않도록,
        // 각 bit/enum 항목을 한 줄씩 나누고 Write 시 Read-Modify-Write로 처리합니다.
        AddBitControl(EssControlTableRows, 30001, "Operating Mode", "-", 0, 3, "0=Standby, 1=AC 자동 충전, 2=수동 제어, 3=AC 외부 전원출력, 4=계통 방전, 5=DC 차량 급속충전, 6=DC ESS 급속충전, 7=Reserved");
        AddBitControl(EssControlTableRows, 30001, "Battery Pack 1 Power", "-", 3, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30001, "Battery Pack 2 Power", "-", 4, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30001, "Inv1 Power", "-", 5, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30001, "Inv2 Power", "-", 6, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30001, "DC-DC1 Power", "-", 7, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30001, "DC-DC2 Power", "-", 8, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30001, "Reserved1", "-", 9, 1, "0=Off, 1=On · 확인 전용", isWriteEnabled: false);
        AddBitControl(EssControlTableRows, 30001, "Reserved2", "-", 10, 1, "0=Off, 1=On · 확인 전용", isWriteEnabled: false);
        AddBitControl(EssControlTableRows, 30001, "Reserved3", "-", 11, 1, "0=Off, 1=On · 확인 전용", isWriteEnabled: false);
        AddBitControl(EssControlTableRows, 30001, "SystemRunEn", "-", 12, 1, "0=Stop, 1=Run");
        AddBitControl(EssControlTableRows, 30001, "SystemOffEn", "-", 13, 1, "0=Active, 1=ShutDown");
        AddBitControl(EssControlTableRows, 30001, "SystemResetEn", "-", 14, 1, "0=Enable, 1=Disable");
        AddBitControl(EssControlTableRows, 30001, "Reserved4", "-", 15, 1, "0=Off, 1=On · 확인 전용", isWriteEnabled: false);

        AddBitControl(EssControlTableRows, 30002, "BMS1 Manual Enable", "-", 0, 1, "0=Disable, 1=Enable");
        AddBitControl(EssControlTableRows, 30002, "BMS2 Manual Enable", "-", 1, 1, "0=Disable, 1=Enable");
        AddBitControl(EssControlTableRows, 30002, "ESS Charge N Relay", "-", 2, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30002, "ESS Charge P Relay", "-", 3, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30002, "EV Charge P Relay", "-", 5, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30002, "EV Charge N Relay", "-", 6, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30002, "AC Main Contactor", "-", 7, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30002, "AC Neutral Switch", "-", 8, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30002, "Alarm Lamp", "-", 9, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30002, "Fault Lamp", "-", 10, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30002, "Buzzer", "-", 11, 1, "0=Off, 1=On");
        AddBitControl(EssControlTableRows, 30002, "Reserved2", "-", 12, 1, "0=Off, 1=On · 확인 전용", isWriteEnabled: false);
        AddBitControl(EssControlTableRows, 30002, "Reserved3", "-", 13, 1, "0=Off, 1=On · 확인 전용", isWriteEnabled: false);
        AddBitControl(EssControlTableRows, 30002, "Reserved4", "-", 14, 1, "0=Off, 1=On · 확인 전용", isWriteEnabled: false);
        AddBitControl(EssControlTableRows, 30002, "Reserved5", "-", 15, 1, "0=Off, 1=On · 확인 전용", isWriteEnabled: false);

        AddControl(EssControlTableRows, 30003, "Charging Max Limit Voltage", "V", "UINT16", 0.01, false, 2, "충전 최대 제한 전압");
        AddControl(EssControlTableRows, 30004, "Charging Max Current Limit per Pack", "A", "INT16", 0.01, true, 2, "Pack당 충전 전류 제한");
        AddControl(EssControlTableRows, 30005, "Discharging Min Limit Voltage", "V", "UINT16", 0.01, false, 2, "방전 최소 제한 전압");
        AddControl(EssControlTableRows, 30006, "Discharging Current Limit per Pack", "A", "INT16", 0.01, true, 2, "Pack당 방전 전류 제한");
        AddControl(EssControlTableRows, 30007, "Number of Installed Packs", "EA", "UINT16", 1.0, false, 0, "설치된 Pack 수");
        AddControl(EssControlTableRows, 30008, "Target Charge SOC", "%", "UINT16", 0.1, false, 1, "목표 충전 SOC");
        AddControl(EssControlTableRows, 30009, "Target Discharge SOC", "%", "UINT16", 0.1, false, 1, "목표 방전 SOC");
        AddControl(EssControlTableRows, 30010, "AC Max Charge Power Limit", "kW", "INT16", 0.01, true, 2, "AC 최대 충전 전력");
        AddControl(EssControlTableRows, 30011, "AC Max Discharge Power On Grid", "kW", "INT16", 0.01, true, 2, "계통 방전 최대 전력");
        AddControl(EssControlTableRows, 30012, "AC Max Discharge Power Off Grid", "kW", "INT16", 0.01, true, 2, "외부 출력 최대 전력");

        AddInverterControlSet("INV1", 40000, InverterControlTableRows);
        AddInverterControlSet("INV2", 41000, InverterControlTableRows);
    }

    private static void AddInverterControlSet(
        string prefix,
        int baseAddress,
        ObservableCollection<AdminControlTableRow> rows)
    {
        AddControl(rows, (ushort)(baseAddress + 24), $"{prefix} Operating Altitude", "m", "UINT16", 1.0, false, 0, "Operating altitude");
        AddControl(rows, (ushort)(baseAddress + 25), $"{prefix} Group Number", "-", "UINT16", 1.0, false, 0, "Group number");
        AddControl(rows, (ushort)(baseAddress + 26), $"{prefix} Address Allocation", "-", "UINT16", 1.0, false, 0, "0=Auto, 1=Dial");
        AddControl(rows, (ushort)(baseAddress + 27), $"{prefix} AC Under Voltage Reset", "-", "UINT16", 1.0, false, 0, "1=Reset");
        AddControl(rows, (ushort)(baseAddress + 28), $"{prefix} Rectifier DC Under Voltage Reset", "-", "UINT16", 1.0, false, 0, "1=Reset");
        AddControl(rows, (ushort)(baseAddress + 29), $"{prefix} Working Mode", "-", "UINT16", 1.0, false, 0, "0=Grid, 1=Off-grid, 2=Rectifier");
        AddControl(rows, (ushort)(baseAddress + 30), $"{prefix} Power On/Off", "-", "UINT16", 1.0, false, 0, "0=Power On, 1=Shutdown");
        AddControl(rows, (ushort)(baseAddress + 31), $"{prefix} DC Over Voltage Reset", "-", "UINT16", 1.0, false, 0, "1=Reset");
        AddControl(rows, (ushort)(baseAddress + 34), $"{prefix} Inverter DC Under Voltage Reset", "-", "UINT16", 1.0, false, 0, "1=Reset");
        AddControl(rows, (ushort)(baseAddress + 36), $"{prefix} Short Circuit Reset", "-", "UINT16", 1.0, false, 0, "1=Reset");
        AddControl(rows, (ushort)(baseAddress + 39), $"{prefix} DC Link Voltage", "V", "UINT16", 0.1, false, 1, "DC link voltage");
        AddControl(rows, (ushort)(baseAddress + 40), $"{prefix} DC Current", "A", "INT16", 0.01, true, 2, "DC current");
        AddControl(rows, (ushort)(baseAddress + 41), $"{prefix} AC Side Power Control", "-", "UINT16", 1.0, false, 0, "0=DC side, 1=AC side");
        AddControl(rows, (ushort)(baseAddress + 42), $"{prefix} AC Active Power", "W", "INT16", 1.0, true, 0, "AC active power");
        AddControl(rows, (ushort)(baseAddress + 43), $"{prefix} AC Reactive Power", "Var", "INT16", 10.0, true, 0, "AC reactive power");
        AddControl(rows, (ushort)(baseAddress + 44), $"{prefix} AC Power Factor", "PF", "INT16", 0.01, true, 2, "Power factor");
        AddControl(rows, (ushort)(baseAddress + 45), $"{prefix} Reactive Power Type", "-", "UINT16", 1.0, false, 0, "0x00A0/0x00A1/0x00A2");
        AddControl(rows, (ushort)(baseAddress + 46), $"{prefix} Rated Phase Voltage", "V", "INT16", 0.1, true, 1, "Rated phase voltage");
        AddControl(rows, (ushort)(baseAddress + 47), $"{prefix} Rated AC Frequency", "Hz", "UINT16", 0.001, false, 3, "Rated AC frequency");
        AddControl(rows, (ushort)(baseAddress + 48), $"{prefix} Phase Error Allow", "-", "UINT16", 1.0, false, 0, "0=Disable, 1=Enable");
        AddControl(rows, (ushort)(baseAddress + 49), $"{prefix} Island Detection", "-", "UINT16", 1.0, false, 0, "0=Enable, 1=Disable");
        AddControl(rows, (ushort)(baseAddress + 50), $"{prefix} DC Under Voltage Protection", "V", "INT16", 0.1, true, 1, "Protection value");
        AddControl(rows, (ushort)(baseAddress + 51), $"{prefix} DC Over Voltage Protection", "V", "INT16", 0.1, true, 1, "Protection value");
        AddControl(rows, (ushort)(baseAddress + 52), $"{prefix} AC Under Voltage Protection", "V", "INT16", 0.1, true, 1, "Protection value");
        AddControl(rows, (ushort)(baseAddress + 53), $"{prefix} AC Under Voltage Time", "s", "INT16", 0.01, true, 2, "Protection time");
        AddControl(rows, (ushort)(baseAddress + 54), $"{prefix} AC Over Voltage Protection", "V", "INT16", 0.1, true, 1, "Protection value");
        AddControl(rows, (ushort)(baseAddress + 55), $"{prefix} AC Over Voltage Time", "s", "INT16", 0.01, true, 2, "Protection time");
        AddControl(rows, (ushort)(baseAddress + 56), $"{prefix} 1st AC Under Frequency", "Hz", "INT16", 0.01, true, 2, "Protection value");
        AddControl(rows, (ushort)(baseAddress + 57), $"{prefix} 1st Under Frequency Time", "s", "INT16", 0.01, true, 2, "Protection time");
        AddControl(rows, (ushort)(baseAddress + 58), $"{prefix} 1st AC Over Frequency", "Hz", "INT16", 0.01, true, 2, "Protection value");
        AddControl(rows, (ushort)(baseAddress + 59), $"{prefix} 1st Over Frequency Time", "s", "INT16", 0.01, true, 2, "Protection time");
        AddControl(rows, (ushort)(baseAddress + 60), $"{prefix} 2nd AC Under Frequency", "Hz", "INT16", 0.01, true, 2, "Protection value");
        AddControl(rows, (ushort)(baseAddress + 61), $"{prefix} 2nd Under Frequency Time", "s", "INT16", 0.01, true, 2, "Protection time");
        AddControl(rows, (ushort)(baseAddress + 62), $"{prefix} 2nd AC Over Frequency", "Hz", "INT16", 0.01, true, 2, "Protection value");
        AddControl(rows, (ushort)(baseAddress + 63), $"{prefix} 2nd Over Frequency Time", "s", "INT16", 0.01, true, 2, "Protection time");
    }

    private static void AddBitControl(
        ObservableCollection<AdminControlTableRow> rows,
        ushort address,
        string itemName,
        string unit,
        int bitPosition,
        int bitLength,
        string remark,
        bool isWriteEnabled = true)
    {
        string addressText = bitLength == 1
            ? $"{address} Bit{bitPosition}"
            : $"{address} Bit{bitPosition}~{bitPosition + bitLength - 1}";

        rows.Add(new AdminControlTableRow
        {
            Address = address,
            ItemName = itemName,
            Unit = unit,
            DataType = "Bit Field",
            Scale = 1.0,
            IsSigned = false,
            DecimalPlaces = 0,
            Remark = $"{addressText} / {remark}",
            WriteKind = AdminControlWriteKind.BitField,
            BitPosition = bitPosition,
            BitLength = bitLength,
            IsWriteEnabled = isWriteEnabled
        });
    }

    private static void AddControl(
        ObservableCollection<AdminControlTableRow> rows,
        ushort address,
        string itemName,
        string unit,
        string dataType,
        double scale,
        bool isSigned,
        int decimalPlaces,
        string remark,
        AdminControlWriteKind writeKind = AdminControlWriteKind.Register)
    {
        rows.Add(new AdminControlTableRow
        {
            Address = address,
            ItemName = itemName,
            Unit = unit,
            DataType = dataType,
            Scale = scale,
            IsSigned = isSigned,
            DecimalPlaces = decimalPlaces,
            Remark = remark,
            WriteKind = writeKind
        });
    }

    private void BuildBatteryComparisonRows()
    {
        const int pack1Base = 32000;
        const int pack2Base = 33000;

        AddPair(BatteryRows, 0, AddressPair(pack1Base, pack2Base, 0), "Voltage", "V", "UINT16", 0.01, false, 2, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 1, AddressPair(pack1Base, pack2Base, 1), "Current", "A", "INT16", 0.01, true, 2, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 2, AddressPair(pack1Base, pack2Base, 2), "Usable Capacity", "Ah", "UINT16", 0.01, false, 2, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 3, AddressPair(pack1Base, pack2Base, 3), "Remaining Capacity", "Ah", "UINT16", 0.01, false, 2, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 4, AddressPair(pack1Base, pack2Base, 4), "SOC", "%", "UINT16", 0.1, false, 1, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 5, AddressPair(pack1Base, pack2Base, 5), "SOH", "%", "UINT16", 0.1, false, 1, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 6, AddressPair(pack1Base, pack2Base, 6), "Cycle Count", "Cycle", "UINT16", 1.0, false, 0, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 7, AddressPair(pack1Base, pack2Base, 7), "Cell Max Temperature", "°C", "INT16", 0.1, true, 1, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 8, AddressPair(pack1Base, pack2Base, 8), "Cell Min Temperature", "°C", "INT16", 0.1, true, 1, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 9, AddressPair(pack1Base, pack2Base, 9), "Cell Max Temperature ID", "Cell ID", "UINT8 (High Byte)", 1.0, false, 0, raw => ((byte)(raw >> 8)).ToString(), "Pack 1", "Pack 2");
        AddPair(BatteryRows, 9, AddressPair(pack1Base, pack2Base, 9), "Cell Min Temperature ID", "Cell ID", "UINT8 (Low Byte)", 1.0, false, 0, raw => ((byte)(raw & 0x00FF)).ToString(), "Pack 1", "Pack 2");
        AddPair(BatteryRows, 10, AddressPair(pack1Base, pack2Base, 10), "Cell Max Voltage", "mV", "UINT16", 1.0, false, 0, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 11, AddressPair(pack1Base, pack2Base, 11), "Cell Min Voltage", "mV", "UINT16", 1.0, false, 0, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 12, AddressPair(pack1Base, pack2Base, 12), "Cell Max Voltage ID", "Cell ID", "UINT8 (High Byte)", 1.0, false, 0, raw => ((byte)(raw >> 8)).ToString(), "Pack 1", "Pack 2");
        AddPair(BatteryRows, 12, AddressPair(pack1Base, pack2Base, 12), "Cell Min Voltage ID", "Cell ID", "UINT8 (Low Byte)", 1.0, false, 0, raw => ((byte)(raw & 0x00FF)).ToString(), "Pack 1", "Pack 2");
        AddPair(BatteryRows, 13, AddressPair(pack1Base, pack2Base, 13), "Allowed Sust Chg Power", "kW", "UINT16", 0.01, false, 2, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 14, AddressPair(pack1Base, pack2Base, 14), "Allowed Inst Chg Power", "kW", "UINT16", 0.01, false, 2, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 15, AddressPair(pack1Base, pack2Base, 15), "Allowed Sust Dchg Power", "kW", "UINT16", 0.01, false, 2, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 16, AddressPair(pack1Base, pack2Base, 16), "Allowed Inst Dchg Power", "kW", "UINT16", 0.01, false, 2, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 17, AddressPair(pack1Base, pack2Base, 17), "Iso R", "kΩ", "UINT16", 10, false, 1, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 18, AddressPair(pack1Base, pack2Base, 18), "Cumulative Charge", "kWh", "UINT16", 10.0, false, 0, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 19, AddressPair(pack1Base, pack2Base, 19), "Charge Times", "Cnt", "UINT16", 1.0, false, 0, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 20, AddressPair(pack1Base, pack2Base, 20), "Discharge UVP Count", "Cnt", "UINT16", 1.0, false, 0, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 21, AddressPair(pack1Base, pack2Base, 21), "Nominal Energy", "kWh", "UINT16", 0.01, false, 2, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 22, AddressPair(pack1Base, pack2Base, 22), "BMS FW Version", "-", "UINT16", 1.0, false, 0, null, "Pack 1", "Pack 2");
        AddPair(BatteryRows, 23, AddressPair(pack1Base, pack2Base, 23), "Manufacture Date", "-", "UINT16", 1.0, false, 0, FormatPackManufactureDate, "Pack 1", "Pack 2");
    }

    private void BuildInverterComparisonRows()
    {
        const int inverter1Base = 40000;
        const int inverter2Base = 41000;

        AddPair(InverterRows, 0, AddressPair(inverter1Base, inverter2Base, 0), "A Phase voltage", "V", "INT16", 0.1, true, 1, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 1, AddressPair(inverter1Base, inverter2Base, 1), "A Phase current", "A", "INT16", 0.01, true, 2, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 2, AddressPair(inverter1Base, inverter2Base, 2), "B Phase voltage", "V", "INT16", 0.1, true, 1, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 3, AddressPair(inverter1Base, inverter2Base, 3), "B Phase current", "A", "INT16", 0.01, true, 2, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 4, AddressPair(inverter1Base, inverter2Base, 4), "C Phase voltage", "V", "INT16", 0.1, true, 1, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 5, AddressPair(inverter1Base, inverter2Base, 5), "C Phase current", "A", "INT16", 0.01, true, 2, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 6, AddressPair(inverter1Base, inverter2Base, 6), "AB Line voltage", "V", "INT16", 0.1, true, 1, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 7, AddressPair(inverter1Base, inverter2Base, 7), "BC Line voltage", "V", "INT16", 0.1, true, 1, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 8, AddressPair(inverter1Base, inverter2Base, 8), "CA Line voltage", "V", "INT16", 0.1, true, 1, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 9, AddressPair(inverter1Base, inverter2Base, 9), "Phase A active power", "W", "UINT16", 1.0, false, 0, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 10, AddressPair(inverter1Base, inverter2Base, 10), "Phase A reactive power", "Var", "INT16", 1.0, true, 0, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 11, AddressPair(inverter1Base, inverter2Base, 11), "Phase B active power", "W", "UINT16", 1.0, false, 0, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 12, AddressPair(inverter1Base, inverter2Base, 12), "Phase B reactive power", "Var", "INT16", 1.0, true, 0, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 13, AddressPair(inverter1Base, inverter2Base, 13), "Phase C active power", "W", "UINT16", 1.0, false, 0, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 14, AddressPair(inverter1Base, inverter2Base, 14), "Phase C reactive power", "Var", "INT16", 1.0, true, 0, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 15, AddressPair(inverter1Base, inverter2Base, 15), "AC frequency", "Hz", "INT16", 0.01, true, 2, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 16, AddressPair(inverter1Base, inverter2Base, 16), "module panel (ambient) temperature", "°C", "INT16", 0.1, true, 1, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 17, AddressPair(inverter1Base, inverter2Base, 17), "total active power", "kW", "INT16", 0.01, true, 2, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 18, AddressPair(inverter1Base, inverter2Base, 18), "total reactive power", "kVar", "INT16", 0.01, true, 2, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 19, AddressPair(inverter1Base, inverter2Base, 19), "total apparent power", "kVA", "INT16", 0.01, true, 2, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 20, AddressPair(inverter1Base, inverter2Base, 20), "DC side voltage", "V", "UINT16", 0.1, false, 1, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 21, AddressPair(inverter1Base, inverter2Base, 21), "DC side current", "A", "INT16", 0.01, true, 2, null, "Inverter 1", "Inverter 2");
        AddPair(InverterRows, 22, AddressPair(inverter1Base, inverter2Base, 22), "rated output power", "W", "UINT16", 1.0, false, 0, null, "Inverter 1", "Inverter 2");
        AddBitPair(InverterRows, 31, AddressPair(inverter1Base, inverter2Base, 31), "AlarmStatus 1", AdminBitFieldDecoder.DecodeInverterAlarmStatus1, "Inverter 1", "Inverter 2");
        AddBitPair(InverterRows, 32, AddressPair(inverter1Base, inverter2Base, 32), "AlarmStatus 2", AdminBitFieldDecoder.DecodeInverterAlarmStatus2, "Inverter 1", "Inverter 2");
    }

    private static string AddressPair(int leftBase, int rightBase, int offset)
        => $"{leftBase + offset + 1} / {rightBase + offset + 1}";

    private static void Add(
        ObservableCollection<AdminRegisterRow> rows,
        ushort relativeAddress,
        string absoluteAddress,
        string name,
        string unit,
        string dataType,
        double scale,
        bool isSigned,
        int decimalPlaces)
    {
        rows.Add(new AdminRegisterRow
        {
            RelativeAddress = relativeAddress,
            AbsoluteAddressText = absoluteAddress,
            Name = name,
            Unit = unit,
            DataType = dataType,
            Scale = scale,
            IsSigned = isSigned,
            DecimalPlaces = decimalPlaces
        });
    }

    private static void AddFormatted(
        ObservableCollection<AdminRegisterRow> rows,
        ushort relativeAddress,
        string absoluteAddress,
        string name,
        string unit,
        string dataType,
        Func<ushort, string> valueFormatter)
    {
        rows.Add(new AdminRegisterRow
        {
            RelativeAddress = relativeAddress,
            AbsoluteAddressText = absoluteAddress,
            Name = name,
            Unit = unit,
            DataType = dataType,
            ValueFormatter = valueFormatter,
            DecimalPlaces = 0
        });
    }

    private static void AddText(
        ObservableCollection<AdminRegisterRow> rows,
        ushort relativeAddress,
        string absoluteAddress,
        string name,
        int wordLength,
        Func<ushort[], int, string> valuesFormatter)
    {
        rows.Add(new AdminRegisterRow
        {
            RelativeAddress = relativeAddress,
            AbsoluteAddressText = absoluteAddress,
            Name = name,
            Unit = "-",
            DataType = "Character",
            WordLength = wordLength,
            ValuesFormatter = valuesFormatter,
            DecimalPlaces = 0
        });
    }

    private static void AddBit(
        ObservableCollection<AdminRegisterRow> rows,
        ushort relativeAddress,
        string absoluteAddress,
        string name,
        Func<ushort, string> bitFieldDecoder)
    {
        rows.Add(new AdminRegisterRow
        {
            RelativeAddress = relativeAddress,
            AbsoluteAddressText = absoluteAddress,
            Name = name,
            Unit = "Bit",
            DataType = "Bit Field",
            IsBitField = true,
            BitFieldDecoder = bitFieldDecoder,
            DecimalPlaces = 0
        });
    }

    private static void AddPair(
        ObservableCollection<AdminComparisonRow> rows,
        ushort relativeAddress,
        string absoluteAddress,
        string name,
        string unit,
        string dataType,
        double scale,
        bool isSigned,
        int decimalPlaces,
        Func<ushort, string>? valueFormatter = null,
        string leftCaption = "1번",
        string rightCaption = "2번")
    {
        rows.Add(new AdminComparisonRow
        {
            RelativeAddress = relativeAddress,
            AbsoluteAddressText = absoluteAddress,
            Name = name,
            Unit = unit,
            DataType = dataType,
            Scale = scale,
            IsSigned = isSigned,
            DecimalPlaces = decimalPlaces,
            ValueFormatter = valueFormatter,
            LeftCaption = leftCaption,
            RightCaption = rightCaption
        });
    }

    private static void AddBitPair(
        ObservableCollection<AdminComparisonRow> rows,
        ushort relativeAddress,
        string absoluteAddress,
        string name,
        Func<ushort, string> bitFieldDecoder,
        string leftCaption = "1번",
        string rightCaption = "2번")
    {
        rows.Add(new AdminComparisonRow
        {
            RelativeAddress = relativeAddress,
            AbsoluteAddressText = absoluteAddress,
            Name = name,
            Unit = "Bit",
            DataType = "Bit Field",
            IsBitField = true,
            BitFieldDecoder = bitFieldDecoder,
            DecimalPlaces = 0,
            LeftCaption = leftCaption,
            RightCaption = rightCaption
        });
    }

    private static string FormatAsciiText(ushort[] values, int startIndex)

    {
        var builder = new StringBuilder();

        // EMS Profile 문자열(예: Manufacturer Name 31050~31053)은
        // 4개 Word가 역순으로 저장되어 있고, 각 Word 내부 바이트도 반대 순서로 들어온다.
        // 예:
        // 현재 수신값 : DN RC MK VE
        // 실제 표시값 : EV KM CR ND
        for (int i = 3; i >= 0; i--)
        {
            int index = startIndex + i;

            if (index < 0 || index >= values.Length)
            {
                continue;
            }

            ushort word = values[index];

            char low = (char)(word & 0x00FF);
            char high = (char)(word >> 8);

            if (low != '\0')
            {
                builder.Append(low);
            }

            if (high != '\0')
            {
                builder.Append(high);
            }
        }

        return builder.ToString().Trim();
    }
    private static string FormatManufacturerDate(ushort raw)
    {
        // "Manufacturer Year, Month" 예: 2608 -> Year 2026, Month 08
        int year = 2000 + (raw / 100);
        int month = raw % 100;

        if (month is < 1 or > 12)
        {
            return $"Invalid date raw ({raw})";
        }

        return $"{year:D4}-{month:D2}";
    }

    private static string FormatMajorMinorVersion(ushort raw)
    {
        byte major = (byte)(raw >> 8);
        byte minor = (byte)(raw & 0x00FF);

        return $"{major}.{minor}";
    }

    private static string FormatPackManufactureDate(ushort raw)
    {
        // 실제 BMS 수신값은 상위 Byte = Month, 하위 Byte = Year 순서로 들어옵니다.
        // 예: 0x0719 -> 2025-07
        int month = (byte)(raw >> 8);
        int shortYear = (byte)(raw & 0x00FF);
        int year = shortYear < 100 ? 2000 + shortYear : shortYear;

        if (month is < 1 or > 12)
        {
            return $"Invalid date raw (0x{raw:X4})";
        }

        return $"{year:D4}-{month:D2}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _emsService.CommunicationLogReceived -=
            EmsService_CommunicationLogReceived;

        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
    }
    private void AddLog(string message)
    {
        AppendLogLine(
            $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    private void AppendLogLine(string logLine)
    {
        _communicationLogBuilder.AppendLine(logLine);

        const int maxLength = 12000;

        if (_communicationLogBuilder.Length > maxLength)
        {
            _communicationLogBuilder.Remove(
                0,
                _communicationLogBuilder.Length - maxLength);
        }

        CommunicationLogText = _communicationLogBuilder.ToString();
    }
    private void EmsService_CommunicationLogReceived(
    string logLine)
    {
        if (_disposed)
        {
            return;
        }

        // Modbus 통신은 백그라운드 작업에서 실행될 수 있으므로
        // Avalonia UI 스레드에서 로그창을 갱신한다.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                AppendLogLine(logLine);
            }
        });
    }
    private void ClearLog()
    {
        _communicationLogBuilder.Clear();
        CommunicationLogText = string.Empty;

        AddLog("로그를 지웠습니다.");
    }
}
