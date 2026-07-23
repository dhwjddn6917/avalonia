using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MobileEssControl.Services.Dialogs;
using MobileEssControl.Services.Ems;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MobileEssControl.ViewModels.AutoCharge;

public enum ChargeFlowState
{
    Ready,
    Starting,
    Charging,
    Stopping,
    Stopped,
    Completed,
    Fault,
    Disconnected
}

public partial class AutoChargeViewModel : ViewModelBase, IDisposable
{
    private const double BatteryCapacityKwh = 77.4;
    private const int GraphSampleIntervalSeconds = 5;
    private const int GraphMaxSamples = 180;
    private const double GraphWidth = 280;
    private const double GraphHeight = 70;

    private readonly EmsService? _emsService;
    private readonly DispatcherTimer? _refreshTimer;
    private readonly List<double> _outputHistoryKwh = new();

    private bool _isRefreshing;
    private bool _disposed;

    private DateTime? _chargeStartedAtUtc;
    private DateTime? _lastEnergySampleUtc;
    private DateTime? _lastGraphSampleUtc;
    private double _cumulativeChargeEnergyKwh;

    public Points ChargeOutputGraphPoints { get; } = new();

    public AutoChargeViewModel()
    {
        // 디자인/미리보기 보호용
    }

    public AutoChargeViewModel(EmsService emsService)
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
    private ChargeFlowState chargeState = ChargeFlowState.Ready;

    [ObservableProperty]
    private double targetSoc = 90;

    private const double MinChargeCurrentA = 0.0;
    private const double MaxChargeCurrentLimitA = 120.0;
    private const double ChargeCurrentStepA = 1.0;

    [ObservableProperty]
    private double chargeCurrentA = 5;

    [ObservableProperty]
    private string modeStatus = "충전 대기";

    [ObservableProperty]
    private string requestStatus =
        "시작 버튼을 누르면 EMS에 충전 요청을 전송합니다.";

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private double? currentSoc;

    [ObservableProperty]
    private double? currentChargePowerKw;

    [ObservableProperty]
    private double? systemVoltage;

    [ObservableProperty]
    private double? systemCurrent;

    [ObservableProperty]
    private string threePhaseVoltageText = "-- / -- / -- V";

    [ObservableProperty]
    private double? inverter1PowerKw;

    [ObservableProperty]
    private double? inverter1AcVoltage;

    [ObservableProperty]
    private double? inverter1DcVoltage;

    [ObservableProperty]
    private double? inverter1DcCurrent;

    [ObservableProperty]
    private double? inverter1Frequency;

    [ObservableProperty]
    private double? inverter2PowerKw;

    [ObservableProperty]
    private double? inverter2AcVoltage;

    [ObservableProperty]
    private double? inverter2DcVoltage;

    [ObservableProperty]
    private double? inverter2DcCurrent;

    [ObservableProperty]
    private double? inverter2Frequency;

    [ObservableProperty]
    private string operatingModeText = "대기";

    [ObservableProperty]
    private string runStopText = "정지";

    [ObservableProperty]
    private string faultLevelText = "정상";

    [ObservableProperty]
    private string communicationStatusText = "확인 대기";

    [ObservableProperty]
    private string alarmSummaryText =
        "세부 알람은 알람 화면에서 확인";

    [ObservableProperty]
    private string chargeElapsedText = "--:--:--";

    [ObservableProperty]
    private string estimatedRemainingText = "-- ";

    private bool IsChargeFlowActive =>
        ChargeState == ChargeFlowState.Starting ||
        ChargeState == ChargeFlowState.Charging;

    private bool IsChargeFlowFault =>
        ChargeState == ChargeFlowState.Fault;

    public string ChargeFlowBadgeText =>
        ChargeState switch
        {
            ChargeFlowState.Starting => "STARTING",
            ChargeFlowState.Charging => "CHARGING",
            ChargeFlowState.Stopping => "STOPPING",
            ChargeFlowState.Stopped => "STOPPED",
            ChargeFlowState.Completed => "COMPLETED",
            ChargeFlowState.Fault => "FAULT",
            ChargeFlowState.Disconnected => "OFFLINE",
            _ => "READY"
        };

    public string ChargeFlowStateText =>
        ModeStatus;

    public string ChargeFlowSubText =>
        ChargeState switch
        {
            ChargeFlowState.Starting =>
                "충전 시작 명령을 전송하고 EMS 상태를 확인 중입니다.",

            ChargeFlowState.Charging =>
                $"전력 {CurrentChargePowerText} · SOC {CurrentSocText}",

            ChargeFlowState.Stopping =>
                "충전 정지 명령을 전송하고 실제 정지 상태를 확인 중입니다.",

            ChargeFlowState.Stopped =>
                "충전이 정지되었습니다.",

            ChargeFlowState.Completed =>
                $"목표 SOC에 도달했습니다. 현재 SOC {CurrentSocText}",

            ChargeFlowState.Fault =>
                "충전 시스템 이상이 감지되었습니다.",

            ChargeFlowState.Disconnected =>
                "EMS 통신이 연결되어 있지 않습니다.",

            _ =>
                "충전 시작 전 상태값을 확인합니다."
        };

    public double ChargeFlowAcArrowOpacity =>
        ChargeState == ChargeFlowState.Starting ||
        ChargeState == ChargeFlowState.Charging
            ? 1.0
            : ChargeState == ChargeFlowState.Stopping
                ? 0.55
                : 0.22;

    public double ChargeFlowDcArrowOpacity =>
        ChargeState == ChargeFlowState.Charging
            ? 1.0
            : ChargeState == ChargeFlowState.Starting ||
              ChargeState == ChargeFlowState.Stopping
                ? 0.55
                : 0.22;

    public IBrush ChargeFlowGridBrush =>
        ChargeState switch
        {
            ChargeFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#DC2626")),

            ChargeFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#D97706")),

            ChargeFlowState.Charging =>
                new SolidColorBrush(Color.Parse("#2563EB")),

            ChargeFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#EA580C")),

            ChargeFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#16A34A")),

            _ =>
                new SolidColorBrush(Color.Parse("#94A3B8"))
        };

    public IBrush ChargeFlowInverterBrush =>
        ChargeFlowGridBrush;

    public IBrush ChargeFlowBatteryBrush =>
        ChargeState switch
        {
            ChargeFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#DC2626")),

            ChargeFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#D97706")),

            ChargeFlowState.Charging =>
                new SolidColorBrush(Color.Parse("#16A34A")),

            ChargeFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#EA580C")),

            ChargeFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#16A34A")),

            _ =>
                new SolidColorBrush(Color.Parse("#94A3B8"))
        };

    public IBrush ChargeFlowGridBackground =>
        ChargeState switch
        {
            ChargeFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#FEF2F2")),

            ChargeFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#FFF7ED")),

            ChargeFlowState.Charging =>
                new SolidColorBrush(Color.Parse("#EFF6FF")),

            ChargeFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#FFF7ED")),

            ChargeFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#ECFDF5")),

            _ =>
                new SolidColorBrush(Color.Parse("#F8FAFC"))
        };

    public IBrush ChargeFlowInverterBackground =>
        ChargeFlowGridBackground;

    public IBrush ChargeFlowBatteryBackground =>
        ChargeState switch
        {
            ChargeFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#FEF2F2")),

            ChargeFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#FFF7ED")),

            ChargeFlowState.Charging =>
                new SolidColorBrush(Color.Parse("#ECFDF5")),

            ChargeFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#FFF7ED")),

            ChargeFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#ECFDF5")),

            _ =>
                new SolidColorBrush(Color.Parse("#F8FAFC"))
        };

    public IBrush ChargeFlowGridBorderBrush =>
        ChargeState switch
        {
            ChargeFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#FCA5A5")),

            ChargeFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#FDBA74")),

            ChargeFlowState.Charging =>
                new SolidColorBrush(Color.Parse("#93C5FD")),

            ChargeFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#FDBA74")),

            ChargeFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#86EFAC")),

            _ =>
                new SolidColorBrush(Color.Parse("#E2E8F0"))
        };

    public IBrush ChargeFlowInverterBorderBrush =>
        ChargeFlowGridBorderBrush;

    public IBrush ChargeFlowBatteryBorderBrush =>
        ChargeState switch
        {
            ChargeFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#FCA5A5")),

            ChargeFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#FDBA74")),

            ChargeFlowState.Charging =>
                new SolidColorBrush(Color.Parse("#86EFAC")),

            ChargeFlowState.Stopping =>
                new SolidColorBrush(Color.Parse("#FDBA74")),

            ChargeFlowState.Completed =>
                new SolidColorBrush(Color.Parse("#86EFAC")),

            _ =>
                new SolidColorBrush(Color.Parse("#E2E8F0"))
        };

    public string SystemVoltageCurrentText =>
        SystemVoltage.HasValue && SystemCurrent.HasValue
            ? $"{SystemVoltage.Value:0.0} V / {SystemCurrent.Value:0.0} A"
            : "-- V / -- A";

    public string CurrentChargePowerText =>
        CurrentChargePowerKw.HasValue
            ? $"{CurrentChargePowerKw.Value:0.00} kW"
            : "-- kW";

    public string TargetSocText =>
        $"{TargetSoc:0}%";

    public string ChargeCurrentText =>
        $"{ChargeCurrentA:0} A";

    public string StartButtonText =>
        ChargeState switch
        {
            ChargeFlowState.Starting => "시작 중",
            ChargeFlowState.Charging => "충전 중",
            _ => "시작"
        };

    public string StopButtonText =>
        ChargeState == ChargeFlowState.Stopping
            ? "정지 중"
            : "정지";

    /// <summary>
    /// 시작 중, 충전 중, 정지 중에는 SOC와 충전 전류 설정을 변경하지 못하게 합니다.
    /// </summary>
    public bool CanEditSettings =>
        ChargeState != ChargeFlowState.Starting &&
        ChargeState != ChargeFlowState.Charging &&
        ChargeState != ChargeFlowState.Stopping;

    public bool IsChargingActive =>
        ChargeState == ChargeFlowState.Charging;

    public bool IsOperatingModeBlinking =>
        OperatingModeText != "Standby" &&
        OperatingModeText != "미연결" &&
        OperatingModeText != "읽기 실패";

    public bool IsChargeTransitionActive =>
        ChargeState == ChargeFlowState.Starting ||
        ChargeState == ChargeFlowState.Stopping;

    public bool IsFaultBlinking =>
        FaultLevelText != "정상";

    public bool IsCommunicationBlinking =>
        CommunicationStatusText != "정상";

    public string CurrentSocText =>
        CurrentSoc.HasValue
            ? $"{CurrentSoc.Value:0.0}%"
            : "-- %";

    public string Inverter1PowerText =>
        Inverter1PowerKw.HasValue
            ? $"{Inverter1PowerKw.Value:0.00} kW"
            : "-- kW";

    public string Inverter1AcDcText =>
        Inverter1AcVoltage.HasValue &&
        Inverter1DcVoltage.HasValue &&
        Inverter1DcCurrent.HasValue
            ? $"AC {Inverter1AcVoltage.Value:0.0} V  ·  DC {Inverter1DcVoltage.Value:0.0} V / {Inverter1DcCurrent.Value:0.0} A"
            : "AC -- V  ·  DC -- V / -- A";

    public string Inverter1FrequencyText =>
        Inverter1Frequency.HasValue
            ? $"{Inverter1Frequency.Value:0.00} Hz"
            : "-- Hz";

    public string Inverter2PowerText =>
        Inverter2PowerKw.HasValue
            ? $"{Inverter2PowerKw.Value:0.00} kW"
            : "-- kW";

    public string Inverter2AcDcText =>
        Inverter2AcVoltage.HasValue &&
        Inverter2DcVoltage.HasValue &&
        Inverter2DcCurrent.HasValue
            ? $"AC {Inverter2AcVoltage.Value:0.0} V  ·  DC {Inverter2DcVoltage.Value:0.0} V / {Inverter2DcCurrent.Value:0.0} A"
            : "AC -- V  ·  DC -- V / -- A";

    public string Inverter2FrequencyText =>
        Inverter2Frequency.HasValue
            ? $"{Inverter2Frequency.Value:0.00} Hz"
            : "-- Hz";

    partial void OnSystemVoltageChanged(double? value)
    {
        OnPropertyChanged(nameof(SystemVoltageCurrentText));
    }

    partial void OnSystemCurrentChanged(double? value)
    {
        OnPropertyChanged(nameof(SystemVoltageCurrentText));
    }

    partial void OnInverter1PowerKwChanged(double? value)
    {
        OnPropertyChanged(nameof(Inverter1PowerText));
    }

    partial void OnInverter1AcVoltageChanged(double? value)
    {
        OnPropertyChanged(nameof(Inverter1AcDcText));
    }

    partial void OnInverter1DcVoltageChanged(double? value)
    {
        OnPropertyChanged(nameof(Inverter1AcDcText));
    }

    partial void OnInverter1DcCurrentChanged(double? value)
    {
        OnPropertyChanged(nameof(Inverter1AcDcText));
    }

    partial void OnInverter1FrequencyChanged(double? value)
    {
        OnPropertyChanged(nameof(Inverter1FrequencyText));
    }

    partial void OnInverter2PowerKwChanged(double? value)
    {
        OnPropertyChanged(nameof(Inverter2PowerText));
    }

    partial void OnInverter2AcVoltageChanged(double? value)
    {
        OnPropertyChanged(nameof(Inverter2AcDcText));
    }

    partial void OnInverter2DcVoltageChanged(double? value)
    {
        OnPropertyChanged(nameof(Inverter2AcDcText));
    }

    partial void OnInverter2DcCurrentChanged(double? value)
    {
        OnPropertyChanged(nameof(Inverter2AcDcText));
    }

    partial void OnInverter2FrequencyChanged(double? value)
    {
        OnPropertyChanged(nameof(Inverter2FrequencyText));
    }

    partial void OnCurrentChargePowerKwChanged(double? value)
    {
        OnPropertyChanged(nameof(CurrentChargePowerText));
        NotifyChargeFlowVisualChanged();
    }

    partial void OnCurrentSocChanged(double? value)
    {
        OnPropertyChanged(nameof(CurrentSocText));
        NotifyChargeFlowVisualChanged();
    }

    partial void OnTargetSocChanged(double value)
    {
        OnPropertyChanged(nameof(TargetSocText));
    }

    partial void OnChargeCurrentAChanged(double value)
    {
        double normalizedValue =
            NormalizeChargeCurrent(value);

        if (Math.Abs(normalizedValue - value) > 0.001)
        {
            ChargeCurrentA = normalizedValue;
            return;
        }

        OnPropertyChanged(nameof(ChargeCurrentText));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(StartButtonText));
        NotifyChargeFlowVisualChanged();
    }

    partial void OnModeStatusChanged(string value)
    {
        NotifyChargeFlowVisualChanged();
    }

    partial void OnChargeStateChanged(ChargeFlowState value)
    {
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(StopButtonText));
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(IsChargingActive));
        OnPropertyChanged(nameof(IsChargeTransitionActive));

        NotifyChargeFlowVisualChanged();
    }

    partial void OnCommunicationStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsCommunicationBlinking));
        NotifyChargeFlowVisualChanged();
    }

    partial void OnOperatingModeTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsOperatingModeBlinking));
    }

    partial void OnFaultLevelTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsFaultBlinking));
    }

    private void ClearInverterPanelValues()
    {
        Inverter1PowerKw = null;
        Inverter1AcVoltage = null;
        Inverter1DcVoltage = null;
        Inverter1DcCurrent = null;
        Inverter1Frequency = null;

        Inverter2PowerKw = null;
        Inverter2AcVoltage = null;
        Inverter2DcVoltage = null;
        Inverter2DcCurrent = null;
        Inverter2Frequency = null;

        ThreePhaseVoltageText = "-- / -- / -- V";
    }

    private void UpdateChargeSessionMetrics()
    {
        if (ChargeState != ChargeFlowState.Charging)
        {
            if (ChargeState == ChargeFlowState.Ready ||
                ChargeState == ChargeFlowState.Stopped ||
                ChargeState == ChargeFlowState.Disconnected)
            {
                ResetChargeSession();
            }

            return;
        }

        DateTime now = DateTime.UtcNow;

        if (_chargeStartedAtUtc is null)
        {
            _chargeStartedAtUtc = now;
            _lastEnergySampleUtc = now;
            _lastGraphSampleUtc = now;
            _cumulativeChargeEnergyKwh = 0;
            _outputHistoryKwh.Clear();
            ChargeOutputGraphPoints.Clear();
        }

        double elapsedHours =
            (now - (_lastEnergySampleUtc ?? now)).TotalHours;

        _lastEnergySampleUtc = now;

        if (CurrentChargePowerKw.HasValue && elapsedHours > 0)
        {
            _cumulativeChargeEnergyKwh +=
                CurrentChargePowerKw.Value * elapsedHours;
        }

        TimeSpan elapsed = now - _chargeStartedAtUtc.Value;

        ChargeElapsedText =
            elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        EstimatedRemainingText = BuildEstimatedRemainingText();

        if (_lastGraphSampleUtc is null ||
            (now - _lastGraphSampleUtc.Value).TotalSeconds >= GraphSampleIntervalSeconds)
        {
            _lastGraphSampleUtc = now;
            AppendGraphSample(_cumulativeChargeEnergyKwh);
        }
    }

    private void ResetChargeSession()
    {
        _chargeStartedAtUtc = null;
        _lastEnergySampleUtc = null;
        _lastGraphSampleUtc = null;
        _cumulativeChargeEnergyKwh = 0;
        _outputHistoryKwh.Clear();
        ChargeOutputGraphPoints.Clear();

        ChargeElapsedText = "--:--:--";
        EstimatedRemainingText = "-- ";
    }

    private string BuildEstimatedRemainingText()
    {
        if (!CurrentChargePowerKw.HasValue ||
            CurrentChargePowerKw.Value <= 0.01 ||
            !CurrentSoc.HasValue)
        {
            return "예상 시간 계산 중...";
        }

        double remainingSocPercent = TargetSoc - CurrentSoc.Value;

        if (remainingSocPercent <= 0)
        {
            return "목표 SOC 도달";
        }

        double remainingKwh =
            remainingSocPercent / 100.0 * BatteryCapacityKwh;

        double remainingHours =
            remainingKwh / CurrentChargePowerKw.Value;

        int hours = (int)remainingHours;
        int minutes = (int)Math.Round((remainingHours - hours) * 60);

        if (minutes >= 60)
        {
            hours += 1;
            minutes = 0;
        }

        return hours > 0
            ? $"약 {hours}시간 {minutes}분"
            : $"약 {minutes}분";
    }

    private void AppendGraphSample(double cumulativeKwh)
    {
        _outputHistoryKwh.Add(cumulativeKwh);

        if (_outputHistoryKwh.Count > GraphMaxSamples)
        {
            _outputHistoryKwh.RemoveAt(0);
        }

        RebuildGraphPoints();
    }

    private void RebuildGraphPoints()
    {
        ChargeOutputGraphPoints.Clear();

        int count = _outputHistoryKwh.Count;

        if (count < 2)
        {
            return;
        }

        double min = _outputHistoryKwh[0];
        double max = _outputHistoryKwh[0];

        foreach (double value in _outputHistoryKwh)
        {
            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }
        }

        double range = max - min;

        if (range < 0.01)
        {
            range = 0.01;
        }

        for (int i = 0; i < count; i++)
        {
            double x = GraphWidth * i / (count - 1);
            double normalized = (_outputHistoryKwh[i] - min) / range;
            double y = GraphHeight - (normalized * GraphHeight);

            ChargeOutputGraphPoints.Add(new Point(x, y));
        }
    }

    private static double GetRepresentativeValue(
        double inverter1Value,
        double inverter2Value)
    {
        const double minimumValidMagnitude = 0.001;

        bool inverter1HasValue =
            !double.IsNaN(inverter1Value) &&
            !double.IsInfinity(inverter1Value) &&
            Math.Abs(inverter1Value) > minimumValidMagnitude;

        bool inverter2HasValue =
            !double.IsNaN(inverter2Value) &&
            !double.IsInfinity(inverter2Value) &&
            Math.Abs(inverter2Value) > minimumValidMagnitude;

        if (inverter1HasValue && inverter2HasValue)
        {
            return (inverter1Value + inverter2Value) / 2.0;
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

    private static double NormalizeChargeCurrent(double value)
    {
        double clampedValue = Math.Max(
            MinChargeCurrentA,
            Math.Min(MaxChargeCurrentLimitA, value));

        return Math.Round(
            clampedValue / ChargeCurrentStepA) *
            ChargeCurrentStepA;
    }

    private void NotifyChargeFlowVisualChanged()
    {
        OnPropertyChanged(nameof(ChargeFlowBadgeText));
        OnPropertyChanged(nameof(ChargeFlowStateText));
        OnPropertyChanged(nameof(ChargeFlowSubText));

        OnPropertyChanged(nameof(ChargeFlowAcArrowOpacity));
        OnPropertyChanged(nameof(ChargeFlowDcArrowOpacity));

        OnPropertyChanged(nameof(ChargeFlowGridBrush));
        OnPropertyChanged(nameof(ChargeFlowInverterBrush));
        OnPropertyChanged(nameof(ChargeFlowBatteryBrush));

        OnPropertyChanged(nameof(ChargeFlowGridBackground));
        OnPropertyChanged(nameof(ChargeFlowInverterBackground));
        OnPropertyChanged(nameof(ChargeFlowBatteryBackground));

        OnPropertyChanged(nameof(ChargeFlowGridBorderBrush));
        OnPropertyChanged(nameof(ChargeFlowInverterBorderBrush));
        OnPropertyChanged(nameof(ChargeFlowBatteryBorderBrush));
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
            CurrentSoc = null;
            CurrentChargePowerKw = null;
            SystemVoltage = null;
            SystemCurrent = null;

            ClearInverterPanelValues();
            ResetChargeSession();

            IsRunning = false;
            ChargeState = ChargeFlowState.Disconnected;

            OperatingModeText = "미연결";
            RunStopText = "정지";
            FaultLevelText = "확인 불가";
            CommunicationStatusText = "EMS 미연결";
            AlarmSummaryText =
                "EMS 통신이 연결되어 있지 않습니다.";

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

            CurrentChargePowerKw =
                status.Inverter1PowerKw +
                status.Inverter2PowerKw;

            SystemVoltage =
                status.BatteryVoltage;

            SystemCurrent =
                status.BatteryCurrent;

            Inverter1PowerKw = status.Inverter1PowerKw;
            Inverter1AcVoltage = status.Inverter1Voltage;
            Inverter1DcVoltage = status.Inverter1DcVoltage;
            Inverter1DcCurrent = status.Inverter1DcCurrent;
            Inverter1Frequency = status.Inverter1Frequency;

            Inverter2PowerKw = status.Inverter2PowerKw;
            Inverter2AcVoltage = status.Inverter2Voltage;
            Inverter2DcVoltage = status.Inverter2DcVoltage;
            Inverter2DcCurrent = status.Inverter2DcCurrent;
            Inverter2Frequency = status.Inverter2Frequency;

            ThreePhaseVoltageText =
                $"{GetRepresentativeValue(status.Inverter1Voltage, status.Inverter2Voltage):0.0} / " +
                $"{GetRepresentativeValue(status.Inverter1BcVoltage, status.Inverter2BcVoltage):0.0} / " +
                $"{GetRepresentativeValue(status.Inverter1CaVoltage, status.Inverter2CaVoltage):0.0} V";

            UpdateChargeState(
                status.SystemStatus1,
                status.SystemStatus2,
                status.AlarmStatus1);

            UpdateChargeSessionMetrics();
        }
        catch (Exception ex)
        {
            CurrentSoc = null;
            CurrentChargePowerKw = null;
            SystemVoltage = null;
            SystemCurrent = null;

            ClearInverterPanelValues();
            ResetChargeSession();

            IsRunning = false;
            ChargeState = ChargeFlowState.Fault;

            OperatingModeText = "읽기 실패";
            RunStopText = "확인 불가";
            FaultLevelText = "확인 불가";
            CommunicationStatusText = "통신 읽기 실패";
            AlarmSummaryText =
                "EMS 상태값을 읽지 못했습니다.";

            ModeStatus = "상태 읽기 실패";
            RequestStatus =
                $"EMS 상태 읽기 실패 · {ex.Message}";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task ShowFaultDetail()
    {
        await AppDialogService.ShowWarningAsync(
            "이상 상태 상세",
            $"이상 상태 : {FaultLevelText}\n\n{AlarmSummaryText}");
    }

    [RelayCommand]
    private void DecreaseTargetSoc()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (TargetSoc > 10)
        {
            TargetSoc -= 5;
        }
    }

    [RelayCommand]
    private void IncreaseTargetSoc()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (TargetSoc < 100)
        {
            TargetSoc += 5;
        }
    }

    [RelayCommand]
    private void DecreaseChargeCurrent()
    {
        if (!CanEditSettings)
        {
            return;
        }

        ChargeCurrentA =
            NormalizeChargeCurrent(
                ChargeCurrentA -
                ChargeCurrentStepA);
    }

    [RelayCommand]
    private void IncreaseChargeCurrent()
    {
        if (!CanEditSettings)
        {
            return;
        }

        ChargeCurrentA =
            NormalizeChargeCurrent(
                ChargeCurrentA +
                ChargeCurrentStepA);
    }

    private void UpdateChargeState(
        ushort systemStatus1,
        ushort systemStatus2,
        ushort alarmStatus1)
    {
        ushort systemFaultLevel =
            GetSystemFaultLevel(systemStatus1);

        int operatingMode =
            GetOperatingMode(systemStatus1);

        bool isSystemRunning =
            IsSystemRunning(systemStatus2);

        bool hasPackOrInverterFault =
            HasPackOrInverterFault(alarmStatus1);

        bool hasCanFault =
            HasCanFault(alarmStatus1);

        OperatingModeText =
            GetOperatingModeName(operatingMode);

        RunStopText =
            isSystemRunning ? "Run" : "Stop";

        FaultLevelText =
            systemFaultLevel == 0 &&
            !hasPackOrInverterFault
                ? "정상"
                : $"이상 · Sys={systemFaultLevel}, Alarm1=0x{alarmStatus1:X4}";

        CommunicationStatusText =
            hasCanFault
                ? "CAN Fault"
                : "정상";

        // 1. Fault
        if (systemFaultLevel != 0 ||
            hasPackOrInverterFault ||
            hasCanFault)
        {
            IsRunning = false;
            ChargeState = ChargeFlowState.Fault;

            ModeStatus = "충전 고장";

            RequestStatus =
                $"충전 고장 감지 · SystemFault={systemFaultLevel} · Alarm1=0x{alarmStatus1:X4}";

            AlarmSummaryText =
                $"충전 중 이상 발생 · Alarm1=0x{alarmStatus1:X4}";

            return;
        }

        // 2. 충전 모드가 아닌 상태
        if (operatingMode != 1)
        {
            IsRunning = false;

            // Standby 상태
            if (operatingMode == 0)
            {
                // 시작 명령을 보낸 직후에는 EMS가 잠시 Standby일 수 있으므로
                // Starting 상태를 바로 Ready로 덮어쓰지 않습니다.
                if (ChargeState != ChargeFlowState.Starting &&
                    ChargeState != ChargeFlowState.Stopping)
                {
                    ChargeState = ChargeFlowState.Ready;
                }

                ModeStatus =
                    ChargeState == ChargeFlowState.Starting
                        ? "충전 시작 확인 중"
                        : ChargeState == ChargeFlowState.Stopping
                            ? "충전 정지 확인 중"
                            : "충전 대기";

                RequestStatus =
                    ChargeState == ChargeFlowState.Starting
                        ? "EMS가 충전 모드로 전환되는지 확인 중입니다."
                        : ChargeState == ChargeFlowState.Stopping
                            ? "EMS가 Standby / Stop 상태로 전환되는지 확인 중입니다."
                            : "시스템 Standby 상태입니다.";

                AlarmSummaryText = "알람 없음";
            }
            else
            {
                ChargeState = ChargeFlowState.Stopped;

                ModeStatus = "다른 모드";

                RequestStatus =
                    $"현재 운전 모드가 충전 모드가 아닙니다. 현재 모드={OperatingModeText}";

                AlarmSummaryText =
                    "충전 모드가 아닙니다.";
            }

            return;
        }

        // 3. 충전 모드 + Run
        if (isSystemRunning)
        {
            IsRunning = true;
            ChargeState = ChargeFlowState.Charging;

            ModeStatus = "충전 중";

            RequestStatus =
                $"충전 중 · SOC {CurrentSocText} · 현재전력 {CurrentChargePowerText}";

            AlarmSummaryText =
                "충전 정상 진행 중";

            return;
        }

        // 4. 충전 모드이지만 Run은 꺼진 상태
        IsRunning = false;

        if (CurrentSoc.HasValue &&
            CurrentSoc.Value >= TargetSoc)
        {
            ChargeState = ChargeFlowState.Completed;

            ModeStatus = "충전 완료";

            RequestStatus =
                $"목표 SOC 도달 · 현재 SOC {CurrentSoc.Value:0.0}% / 목표 {TargetSoc:0}%";

            AlarmSummaryText =
                "목표 SOC 도달로 충전이 완료되었습니다.";
        }
        else
        {
            if (ChargeState != ChargeFlowState.Starting &&
                ChargeState != ChargeFlowState.Stopping)
            {
                ChargeState = ChargeFlowState.Stopped;
            }

            ModeStatus =
                ChargeState == ChargeFlowState.Starting
                    ? "충전 시작 확인 중"
                    : ChargeState == ChargeFlowState.Stopping
                        ? "충전 정지 확인 중"
                        : "충전 준비";

            RequestStatus =
                ChargeState == ChargeFlowState.Starting
                    ? "EMS의 실제 Run 상태를 확인 중입니다."
                    : ChargeState == ChargeFlowState.Stopping
                        ? "EMS의 실제 Stop 상태를 확인 중입니다."
                        : "AC 자동충전 모드 진입 상태 · 시작 대기 중";

            AlarmSummaryText = "알람 없음";
        }
    }

    private static ushort GetSystemFaultLevel(
        ushort systemStatus1)
    {
        // 31022 Bit0~2
        return (ushort)(systemStatus1 & 0x0007);
    }

    private static int GetOperatingMode(
        ushort systemStatus1)
    {
        // 31022 Bit3~5
        return (systemStatus1 >> 3) & 0x0007;
    }

    private static bool IsSystemRunning(
        ushort systemStatus2)
    {
        // 31023 Bit13
        return (systemStatus2 & (1 << 13)) != 0;
    }

    private static bool HasPackOrInverterFault(
        ushort alarmStatus1)
    {
        ushort pack1FaultLevel =
            (ushort)((alarmStatus1 >> 0) & 0x0003);

        ushort pack2FaultLevel =
            (ushort)((alarmStatus1 >> 2) & 0x0003);

        ushort inverter1FaultLevel =
            (ushort)((alarmStatus1 >> 4) & 0x0003);

        ushort inverter2FaultLevel =
            (ushort)((alarmStatus1 >> 6) & 0x0003);

        return pack1FaultLevel != 0 ||
               pack2FaultLevel != 0 ||
               inverter1FaultLevel != 0 ||
               inverter2FaultLevel != 0;
    }

    private static bool HasCanFault(
        ushort alarmStatus1)
    {
        bool pack1CanFault =
            (alarmStatus1 & (1 << 12)) != 0;

        bool pack2CanFault =
            (alarmStatus1 & (1 << 13)) != 0;

        bool inverter1CanFault =
            (alarmStatus1 & (1 << 14)) != 0;

        bool inverter2CanFault =
            (alarmStatus1 & (1 << 15)) != 0;

        return pack1CanFault ||
               pack2CanFault ||
               inverter1CanFault ||
               inverter2CanFault;
    }

    private static string GetOperatingModeName(
        int mode)
    {
        return mode switch
        {
            0 => "Standby",
            1 => "AC 자동충전",
            2 => "수동제어",
            3 => "AC 외부출력",
            4 => "AC 계통방전",
            5 => "DC EV 급속충전",
            6 => "DC ESS 급속충전",
            7 => "UPS",
            _ => $"Unknown({mode})"
        };
    }

    [RelayCommand]
    private async Task StartCharge()
    {
        if (_emsService is null)
        {
            ChargeState = ChargeFlowState.Fault;
            ModeStatus = "EMS 서비스 없음";
            RequestStatus =
                "EMS 서비스가 연결되지 않았습니다.";

            return;
        }

        if (_emsService.IsOperationSequenceRunning ||
            IsRunning ||
            ChargeState == ChargeFlowState.Starting)
        {
            RequestStatus =
                "이미 운전 중이거나 다른 운전 명령을 처리 중입니다.";

            return;
        }

        if (!_emsService.IsConnected)
        {
            IsRunning = false;
            ChargeState = ChargeFlowState.Disconnected;

            ModeStatus = "EMS 미연결";
            RequestStatus =
                "EMS 통신이 연결되어 있지 않습니다.";

            await AppDialogService.ShowWarningAsync(
                "충전 시작 불가",
                "EMS 통신이 연결되어 있지 않습니다.");

            return;
        }

        bool confirmed =
            await AppDialogService.ShowConfirmAsync(
                "배터리 충전 시작",
                $"현재 설정으로 배터리 충전을 시작하시겠습니까?\n\n" +
                $"목표 SOC : {TargetSoc:0}%\n" +
                $"충전 전류 : {ChargeCurrentA:0} A",
                confirmText: "충전 시작",
                cancelText: "취소");

        if (!confirmed)
        {
            ChargeState = ChargeFlowState.Ready;
            ModeStatus = "충전 대기";
            RequestStatus =
                "배터리 충전 시작이 취소되었습니다.";

            return;
        }

        try
        {
            IsRunning = false;
            ChargeState = ChargeFlowState.Starting;

            ModeStatus = "충전 시작 중";

            RequestStatus =
                $"충전 시작 명령 전송 중 · 목표 SOC {TargetSoc:0}% · 충전 전류 {ChargeCurrentA:0}A";

            await _emsService.StartAutoChargeAsync(
                TargetSoc,
                ChargeCurrentA);

            ModeStatus = "충전 시작 확인 중";

            RequestStatus =
                "EMS 명령 전송 완료 · 실제 Run 상태를 확인 중입니다.";
        }
        catch (InvalidOperationException ex)
        {
            IsRunning = false;
            ChargeState = ChargeFlowState.Fault;

            ModeStatus = "충전 시작 불가";
            RequestStatus = ex.Message;

            await AppDialogService.ShowWarningAsync(
                "충전 시작 불가",
                ex.Message);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            ChargeState = ChargeFlowState.Fault;

            ModeStatus = "충전 시작 실패";
            RequestStatus =
                $"충전 시작 실패 · {ex.Message}";

            await AppDialogService.ShowWarningAsync(
                "충전 시작 실패",
                ex.Message);
        }
    }

    [RelayCommand]
    private async Task StopCharge()
    {
        if (_emsService is null)
        {
            ChargeState = ChargeFlowState.Fault;
            ModeStatus = "EMS 서비스 없음";
            RequestStatus =
                "EMS 서비스가 연결되지 않았습니다.";

            return;
        }

        if (!_emsService.IsConnected)
        {
            IsRunning = false;
            ChargeState = ChargeFlowState.Disconnected;

            ModeStatus = "EMS 미연결";
            RequestStatus =
                "EMS 통신이 연결되어 있지 않습니다.";

            return;
        }

        try
        {
            ChargeState = ChargeFlowState.Stopping;

            ModeStatus = "충전 정지 중";

            RequestStatus =
                "충전 정지 명령 전송 및 실제 정지 상태 확인 중";

            await _emsService.StopAllAsync();

            IsRunning = false;
            ChargeState = ChargeFlowState.Stopped;

            ModeStatus = "충전 정지";

            RequestStatus =
                "EMS가 Standby / Stop 상태로 전환되었습니다.";
        }
        catch (Exception ex)
        {
            ChargeState = ChargeFlowState.Fault;

            ModeStatus = "충전 정지 실패";
            RequestStatus =
                $"충전 정지 실패 · {ex.Message}";

            await AppDialogService.ShowWarningAsync(
                "충전 정지 실패",
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