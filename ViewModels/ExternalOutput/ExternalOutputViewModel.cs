using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MobileEssControl.Constants;
using MobileEssControl.Models.System;
using MobileEssControl.Services.Dialogs;
using MobileEssControl.Services.Ems;
using System;
using System.Threading.Tasks;
using static MobileEssControl.Constants.EmsControlWord1;

namespace MobileEssControl.ViewModels.ExternalOutput;

public enum ExternalOutputFlowState
{
    Ready,
    Starting,
    Outputting,
    Stopping,
    Stopped,
    Completed,
    Fault,
    Disconnected
}

public partial class ExternalOutputViewModel : ViewModelBase, IDisposable
{
    private readonly EmsService? _emsService;
    private readonly DispatcherTimer? _refreshTimer;

    private bool _isRefreshing;
    private bool _disposed;

    // 외부출력 운전 중 전압/주파수 이상이 3회 연속 확인되면 자동 정지합니다.
    private const int ExternalOutputSafetyTripCount = 3;

    private int _externalOutputElectricalFaultCount;
    private bool _isExternalOutputSafetyStopInProgress;
    private bool _externalOutputSafetyTripLatched;
    private string _externalOutputSafetyTripMessage = string.Empty;

    public ExternalOutputViewModel()
    {
        // 디자인/미리보기 보호용
    }

    public ExternalOutputViewModel(EmsService emsService)
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
    private ExternalOutputFlowState outputState =
        ExternalOutputFlowState.Ready;

    [ObservableProperty]
    private double outputLimitKw = 40;

    [ObservableProperty]
    private double minSoc = 20;

    [ObservableProperty]
    private string modeStatus = "외부 출력 대기";

    [ObservableProperty]
    private string requestStatus =
        "시작 버튼을 누르면 외부 전원 출력 요청을 전송합니다.";

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private double? currentSoc;

    [ObservableProperty]
    private double? currentOutputPowerKw;

    [ObservableProperty]
    private double? systemVoltage;

    [ObservableProperty]
    private double? systemCurrent;

    public string OutputLimitText =>
        $"{OutputLimitKw:0} kW";

    public string MinSocText =>
        $"{MinSoc:0}%";

    public string CurrentSocText =>
        CurrentSoc.HasValue
            ? $"{CurrentSoc.Value:0.0}%"
            : "-- %";

    public string CurrentOutputPowerText =>
        CurrentOutputPowerKw.HasValue
            ? $"{CurrentOutputPowerKw.Value:0.00} kW"
            : "-- kW";

    public string SystemVoltageCurrentText =>
        SystemVoltage.HasValue &&
        SystemCurrent.HasValue
            ? $"{SystemVoltage.Value:0.0} V / {SystemCurrent.Value:0.0} A"
            : "-- V / -- A";

    public string StartButtonText =>
        OutputState switch
        {
            ExternalOutputFlowState.Starting => "시작 중",
            ExternalOutputFlowState.Outputting => "출력 중",
            _ => "외부 출력 시작"
        };

    public string StopButtonText =>
        OutputState == ExternalOutputFlowState.Stopping
            ? "정지 중"
            : "정지";

    /// <summary>
    /// 시작 중, 출력 중, 정지 중에는 SOC와 출력전력 설정을 변경하지 못하게 합니다.
    /// </summary>
    public bool CanEditSettings =>
        OutputState != ExternalOutputFlowState.Starting &&
        OutputState != ExternalOutputFlowState.Outputting &&
        OutputState != ExternalOutputFlowState.Stopping;

    public string FlowBadgeText =>
        OutputState switch
        {
            ExternalOutputFlowState.Starting => "STARTING",
            ExternalOutputFlowState.Outputting => "OUTPUTTING",
            ExternalOutputFlowState.Stopping => "STOPPING",
            ExternalOutputFlowState.Stopped => "STOPPED",
            ExternalOutputFlowState.Completed => "COMPLETED",
            ExternalOutputFlowState.Fault => "FAULT",
            ExternalOutputFlowState.Disconnected => "OFFLINE",
            _ => "READY"
        };

    public string FlowSubText =>
        OutputState switch
        {
            ExternalOutputFlowState.Starting =>
                "외부 출력 명령 전송 후 실제 운전 상태를 확인 중입니다.",

            ExternalOutputFlowState.Outputting =>
                $"현재 출력전력 {CurrentOutputPowerText} · SOC {CurrentSocText}",

            ExternalOutputFlowState.Stopping =>
                "정지 명령을 전송하고 Standby 상태를 확인 중입니다.",

            ExternalOutputFlowState.Stopped =>
                "외부 전원 출력이 정지되었습니다.",

            ExternalOutputFlowState.Completed =>
                $"최저 SOC에 도달했습니다. 현재 SOC {CurrentSocText}",

            ExternalOutputFlowState.Fault =>
                "외부 출력 시스템 이상이 감지되었습니다.",

            ExternalOutputFlowState.Disconnected =>
                "EMS 통신이 연결되어 있지 않습니다.",

            _ =>
                "외부 출력 시작 전 상태값을 확인합니다."
        };

    public double DcArrowOpacity =>
        OutputState == ExternalOutputFlowState.Starting ||
        OutputState == ExternalOutputFlowState.Outputting
            ? 1.0
            : OutputState == ExternalOutputFlowState.Stopping
                ? 0.55
                : 0.22;

    public double AcArrowOpacity =>
        OutputState == ExternalOutputFlowState.Outputting
            ? 1.0
            : OutputState == ExternalOutputFlowState.Starting ||
              OutputState == ExternalOutputFlowState.Stopping
                ? 0.55
                : 0.22;

    public IBrush FlowBrush =>
        OutputState switch
        {
            ExternalOutputFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#DC2626")),

            ExternalOutputFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#D97706")),

            ExternalOutputFlowState.Outputting =>
                new SolidColorBrush(Color.Parse("#2563EB")),

            ExternalOutputFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#EA580C")),

            ExternalOutputFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#16A34A")),

            _ =>
                new SolidColorBrush(Color.Parse("#94A3B8"))
        };

    public IBrush BatteryBrush =>
        OutputState switch
        {
            ExternalOutputFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#DC2626")),

            ExternalOutputFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#D97706")),

            ExternalOutputFlowState.Outputting =>
                new SolidColorBrush(Color.Parse("#16A34A")),

            ExternalOutputFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#EA580C")),

            ExternalOutputFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#16A34A")),

            _ =>
                new SolidColorBrush(Color.Parse("#94A3B8"))
        };

    public IBrush FlowBackground =>
        OutputState switch
        {
            ExternalOutputFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#FEF2F2")),

            ExternalOutputFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#FFF7ED")),

            ExternalOutputFlowState.Outputting =>
                new SolidColorBrush(Color.Parse("#EFF6FF")),

            ExternalOutputFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#FFF7ED")),

            ExternalOutputFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#ECFDF5")),

            _ =>
                new SolidColorBrush(Color.Parse("#F8FAFC"))
        };

    public IBrush BatteryBackground =>
        OutputState switch
        {
            ExternalOutputFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#FEF2F2")),

            ExternalOutputFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#FFF7ED")),

            ExternalOutputFlowState.Outputting =>
                new SolidColorBrush(Color.Parse("#ECFDF5")),

            ExternalOutputFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#FFF7ED")),

            ExternalOutputFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#ECFDF5")),

            _ =>
                new SolidColorBrush(Color.Parse("#F8FAFC"))
        };

    public IBrush FlowBorderBrush =>
        OutputState switch
        {
            ExternalOutputFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#FCA5A5")),

            ExternalOutputFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#FDBA74")),

            ExternalOutputFlowState.Outputting =>
                new SolidColorBrush(Color.Parse("#93C5FD")),

            ExternalOutputFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#FDBA74")),

            ExternalOutputFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#86EFAC")),

            _ =>
                new SolidColorBrush(Color.Parse("#E2E8F0"))
        };

    public IBrush BatteryBorderBrush =>
        OutputState switch
        {
            ExternalOutputFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#FCA5A5")),

            ExternalOutputFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#FDBA74")),

            ExternalOutputFlowState.Outputting =>
                new SolidColorBrush(Color.Parse("#86EFAC")),

            ExternalOutputFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#FDBA74")),

            ExternalOutputFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#86EFAC")),

            _ =>
                new SolidColorBrush(Color.Parse("#E2E8F0"))
        };

    partial void OnOutputLimitKwChanged(double value)
    {
        if (value < 1)
        {
            OutputLimitKw = 1;
            return;
        }

        if (value > 40)
        {
            OutputLimitKw = 40;
            return;
        }

        OnPropertyChanged(nameof(OutputLimitText));
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

    partial void OnCurrentOutputPowerKwChanged(double? value)
    {
        OnPropertyChanged(nameof(CurrentOutputPowerText));
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

    partial void OnOutputStateChanged(
        ExternalOutputFlowState value)
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
            OutputState =
                ExternalOutputFlowState.Disconnected;

            CurrentSoc = null;
            CurrentOutputPowerKw = null;
            SystemVoltage = null;
            SystemCurrent = null;

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

            CurrentOutputPowerKw =
                Math.Abs(
                    status.Inverter1PowerKw +
                    status.Inverter2PowerKw);

            SystemVoltage = status.BatteryVoltage;
            SystemCurrent = status.BatteryCurrent;

            UpdateOutputState(
                status.SystemStatus1,
                status.SystemStatus2,
                status.AlarmStatus1);

            await MonitorExternalOutputElectricalStateAsync(
                status);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            OutputState =
                ExternalOutputFlowState.Fault;

            CurrentSoc = null;
            CurrentOutputPowerKw = null;
            SystemVoltage = null;
            SystemCurrent = null;

            ModeStatus = "상태 읽기 실패";
            RequestStatus =
                $"EMS 상태 읽기 실패 · {ex.Message}";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>
    /// 외부출력 운전 중 Mode/Run 상태에서 인버터 AB 선간전압과 주파수를 확인합니다.
    /// 전기 상태 이상이 3회 연속 확인되면 자동으로 Standby/Stop을 요청합니다.
    /// </summary>
    private async Task MonitorExternalOutputElectricalStateAsync(
        EssStatusData status)
    {
        if (_emsService is null ||
            _isExternalOutputSafetyStopInProgress ||
            _externalOutputSafetyTripLatched)
        {
            return;
        }

        EmsOperationMode operatingMode =
            EmsSystemStatus1.GetOperatingMode(
                status.SystemStatus1);

        bool isSystemRunning =
            EmsSystemStatus2.IsRunning(
                status.SystemStatus2);

        if (operatingMode !=
                EmsOperationMode.ExternalOutput ||
            !isSystemRunning)
        {
            _externalOutputElectricalFaultCount = 0;
            return;
        }

        ushort systemFaultLevel =
            EmsSystemStatus1.GetSystemFaultLevel(
                status.SystemStatus1);

        bool hasPackOrInverterFault =
            EmsSystemAlarms1.HasPackOrInverterFault(
                status.AlarmStatus1);

        bool hasCanFault =
            EmsSystemAlarms1.HasCanFault(
                status.AlarmStatus1);

        bool systemFaultDetected =
            systemFaultLevel != 0 ||
            hasPackOrInverterFault ||
            hasCanFault;

        bool electricalNormal =
            _emsService.IsExternalOutputElectricalStateNormal(
                status,
                out string electricalDetail);

        if (systemFaultDetected)
        {
            electricalDetail =
                $"외부출력 Fault 감지 · " +
                $"SystemFault={systemFaultLevel} · " +
                $"Alarm1=0x{status.AlarmStatus1:X4}";

            // Fault는 전압 이상 3회 대기 없이 즉시 안전 정지를 요청합니다.
            _externalOutputElectricalFaultCount =
                ExternalOutputSafetyTripCount;
        }
        else if (electricalNormal)
        {
            _externalOutputElectricalFaultCount = 0;

            RequestStatus =
                $"외부 출력 정상 · " +
                $"INV1 {status.Inverter1Voltage:0.0}V/" +
                $"{status.Inverter1Frequency:0.00}Hz · " +
                $"INV2 {status.Inverter2Voltage:0.0}V/" +
                $"{status.Inverter2Frequency:0.00}Hz · " +
                $"SOC {CurrentSocText}";

            return;
        }
        else
        {
            _externalOutputElectricalFaultCount++;
        }

        RequestStatus =
            $"외부 출력 전기 상태 이상 " +
            $"{_externalOutputElectricalFaultCount}/" +
            $"{ExternalOutputSafetyTripCount} · " +
            $"{electricalDetail}";

        if (_externalOutputElectricalFaultCount <
            ExternalOutputSafetyTripCount)
        {
            return;
        }

        _isExternalOutputSafetyStopInProgress = true;
        _externalOutputSafetyTripLatched = true;

        _externalOutputSafetyTripMessage =
            "외부출력 운전 중 전압 또는 주파수 이상이 " +
            $"{ExternalOutputSafetyTripCount}회 연속 감지되어 " +
            "자동 정지했습니다. " +
            $"{electricalDetail}";

        try
        {
            IsRunning = false;

            OutputState =
                ExternalOutputFlowState.Stopping;

            ModeStatus =
                "외부 출력 안전 정지 중";

            RequestStatus =
                _externalOutputSafetyTripMessage;

            await _emsService.StopAllAsync();

            OutputState =
                ExternalOutputFlowState.Fault;

            ModeStatus =
                "외부 출력 안전 정지";

            RequestStatus =
                _externalOutputSafetyTripMessage;

            await AppDialogService.ShowWarningAsync(
                "외부 출력 자동 정지",
                _externalOutputSafetyTripMessage);
        }
        catch (Exception ex)
        {
            OutputState =
                ExternalOutputFlowState.Fault;

            ModeStatus =
                "외부 출력 안전 정지 실패";

            RequestStatus =
                $"{_externalOutputSafetyTripMessage}\n" +
                $"정지 명령 실패 · {ex.Message}";

            await AppDialogService.ShowWarningAsync(
                "외부 출력 안전 정지 실패",
                RequestStatus);
        }
        finally
        {
            _externalOutputElectricalFaultCount = 0;
            _isExternalOutputSafetyStopInProgress = false;
        }
    }

    private void UpdateOutputState(
        ushort systemStatus1,
        ushort systemStatus2,
        ushort alarmStatus1)
    {
        ExternalOutputFlowState previousState =
            OutputState;

        if (_externalOutputSafetyTripLatched)
        {
            IsRunning = false;

            OutputState =
                ExternalOutputFlowState.Fault;

            ModeStatus =
                "외부 출력 안전 정지";

            RequestStatus =
                _externalOutputSafetyTripMessage;

            return;
        }

        ushort systemFaultLevel =
            EmsSystemStatus1.GetSystemFaultLevel(systemStatus1);

        EmsOperationMode operatingMode =
            EmsSystemStatus1.GetOperatingMode(systemStatus1);

        bool isSystemRunning =
            EmsSystemStatus2.IsRunning(systemStatus2);

        bool hasPackOrInverterFault =
            EmsSystemAlarms1.HasPackOrInverterFault(alarmStatus1);

        bool hasCanFault =
            EmsSystemAlarms1.HasCanFault(alarmStatus1);

        if (systemFaultLevel != 0 ||
            hasPackOrInverterFault ||
            hasCanFault)
        {
            IsRunning = false;

            OutputState =
                ExternalOutputFlowState.Fault;

            ModeStatus = "외부 출력 고장";

            RequestStatus =
                $"외부 출력 이상 감지 · SystemFault={systemFaultLevel} · Alarm1=0x{alarmStatus1:X4}";

            return;
        }

        if (operatingMode == EmsOperationMode.ExternalOutput)
        {
            if (isSystemRunning)
            {
                IsRunning = true;

                OutputState =
                    ExternalOutputFlowState.Outputting;

                ModeStatus = "외부 출력 중";

                RequestStatus =
                    $"외부 출력 중 · 현재전력 {CurrentOutputPowerText} · SOC {CurrentSocText}";

                return;
            }

            IsRunning = false;

            if (CurrentSoc.HasValue &&
                CurrentSoc.Value <= MinSoc)
            {
                OutputState =
                    ExternalOutputFlowState.Completed;

                ModeStatus = "외부 출력 완료";

                RequestStatus =
                    $"최저 SOC 도달 · 현재 SOC {CurrentSoc.Value:0.0}% / 최저 SOC {MinSoc:0}%";

                return;
            }

            if (previousState != ExternalOutputFlowState.Starting &&
                previousState != ExternalOutputFlowState.Stopping)
            {
                OutputState =
                    ExternalOutputFlowState.Stopped;
            }

            ModeStatus =
                previousState == ExternalOutputFlowState.Starting
                    ? "외부 출력 시작 확인 중"
                    : previousState == ExternalOutputFlowState.Stopping
                        ? "외부 출력 정지 확인 중"
                        : "외부 출력 준비";

            RequestStatus =
                previousState == ExternalOutputFlowState.Starting
                    ? "EMS의 실제 Run 상태를 확인 중입니다."
                    : previousState == ExternalOutputFlowState.Stopping
                        ? "EMS의 실제 Stop 상태를 확인 중입니다."
                        : "외부 출력 모드 진입 상태 · 시작 대기 중";

            return;
        }

        IsRunning = false;

        if (operatingMode == EmsOperationMode.Standby)
        {
            if (CurrentSoc.HasValue &&
                CurrentSoc.Value <= MinSoc &&
                (previousState == ExternalOutputFlowState.Outputting ||
                 previousState == ExternalOutputFlowState.Completed))
            {
                OutputState =
                    ExternalOutputFlowState.Completed;

                ModeStatus = "외부 출력 완료";

                RequestStatus =
                    $"최저 SOC 도달 · 현재 SOC {CurrentSoc.Value:0.0}% / 최저 SOC {MinSoc:0}%";

                return;
            }

            if (previousState == ExternalOutputFlowState.Starting)
            {
                OutputState =
                    ExternalOutputFlowState.Starting;

                ModeStatus = "외부 출력 시작 확인 중";
                RequestStatus =
                    "EMS가 외부 출력 모드로 전환되는지 확인 중입니다.";

                return;
            }

            if (previousState == ExternalOutputFlowState.Stopping)
            {
                OutputState =
                    ExternalOutputFlowState.Stopping;

                ModeStatus = "외부 출력 정지 확인 중";
                RequestStatus =
                    "EMS가 Standby / Stop 상태로 전환되는지 확인 중입니다.";

                return;
            }

            if (previousState == ExternalOutputFlowState.Stopped)
            {
                OutputState =
                    ExternalOutputFlowState.Stopped;

                ModeStatus = "외부 출력 정지";
                RequestStatus =
                    "EMS가 Standby / Stop 상태입니다.";

                return;
            }

            OutputState =
                ExternalOutputFlowState.Ready;

            ModeStatus = "외부 출력 대기";
            RequestStatus =
                "시스템 Standby 상태입니다.";

            return;
        }

        OutputState =
            ExternalOutputFlowState.Stopped;

        ModeStatus = "다른 운전 모드";

        RequestStatus =
            $"현재 외부 출력 모드가 아닙니다. OperatingMode={operatingMode}";
    }

    [RelayCommand]
    private void DecreaseOutputLimit()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (OutputLimitKw > 1)
        {
            OutputLimitKw -= 1;
        }
    }

    [RelayCommand]
    private void IncreaseOutputLimit()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (OutputLimitKw < 40)
        {
            OutputLimitKw += 1;
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
    private async Task StartOutput()
    {
        if (_emsService is null)
        {
            OutputState =
                ExternalOutputFlowState.Fault;

            ModeStatus = "EMS 서비스 없음";
            RequestStatus =
                "EMS 서비스가 연결되지 않았습니다.";

            return;
        }

        if (OutputState == ExternalOutputFlowState.Starting ||
            OutputState == ExternalOutputFlowState.Outputting ||
            OutputState == ExternalOutputFlowState.Stopping ||
            _emsService.IsOperationSequenceRunning)
        {
            return;
        }

        if (!_emsService.IsConnected)
        {
            IsRunning = false;

            OutputState =
                ExternalOutputFlowState.Disconnected;

            ModeStatus = "EMS 미연결";
            RequestStatus =
                "EMS 통신이 연결되어 있지 않습니다.";

            await AppDialogService.ShowWarningAsync(
                "외부 출력 시작 불가",
                "EMS 통신이 연결되어 있지 않습니다.");

            return;
        }

        // 시작 확인 팝업보다 먼저 최신 EMS 상태를 읽습니다.
        // 비상정지는 SOC, 선간전압 등 다른 시작 조건보다 가장 먼저 안내합니다.
        EssStatusData latestStatus;

        try
        {
            latestStatus =
                await _emsService.ReadStatusAsync();

            // 화면 표시값도 시작 버튼을 누른 시점의 최신값으로 갱신합니다.
            CurrentSoc =
                latestStatus.Soc;

            CurrentOutputPowerKw =
                Math.Abs(
                    latestStatus.Inverter1PowerKw +
                    latestStatus.Inverter2PowerKw);

            SystemVoltage =
                latestStatus.BatteryVoltage;

            SystemCurrent =
                latestStatus.BatteryCurrent;
        }
        catch (Exception ex)
        {
            IsRunning = false;

            OutputState =
                ExternalOutputFlowState.Fault;

            ModeStatus =
                "외부 출력 상태 확인 실패";

            RequestStatus =
                $"외부 출력 시작 전 EMS 상태 확인 실패 · {ex.Message}";

            await AppDialogService.ShowWarningAsync(
                "외부 출력 시작 불가",
                "EMS 상태를 확인하지 못했습니다.\n\n" +
                "통신 상태를 확인한 후 다시 시도하세요.");

            return;
        }

        // 31023 System Status2 Bit10 : EmStopSwitchSts
        bool isEmergencyStopOn =
            (latestStatus.SystemStatus2 & (1 << 10)) != 0;

        if (isEmergencyStopOn)
        {
            IsRunning = false;

            OutputState =
                ExternalOutputFlowState.Fault;

            ModeStatus =
                "비상정지 상태";

            RequestStatus =
                "비상정지 스위치가 ON 상태입니다.";

            await AppDialogService.ShowWarningAsync(
                "비상정지 상태",
                "비상정지 스위치가 ON 상태입니다.\n\n" +
                "비상정지를 해제한 후 다시 시작하세요.");

            return;
        }

        if (CurrentSoc.HasValue &&
            CurrentSoc.Value <= MinSoc)
        {
            OutputState =
                ExternalOutputFlowState.Completed;

            ModeStatus = "외부 출력 시작 불가";
            RequestStatus =
                $"현재 SOC가 최저 SOC 이하입니다. 현재 {CurrentSoc.Value:0.0}% / 최저 {MinSoc:0}%";

            await AppDialogService.ShowWarningAsync(
                "외부 출력 시작 불가",
                $"현재 SOC가 설정한 최저 SOC 이하입니다.\n\n" +
                $"현재 SOC : {CurrentSoc.Value:0.0}%\n" +
                $"최저 SOC : {MinSoc:0}%");

            return;
        }

        bool confirmed =
            await AppDialogService.ShowConfirmAsync(
                "외부 전원 출력 시작",
                $"현재 설정으로 외부 전원 출력을 시작하시겠습니까?\n\n" +
                $"출력 제한 : {OutputLimitKw:0} kW\n" +
                $"최저 SOC : {MinSoc:0}%\n\n" +
                "Off-Grid AC 출력이 활성화됩니다.",
                confirmText: "외부 출력 시작",
                cancelText: "취소");

        if (!confirmed)
        {
            OutputState =
                ExternalOutputFlowState.Ready;

            ModeStatus = "외부 출력 대기";
            RequestStatus =
                "외부 출력 시작이 취소되었습니다.";

            return;
        }

        _externalOutputElectricalFaultCount = 0;
        _externalOutputSafetyTripLatched = false;
        _externalOutputSafetyTripMessage = string.Empty;

        try
        {
            IsRunning = false;

            OutputState =
                ExternalOutputFlowState.Starting;

            ModeStatus = "외부 출력 시작 중";

            RequestStatus =
                $"외부 출력 명령 전송 중 · 출력 제한 {OutputLimitKw:0}kW · 최저 SOC {MinSoc:0}%";

            await _emsService.StartExternalOutputAsync(
                OutputLimitKw,
                MinSoc);

            IsRunning = true;

            OutputState =
                ExternalOutputFlowState.Outputting;

            ModeStatus = "외부 출력 중";

            RequestStatus =
                "EMS가 ExternalOutput / Run 상태로 전환되었습니다.";

            await RefreshStatusAsync();
        }
        catch (InvalidOperationException ex)
        {
            IsRunning = false;

            OutputState =
                ExternalOutputFlowState.Fault;

            ModeStatus = "외부 출력 시작 불가";
            RequestStatus = ex.Message;

            await AppDialogService.ShowWarningAsync(
                "외부 출력 시작 불가",
                $"{ex.Message}\n\n시작 명령을 전송하지 않았습니다.");
        }
        catch (Exception ex)
        {
            IsRunning = false;

            OutputState =
                ExternalOutputFlowState.Fault;

            ModeStatus = "외부 출력 시작 실패";

            RequestStatus =
                $"외부 출력 시작 실패 · {ex.Message}";

            await AppDialogService.ShowWarningAsync(
                "외부 출력 시작 실패",
                ex.Message);
        }
    }

    [RelayCommand]
    private async Task StopOutput()
    {
        if (_emsService is null)
        {
            OutputState =
                ExternalOutputFlowState.Fault;

            ModeStatus = "EMS 서비스 없음";
            RequestStatus =
                "EMS 서비스가 연결되지 않았습니다.";

            return;
        }

        if (OutputState == ExternalOutputFlowState.Stopping ||
            _emsService.IsOperationSequenceRunning)
        {
            return;
        }

        if (!_emsService.IsConnected)
        {
            IsRunning = false;

            OutputState =
                ExternalOutputFlowState.Disconnected;

            ModeStatus = "EMS 미연결";
            RequestStatus =
                "EMS 통신이 연결되어 있지 않습니다.";

            return;
        }

        if (OutputState != ExternalOutputFlowState.Outputting &&
            OutputState != ExternalOutputFlowState.Starting)
        {
            RequestStatus =
                "현재 외부 출력 운전 중이 아닙니다.";

            return;
        }

        try
        {
            OutputState =
                ExternalOutputFlowState.Stopping;

            ModeStatus = "외부 출력 정지 중";

            RequestStatus =
                "정지 명령 전송 및 실제 Standby 상태 확인 중";

            await _emsService.StopAllAsync();

            IsRunning = false;

            OutputState =
                ExternalOutputFlowState.Stopped;

            ModeStatus = "외부 출력 정지";

            RequestStatus =
                "EMS가 Standby / Stop 상태로 전환되었습니다.";
        }
        catch (Exception ex)
        {
            OutputState =
                ExternalOutputFlowState.Fault;

            ModeStatus = "외부 출력 정지 실패";

            RequestStatus =
                $"외부 출력 정지 실패 · {ex.Message}";

            await AppDialogService.ShowWarningAsync(
                "외부 출력 정지 실패",
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
