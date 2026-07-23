using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MobileEssControl.Constants;
using MobileEssControl.Services.Dialogs;
using MobileEssControl.Services.Ems;
using System;
using System.Threading.Tasks;
using static MobileEssControl.Constants.EmsControlWord1;

namespace MobileEssControl.ViewModels.GridDischarge;

public enum GridDischargeFlowState
{
    Ready,
    Starting,
    Discharging,
    Stopping,
    Stopped,
    Completed,
    Fault,
    Disconnected
}

public partial class GridDischargeViewModel : ViewModelBase, IDisposable
{
    private readonly EmsService? _emsService;
    private readonly DispatcherTimer? _refreshTimer;

    private bool _isRefreshing;
    private bool _disposed;

    public GridDischargeViewModel()
    {
        // 디자인/미리보기 보호용
    }

    public GridDischargeViewModel(EmsService emsService)
    {
        _emsService = emsService;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();

        _ = RefreshStatusAsync();
    }

    [ObservableProperty]
    private GridDischargeFlowState dischargeState =
        GridDischargeFlowState.Ready;

    [ObservableProperty]
    private double dischargePowerKw = 10;

    [ObservableProperty]
    private double minSoc = 20;

    [ObservableProperty]
    private string modeStatus = "계통 방전 대기";

    [ObservableProperty]
    private string requestStatus =
        "시작 버튼을 누르면 계통 방전 요청을 전송합니다.";

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private double? currentSoc;

    [ObservableProperty]
    private double? currentDischargePowerKw;

    [ObservableProperty]
    private double? systemVoltage;

    [ObservableProperty]
    private double? systemCurrent;

    [ObservableProperty]
    private string threePhaseVoltageText =
        "-- / -- / -- V";

    [ObservableProperty]
    private string threePhaseCurrentText =
        "-- / -- / -- A";

    [ObservableProperty]
    private string gridFrequencyText =
        "-- Hz";

    public string DischargePowerText =>
        $"{DischargePowerKw:0} kW";

    public string MinSocText =>
        $"{MinSoc:0}%";

    public string CurrentSocText =>
        CurrentSoc.HasValue
            ? $"{CurrentSoc.Value:0.0}%"
            : "-- %";

    public string CurrentDischargePowerText =>
        CurrentDischargePowerKw.HasValue
            ? $"{CurrentDischargePowerKw.Value:0.00} kW"
            : "-- kW";

    public string SystemVoltageCurrentText =>
        SystemVoltage.HasValue &&
        SystemCurrent.HasValue
            ? $"{SystemVoltage.Value:0.0} V / {SystemCurrent.Value:0.0} A"
            : "-- V / -- A";

    public string StartButtonText =>
        DischargeState switch
        {
            GridDischargeFlowState.Starting => "시작 중",
            GridDischargeFlowState.Discharging => "방전 중",
            _ => "시작"
        };

    public string StopButtonText =>
        DischargeState == GridDischargeFlowState.Stopping
            ? "정지 중"
            : "정지";

    /// <summary>
    /// 시작 중, 방전 중, 정지 중에는 SOC와 방전전력 설정을 변경하지 못하게 합니다.
    /// </summary>
    public bool CanEditSettings =>
        DischargeState != GridDischargeFlowState.Starting &&
        DischargeState != GridDischargeFlowState.Discharging &&
        DischargeState != GridDischargeFlowState.Stopping;

    public string FlowBadgeText =>
        DischargeState switch
        {
            GridDischargeFlowState.Starting => "STARTING",
            GridDischargeFlowState.Discharging => "DISCHARGING",
            GridDischargeFlowState.Stopping => "STOPPING",
            GridDischargeFlowState.Stopped => "STOPPED",
            GridDischargeFlowState.Completed => "COMPLETED",
            GridDischargeFlowState.Fault => "FAULT",
            GridDischargeFlowState.Disconnected => "OFFLINE",
            _ => "READY"
        };

    public string FlowSubText =>
        DischargeState switch
        {
            GridDischargeFlowState.Starting =>
                "계통 방전 명령 전송 후 실제 운전 상태를 확인 중입니다.",

            GridDischargeFlowState.Discharging =>
                $"현재 방전전력 {CurrentDischargePowerText} · SOC {CurrentSocText}",

            GridDischargeFlowState.Stopping =>
                "정지 명령을 전송하고 Standby 상태를 확인 중입니다.",

            GridDischargeFlowState.Stopped =>
                "계통 방전이 정지되었습니다.",

            GridDischargeFlowState.Completed =>
                $"최저 SOC에 도달했습니다. 현재 SOC {CurrentSocText}",

            GridDischargeFlowState.Fault =>
                "계통 방전 시스템 이상이 감지되었습니다.",

            GridDischargeFlowState.Disconnected =>
                "EMS 통신이 연결되어 있지 않습니다.",

            _ =>
                "계통 방전 시작 전 상태값을 확인합니다."
        };

    public double DcArrowOpacity =>
        DischargeState == GridDischargeFlowState.Starting ||
        DischargeState == GridDischargeFlowState.Discharging
            ? 1.0
            : DischargeState == GridDischargeFlowState.Stopping
                ? 0.55
                : 0.22;

    public double AcArrowOpacity =>
        DischargeState == GridDischargeFlowState.Discharging
            ? 1.0
            : DischargeState == GridDischargeFlowState.Starting ||
              DischargeState == GridDischargeFlowState.Stopping
                ? 0.55
                : 0.22;

    public IBrush FlowBrush =>
        DischargeState switch
        {
            GridDischargeFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#DC2626")),

            GridDischargeFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#D97706")),

            GridDischargeFlowState.Discharging =>
                new SolidColorBrush(Color.Parse("#2563EB")),

            GridDischargeFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#EA580C")),

            GridDischargeFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#16A34A")),

            _ =>
                new SolidColorBrush(Color.Parse("#94A3B8"))
        };

    public IBrush BatteryBrush =>
        DischargeState switch
        {
            GridDischargeFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#DC2626")),

            GridDischargeFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#D97706")),

            GridDischargeFlowState.Discharging =>
                new SolidColorBrush(Color.Parse("#16A34A")),

            GridDischargeFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#EA580C")),

            GridDischargeFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#16A34A")),

            _ =>
                new SolidColorBrush(Color.Parse("#94A3B8"))
        };

    public IBrush FlowBackground =>
        DischargeState switch
        {
            GridDischargeFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#FEF2F2")),

            GridDischargeFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#FFF7ED")),

            GridDischargeFlowState.Discharging =>
                new SolidColorBrush(Color.Parse("#EFF6FF")),

            GridDischargeFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#FFF7ED")),

            GridDischargeFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#ECFDF5")),

            _ =>
                new SolidColorBrush(Color.Parse("#F8FAFC"))
        };

    public IBrush BatteryBackground =>
        DischargeState switch
        {
            GridDischargeFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#FEF2F2")),

            GridDischargeFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#FFF7ED")),

            GridDischargeFlowState.Discharging =>
                new SolidColorBrush(Color.Parse("#ECFDF5")),

            GridDischargeFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#FFF7ED")),

            GridDischargeFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#ECFDF5")),

            _ =>
                new SolidColorBrush(Color.Parse("#F8FAFC"))
        };

    public IBrush FlowBorderBrush =>
        DischargeState switch
        {
            GridDischargeFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#FCA5A5")),

            GridDischargeFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#FDBA74")),

            GridDischargeFlowState.Discharging =>
                new SolidColorBrush(Color.Parse("#93C5FD")),

            GridDischargeFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#FDBA74")),

            GridDischargeFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#86EFAC")),

            _ =>
                new SolidColorBrush(Color.Parse("#E2E8F0"))
        };

    public IBrush BatteryBorderBrush =>
        DischargeState switch
        {
            GridDischargeFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#FCA5A5")),

            GridDischargeFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#FDBA74")),

            GridDischargeFlowState.Discharging =>
                new SolidColorBrush(Color.Parse("#86EFAC")),

            GridDischargeFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#FDBA74")),

            GridDischargeFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#86EFAC")),

            _ =>
                new SolidColorBrush(Color.Parse("#E2E8F0"))
        };

    partial void OnDischargePowerKwChanged(double value)
    {
        if (value < 1)
        {
            DischargePowerKw = 1;
            return;
        }

        if (value > 40)
        {
            DischargePowerKw = 40;
            return;
        }

        OnPropertyChanged(nameof(DischargePowerText));
    }

    partial void OnMinSocChanged(double value)
    {
        if (value < 10)
        {
            MinSoc = 10;
            return;
        }

        if (value > 90)
        {
            MinSoc = 90;
            return;
        }

        OnPropertyChanged(nameof(MinSocText));
    }

    partial void OnCurrentSocChanged(double? value)
    {
        OnPropertyChanged(nameof(CurrentSocText));
        NotifyFlowVisualChanged();
    }

    partial void OnCurrentDischargePowerKwChanged(double? value)
    {
        OnPropertyChanged(nameof(CurrentDischargePowerText));
        NotifyFlowVisualChanged();
    }

    partial void OnSystemVoltageChanged(double? value)
    {
        OnPropertyChanged(nameof(SystemVoltageCurrentText));
    }

    partial void OnSystemCurrentChanged(double? value)
    {
        OnPropertyChanged(nameof(SystemVoltageCurrentText));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(StartButtonText));
    }

    partial void OnModeStatusChanged(string value)
    {
        NotifyFlowVisualChanged();
    }

    partial void OnDischargeStateChanged(
        GridDischargeFlowState value)
    {
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(StopButtonText));
        OnPropertyChanged(nameof(CanEditSettings));

        NotifyFlowVisualChanged();
    }

    private void NotifyFlowVisualChanged()
    {
        OnPropertyChanged(nameof(FlowBadgeText));
        OnPropertyChanged(nameof(FlowSubText));

        OnPropertyChanged(nameof(DcArrowOpacity));
        OnPropertyChanged(nameof(AcArrowOpacity));

        OnPropertyChanged(nameof(FlowBrush));
        OnPropertyChanged(nameof(BatteryBrush));

        OnPropertyChanged(nameof(FlowBackground));
        OnPropertyChanged(nameof(BatteryBackground));

        OnPropertyChanged(nameof(FlowBorderBrush));
        OnPropertyChanged(nameof(BatteryBorderBrush));
    }

    private async void RefreshTimer_Tick(
        object? sender,
        EventArgs e)
    {
        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        if (_disposed ||
            _isRefreshing ||
            _emsService is null)
        {
            return;
        }

        if (!_emsService.IsConnected)
        {
            IsRunning = false;
            DischargeState =
                GridDischargeFlowState.Disconnected;

            CurrentSoc = null;
            CurrentDischargePowerKw = null;
            SystemVoltage = null;
            SystemCurrent = null;
            ClearInverterAcRepresentativeValues();

            ModeStatus = "EMS 미연결";
            RequestStatus =
                "EMS 통신이 연결되어 있지 않습니다.";

            return;
        }

        _isRefreshing = true;

        try
        {
            var status =
                await _emsService.ReadStatusAsync();

            CurrentSoc = status.Soc;

            CurrentDischargePowerKw =
                Math.Abs(
                    status.Inverter1PowerKw +
                    status.Inverter2PowerKw);

            SystemVoltage = status.BatteryVoltage;
            SystemCurrent = status.BatteryCurrent;

            UpdateInverterAcRepresentativeValues(status);

            UpdateDischargeState(
                status.SystemStatus1,
                status.SystemStatus2,
                status.AlarmStatus1);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            DischargeState =
                GridDischargeFlowState.Fault;

            CurrentSoc = null;
            CurrentDischargePowerKw = null;
            SystemVoltage = null;
            SystemCurrent = null;
            ClearInverterAcRepresentativeValues();

            ModeStatus = "상태 읽기 실패";
            RequestStatus =
                $"EMS 상태 읽기 실패 · {ex.Message}";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void UpdateInverterAcRepresentativeValues(
        MobileEssControl.Models.System.EssStatusData status)
    {
        // 운전모드, SystemRun, WorkingMode, PowerOnOff 상태와 관계없이
        // EMS에서 읽은 인버터 1/2의 실제 AC 상태값을 항상 표시합니다.
        //
        // 두 인버터 모두 값이 있으면 평균값을 표시하고,
        // 한 대만 값이 있으면 해당 인버터 값을 표시합니다.
        // 두 값 모두 0이면 0을 표시합니다.

        double abVoltage =
            GetRepresentativeValue(
                status.Inverter1Voltage,
                status.Inverter2Voltage);

        double bcVoltage =
            GetRepresentativeValue(
                status.Inverter1BcVoltage,
                status.Inverter2BcVoltage);

        double caVoltage =
            GetRepresentativeValue(
                status.Inverter1CaVoltage,
                status.Inverter2CaVoltage);

        double phaseACurrent =
            GetRepresentativeValue(
                status.Inverter1PhaseACurrent,
                status.Inverter2PhaseACurrent);

        double phaseBCurrent =
            GetRepresentativeValue(
                status.Inverter1PhaseBCurrent,
                status.Inverter2PhaseBCurrent);

        double phaseCCurrent =
            GetRepresentativeValue(
                status.Inverter1PhaseCCurrent,
                status.Inverter2PhaseCCurrent);

        double frequency =
            GetRepresentativeValue(
                status.Inverter1Frequency,
                status.Inverter2Frequency);

        ThreePhaseVoltageText =
            $"{abVoltage:0.0} / " +
            $"{bcVoltage:0.0} / " +
            $"{caVoltage:0.0} V";

        ThreePhaseCurrentText =
            $"{phaseACurrent:0.00} / " +
            $"{phaseBCurrent:0.00} / " +
            $"{phaseCCurrent:0.00} A";

        GridFrequencyText =
            $"{frequency:0.00} Hz";
    }

    private static double GetRepresentativeValue(
        double inverter1Value,
        double inverter2Value)
    {
        const double minimumValidMagnitude = 0.001;

        bool inverter1HasValue =
            !double.IsNaN(inverter1Value) &&
            !double.IsInfinity(inverter1Value) &&
            Math.Abs(inverter1Value) >
                minimumValidMagnitude;

        bool inverter2HasValue =
            !double.IsNaN(inverter2Value) &&
            !double.IsInfinity(inverter2Value) &&
            Math.Abs(inverter2Value) >
                minimumValidMagnitude;

        if (inverter1HasValue &&
            inverter2HasValue)
        {
            return
                (inverter1Value + inverter2Value) / 2.0;
        }

        if (inverter1HasValue)
        {
            return inverter1Value;
        }

        if (inverter2HasValue)
        {
            return inverter2Value;
        }

        return 0.0;
    }

    private void ClearInverterAcRepresentativeValues()
    {
        ThreePhaseVoltageText = "-- / -- / -- V";
        ThreePhaseCurrentText = "-- / -- / -- A";
        GridFrequencyText = "-- Hz";
    }

    private static string FormatThreePhaseValues(
        double? firstValue,
        double? secondValue,
        double? thirdValue,
        string unit,
        int decimalPlaces)
    {
        if (!firstValue.HasValue ||
            !secondValue.HasValue ||
            !thirdValue.HasValue)
        {
            return $"-- / -- / -- {unit}";
        }

        string numberFormat =
            decimalPlaces == 1
                ? "0.0"
                : "0.00";

        return
            $"{firstValue.Value.ToString(numberFormat)} / " +
            $"{secondValue.Value.ToString(numberFormat)} / " +
            $"{thirdValue.Value.ToString(numberFormat)} {unit}";
    }

    private void UpdateDischargeState(
        ushort systemStatus1,
        ushort systemStatus2,
        ushort alarmStatus1)
    {
        ushort systemFaultLevel =
    EmsSystemStatus1.GetSystemFaultLevel(systemStatus1);

        EmsOperationMode operatingMode =
            EmsSystemStatus1.GetOperatingMode(systemStatus1);

        bool isSystemRunning =
            EmsSystemStatus2.IsRunning(systemStatus2);

        bool hasPackOrInverterFault =
            HasPackOrInverterFault(alarmStatus1);

        bool hasCanFault =
            HasCanFault(alarmStatus1);

        if (systemFaultLevel != 0 ||
            hasPackOrInverterFault ||
            hasCanFault)
        {
            IsRunning = false;

            DischargeState =
                GridDischargeFlowState.Fault;

            ModeStatus = "계통 방전 고장";

            RequestStatus =
                $"계통 방전 이상 감지 · SystemFault={systemFaultLevel} · Alarm1=0x{alarmStatus1:X4}";

            return;
        }


        if (operatingMode != EmsOperationMode.GridDischarge)
        {
            IsRunning = false;

            if (operatingMode == EmsOperationMode.Standby)
            {
                if (DischargeState != GridDischargeFlowState.Starting &&
                    DischargeState != GridDischargeFlowState.Stopping)
                {
                    DischargeState =
                        GridDischargeFlowState.Ready;
                }

                ModeStatus =
                    DischargeState == GridDischargeFlowState.Starting
                        ? "계통 방전 시작 확인 중"
                        : DischargeState == GridDischargeFlowState.Stopping
                            ? "계통 방전 정지 확인 중"
                            : "계통 방전 대기";

                RequestStatus =
                    DischargeState == GridDischargeFlowState.Starting
                        ? "EMS가 계통 방전 모드로 전환되는지 확인 중입니다."
                        : DischargeState == GridDischargeFlowState.Stopping
                            ? "EMS가 Standby / Stop 상태로 전환되는지 확인 중입니다."
                            : "시스템 Standby 상태입니다.";
            }
            else
            {
                DischargeState =
                    GridDischargeFlowState.Stopped;

                ModeStatus = "다른 운전 모드";

                RequestStatus =
                    $"현재 계통 방전 모드가 아닙니다. OperatingMode={operatingMode}";
            }

            return;
        }

        if (isSystemRunning)
        {
            IsRunning = true;

            DischargeState =
                GridDischargeFlowState.Discharging;

            ModeStatus = "계통 방전 중";

            RequestStatus =
                $"계통 방전 중 · 현재전력 {CurrentDischargePowerText} · SOC {CurrentSocText}";

            return;
        }

        IsRunning = false;

        if (CurrentSoc.HasValue &&
            CurrentSoc.Value <= MinSoc)
        {
            DischargeState =
                GridDischargeFlowState.Completed;

            ModeStatus = "계통 방전 완료";

            RequestStatus =
                $"최저 SOC 도달 · 현재 SOC {CurrentSoc.Value:0.0}% / 최저 SOC {MinSoc:0}%";
        }
        else
        {
            if (DischargeState != GridDischargeFlowState.Starting &&
                DischargeState != GridDischargeFlowState.Stopping)
            {
                DischargeState =
                    GridDischargeFlowState.Stopped;
            }

            ModeStatus =
                DischargeState == GridDischargeFlowState.Starting
                    ? "계통 방전 시작 확인 중"
                    : DischargeState == GridDischargeFlowState.Stopping
                        ? "계통 방전 정지 확인 중"
                        : "계통 방전 준비";

            RequestStatus =
                DischargeState == GridDischargeFlowState.Starting
                    ? "EMS의 실제 Run 상태를 확인 중입니다."
                    : DischargeState == GridDischargeFlowState.Stopping
                        ? "EMS의 실제 Stop 상태를 확인 중입니다."
                        : "계통 방전 모드 진입 상태 · 시작 대기 중";
        }
    }

    private static bool HasPackOrInverterFault(
        ushort alarmStatus1)
    {
        ushort pack1 =
            (ushort)((alarmStatus1 >> 0) & 0x0003);

        ushort pack2 =
            (ushort)((alarmStatus1 >> 2) & 0x0003);

        ushort inverter1 =
            (ushort)((alarmStatus1 >> 4) & 0x0003);

        ushort inverter2 =
            (ushort)((alarmStatus1 >> 6) & 0x0003);

        return pack1 != 0 ||
               pack2 != 0 ||
               inverter1 != 0 ||
               inverter2 != 0;
    }

    private static bool HasCanFault(
        ushort alarmStatus1)
    {
        return
            (alarmStatus1 & (1 << 12)) != 0 ||
            (alarmStatus1 & (1 << 13)) != 0 ||
            (alarmStatus1 & (1 << 14)) != 0 ||
            (alarmStatus1 & (1 << 15)) != 0;
    }

    [RelayCommand]
    private void DecreaseDischargePower()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (DischargePowerKw > 1)
        {
            DischargePowerKw -= 1;
        }
    }

    [RelayCommand]
    private void IncreaseDischargePower()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (DischargePowerKw < 40)
        {
            DischargePowerKw += 1;
        }
    }

    [RelayCommand]
    private void DecreaseMinSoc()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (MinSoc > 10)
        {
            MinSoc -= 5;
        }
    }

    [RelayCommand]
    private void IncreaseMinSoc()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (MinSoc < 90)
        {
            MinSoc += 5;
        }
    }

    [RelayCommand]
    private async Task StartDischarge()
    {
        if (_emsService is null)
        {
            DischargeState =
                GridDischargeFlowState.Fault;

            ModeStatus = "EMS 서비스 없음";
            RequestStatus =
                "EMS 서비스가 연결되지 않았습니다.";

            return;
        }

        if (_emsService.IsOperationSequenceRunning ||
            IsRunning ||
            DischargeState == GridDischargeFlowState.Starting)
        {
            RequestStatus =
                "이미 운전 중이거나 다른 운전 명령을 처리 중입니다.";

            return;
        }

        if (!_emsService.IsConnected)
        {
            IsRunning = false;

            DischargeState =
                GridDischargeFlowState.Disconnected;

            ModeStatus = "EMS 미연결";
            RequestStatus =
                "EMS 통신이 연결되어 있지 않습니다.";

            await AppDialogService.ShowWarningAsync(
                "계통 방전 시작 불가",
                "EMS 통신이 연결되어 있지 않습니다.");

            return;
        }

        // 화면에 표시된 이전 SOC가 아니라 시작 버튼을 누른 시점의
        // 최신 EMS SOC를 다시 읽어 최저 SOC 조건을 확인합니다.
        try
        {
            var latestStatus =
                await _emsService.ReadStatusAsync();

            CurrentSoc = latestStatus.Soc;
        }
        catch (Exception ex)
        {
            DischargeState =
                GridDischargeFlowState.Fault;

            ModeStatus = "SOC 확인 실패";
            RequestStatus =
                $"현재 SOC 확인 실패 · {ex.Message}";

            await AppDialogService.ShowWarningAsync(
                "계통 방전 시작 불가",
                "EMS의 현재 SOC를 확인하지 못했습니다.\n\n" +
                "통신 상태를 확인한 후 다시 시도하세요.");

            return;
        }

        if (!CurrentSoc.HasValue)
        {
            DischargeState =
                GridDischargeFlowState.Fault;

            ModeStatus = "SOC 확인 필요";
            RequestStatus =
                "현재 SOC를 확인할 수 없어 시작하지 않았습니다.";

            await AppDialogService.ShowWarningAsync(
                "계통 방전 시작 불가",
                "EMS의 현재 SOC를 확인한 후 다시 시도하세요.");

            return;
        }

        if (CurrentSoc.Value <= MinSoc)
        {
            DischargeState =
                GridDischargeFlowState.Completed;

            ModeStatus = "계통 방전 시작 불가";
            RequestStatus =
                $"현재 SOC {CurrentSoc.Value:0.0}%가 최저 SOC {MinSoc:0}% 이하입니다.";

            await AppDialogService.ShowWarningAsync(
                "계통 방전 시작 불가",
                $"현재 SOC : {CurrentSoc.Value:0.0}%\n" +
                $"최저 SOC : {MinSoc:0}%\n\n" +
                "현재 SOC가 설정한 최저 SOC 이하이므로\n" +
                "계통 방전을 시작하지 않습니다.");

            return;
        }

        bool confirmed =
            await AppDialogService.ShowConfirmAsync(
                "계통 방전 시작",
                $"현재 설정으로 계통 방전을 시작하시겠습니까?\n\n" +
                $"현재 SOC : {CurrentSoc.Value:0.0}%\n" +
                $"방전전력 : {DischargePowerKw:0} kW\n" +
                $"최저 SOC : {MinSoc:0}%",
                confirmText: "방전 시작",
                cancelText: "취소");

        if (!confirmed)
        {
            DischargeState =
                GridDischargeFlowState.Ready;

            ModeStatus = "계통 방전 대기";

            RequestStatus =
                "계통 방전 시작이 취소되었습니다.";

            return;
        }

        try
        {
            IsRunning = false;

            DischargeState =
                GridDischargeFlowState.Starting;

            ModeStatus = "계통 방전 시작 중";

            RequestStatus =
                $"계통 방전 명령 전송 중 · 방전전력 {DischargePowerKw:0}kW · 최저 SOC {MinSoc:0}%";

            await _emsService.StartGridDischargeAsync(
                DischargePowerKw,
                MinSoc);

            ModeStatus = "계통 방전 시작 확인 중";

            RequestStatus =
                "EMS 명령 전송 완료 · 실제 Run 상태를 확인 중입니다.";
        }
        catch (InvalidOperationException ex)
        {
            IsRunning = false;

            DischargeState =
                GridDischargeFlowState.Fault;

            ModeStatus = "계통 방전 시작 불가";
            RequestStatus = ex.Message;

            await AppDialogService.ShowWarningAsync(
                "계통 방전 시작 불가",
                $"{ex.Message}\n\n시작 명령을 전송하지 않았습니다.");
        }
        catch (Exception ex)
        {
            IsRunning = false;

            DischargeState =
                GridDischargeFlowState.Fault;

            ModeStatus = "계통 방전 시작 실패";

            RequestStatus =
                $"계통 방전 시작 실패 · {ex.Message}";

            await AppDialogService.ShowWarningAsync(
                "계통 방전 시작 실패",
                ex.Message);
        }
    }

    [RelayCommand]
    private async Task StopDischarge()
    {
        if (_emsService is null)
        {
            DischargeState =
                GridDischargeFlowState.Fault;

            ModeStatus = "EMS 서비스 없음";
            RequestStatus =
                "EMS 서비스가 연결되지 않았습니다.";

            return;
        }

        if (!_emsService.IsConnected)
        {
            IsRunning = false;

            DischargeState =
                GridDischargeFlowState.Disconnected;

            ModeStatus = "EMS 미연결";
            RequestStatus =
                "EMS 통신이 연결되어 있지 않습니다.";

            return;
        }

        try
        {
            DischargeState =
                GridDischargeFlowState.Stopping;

            ModeStatus = "계통 방전 정지 중";

            RequestStatus =
                "정지 명령 전송 및 실제 Standby 상태 확인 중";

            await _emsService.StopAllAsync();

            IsRunning = false;

            DischargeState =
                GridDischargeFlowState.Stopped;

            ModeStatus = "계통 방전 정지";

            RequestStatus =
                "EMS가 Standby / Stop 상태로 전환되었습니다.";
        }
        catch (Exception ex)
        {
            DischargeState =
                GridDischargeFlowState.Fault;

            ModeStatus = "계통 방전 정지 실패";

            RequestStatus =
                $"계통 방전 정지 실패 · {ex.Message}";

            await AppDialogService.ShowWarningAsync(
                "계통 방전 정지 실패",
                ex.Message);
        }
    }


    public void Dispose()
    {
        _disposed = true;

        if (_refreshTimer is not null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= RefreshTimer_Tick;
        }
    }
}