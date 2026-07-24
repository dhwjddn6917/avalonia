using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MobileEssControl.Constants;
using MobileEssControl.Models.System;
using MobileEssControl.Services.Dialogs;
using MobileEssControl.Services.Ems;
using System;
using System.Collections.Generic;
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

public enum GraphWindowOption
{
    Minutes15,
    Minutes30,
    Hours1,
    Hours3,
    Full
}

public partial class ExternalOutputViewModel : ViewModelBase, IDisposable
{
    private const double BatteryCapacityKwh = 77.4;
    private const int GraphSampleIntervalSeconds = 5;
    private const double GraphWidth = 280;
    private const double GraphHeight = 70;

    private readonly EmsService? _emsService;
    private readonly DispatcherTimer? _refreshTimer;
    private readonly List<double> _outputHistoryKwh = new();
    private readonly List<double> _abVoltageHistory = new();
    private readonly List<double> _bcVoltageHistory = new();
    private readonly List<double> _caVoltageHistory = new();

    private bool _isRefreshing;
    private bool _disposed;

    // 외부출력 운전 중 전압/주파수 이상이 3회 연속 확인되면 자동 정지합니다.
    private const int ExternalOutputSafetyTripCount = 3;

    private int _externalOutputElectricalFaultCount;
    private bool _isExternalOutputSafetyStopInProgress;
    private bool _externalOutputSafetyTripLatched;
    private string _externalOutputSafetyTripMessage = string.Empty;

    private DateTime? _outputStartedAtUtc;
    private DateTime? _lastEnergySampleUtc;
    private DateTime? _lastGraphSampleUtc;
    private double _cumulativeOutputEnergyKwh;
    private double _currentAbVoltage;
    private double _currentBcVoltage;
    private double _currentCaVoltage;

    public Points OutputGraphPoints { get; } = new();
    public Points AbVoltageGraphPoints { get; } = new();
    public Points BcVoltageGraphPoints { get; } = new();
    public Points CaVoltageGraphPoints { get; } = new();

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
    private GraphWindowOption selectedGraphWindowOption = GraphWindowOption.Minutes15;

    [ObservableProperty]
    private double outputLimitKw = 40;

    [ObservableProperty]
    private double minSoc = 20;

    [ObservableProperty]
    private double phaseVoltage = 220;

    [ObservableProperty]
    private double frequencyHz = 60;

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

    [ObservableProperty]
    private string operatingModeText = "Standby";

    [ObservableProperty]
    private string faultLevelText = "정상";

    [ObservableProperty]
    private string communicationStatusText = "정상";

    [ObservableProperty]
    private string threePhaseVoltageText = "-- / -- / -- V";

    [ObservableProperty]
    private string abVoltageText = "-- V";

    [ObservableProperty]
    private string bcVoltageText = "-- V";

    [ObservableProperty]
    private string caVoltageText = "-- V";

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
    private string outputElapsedText = "--:--:--";

    [ObservableProperty]
    private string estimatedRemainingText = "-- ";

    [ObservableProperty]
    private string cumulativeOutputText = "0.00 kWh";

    [ObservableProperty]
    private string voltageMaxText = "--";

    [ObservableProperty]
    private string voltageMinText = "--";

    public string OutputLimitText =>
        $"{OutputLimitKw:0} kW";

    public string MinSocText =>
        $"{MinSoc:0}%";

    public string PhaseVoltageText =>
        $"{PhaseVoltage:0} V";

    public string FrequencyHzText =>
        $"{FrequencyHz:0.0} Hz";

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

    public bool IsOperatingModeBlinking =>
        OperatingModeText != "Standby" &&
        OperatingModeText != "미연결" &&
        OperatingModeText != "읽기 실패";

    public bool IsOutputTransitionActive =>
        OutputState == ExternalOutputFlowState.Starting ||
        OutputState == ExternalOutputFlowState.Stopping;

    public bool IsFaultBlinking =>
        FaultLevelText != "정상";

    public string GraphWindowOptionText =>
        SelectedGraphWindowOption switch
        {
            GraphWindowOption.Minutes15 => "최근 15분",
            GraphWindowOption.Minutes30 => "최근 30분",
            GraphWindowOption.Hours1 => "최근 1시간",
            GraphWindowOption.Hours3 => "최근 3시간",
            GraphWindowOption.Full => "전체 세션",
            _ => "최근 15분"
        };

    public bool IsGraphWindow15Selected =>
        SelectedGraphWindowOption == GraphWindowOption.Minutes15;

    public bool IsGraphWindow30Selected =>
        SelectedGraphWindowOption == GraphWindowOption.Minutes30;

    public bool IsGraphWindow1hSelected =>
        SelectedGraphWindowOption == GraphWindowOption.Hours1;

    public bool IsGraphWindow3hSelected =>
        SelectedGraphWindowOption == GraphWindowOption.Hours3;

    public bool IsGraphWindowFullSelected =>
        SelectedGraphWindowOption == GraphWindowOption.Full;

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

    partial void OnPhaseVoltageChanged(double value)
    {
        if (value < 200)
        {
            PhaseVoltage = 200;
            return;
        }

        if (value > 240)
        {
            PhaseVoltage = 240;
            return;
        }

        OnPropertyChanged(nameof(PhaseVoltageText));
    }

    partial void OnFrequencyHzChanged(double value)
    {
        if (value < 45)
        {
            FrequencyHz = 45;
            return;
        }

        if (value > 65)
        {
            FrequencyHz = 65;
            return;
        }

        OnPropertyChanged(nameof(FrequencyHzText));
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
        OnPropertyChanged(nameof(IsOutputTransitionActive));

        NotifyFlowVisualChanged();
    }

    partial void OnOperatingModeTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsOperatingModeBlinking));
    }

    partial void OnFaultLevelTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsFaultBlinking));
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

    partial void OnSelectedGraphWindowOptionChanged(GraphWindowOption value)
    {
        OnPropertyChanged(nameof(GraphWindowOptionText));
        OnPropertyChanged(nameof(IsGraphWindow15Selected));
        OnPropertyChanged(nameof(IsGraphWindow30Selected));
        OnPropertyChanged(nameof(IsGraphWindow1hSelected));
        OnPropertyChanged(nameof(IsGraphWindow3hSelected));
        OnPropertyChanged(nameof(IsGraphWindowFullSelected));

        TrimGraphHistoriesToWindow();
        RebuildGraphPoints();
        RebuildVoltageGraphPoints();
    }

    [RelayCommand]
    private void SetGraphWindow(string option)
    {
        SelectedGraphWindowOption = option switch
        {
            "15" => GraphWindowOption.Minutes15,
            "30" => GraphWindowOption.Minutes30,
            "60" => GraphWindowOption.Hours1,
            "180" => GraphWindowOption.Hours3,
            "full" => GraphWindowOption.Full,
            _ => SelectedGraphWindowOption
        };
    }

    [RelayCommand]
    private async Task ShowFaultDetail()
    {
        await AppDialogService.ShowWarningAsync(
            "이상 상태 상세",
            $"이상 상태 : {FaultLevelText}\n\n{RequestStatus}");
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
        AbVoltageText = "-- V";
        BcVoltageText = "-- V";
        CaVoltageText = "-- V";
    }

    private void UpdateOutputSessionMetrics()
    {
        if (OutputState != ExternalOutputFlowState.Outputting)
        {
            if (OutputState == ExternalOutputFlowState.Ready ||
                OutputState == ExternalOutputFlowState.Stopped ||
                OutputState == ExternalOutputFlowState.Disconnected)
            {
                ResetOutputSession();
            }

            return;
        }

        DateTime now = DateTime.UtcNow;

        if (_outputStartedAtUtc is null)
        {
            _outputStartedAtUtc = now;
            _lastEnergySampleUtc = now;
            _lastGraphSampleUtc = now;
            _cumulativeOutputEnergyKwh = 0;
            _outputHistoryKwh.Clear();
            OutputGraphPoints.Clear();

            _abVoltageHistory.Clear();
            _bcVoltageHistory.Clear();
            _caVoltageHistory.Clear();
            AbVoltageGraphPoints.Clear();
            BcVoltageGraphPoints.Clear();
            CaVoltageGraphPoints.Clear();
        }

        double elapsedHours =
            (now - (_lastEnergySampleUtc ?? now)).TotalHours;

        _lastEnergySampleUtc = now;

        if (CurrentOutputPowerKw.HasValue && elapsedHours > 0)
        {
            _cumulativeOutputEnergyKwh +=
                CurrentOutputPowerKw.Value * elapsedHours;
        }

        TimeSpan elapsed = now - _outputStartedAtUtc.Value;

        OutputElapsedText =
            elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        EstimatedRemainingText = BuildEstimatedRemainingText();

        if (_lastGraphSampleUtc is null ||
            (now - _lastGraphSampleUtc.Value).TotalSeconds >= GraphSampleIntervalSeconds)
        {
            _lastGraphSampleUtc = now;
            AppendGraphSample(_cumulativeOutputEnergyKwh);
            AppendVoltageGraphSample(_currentAbVoltage, _currentBcVoltage, _currentCaVoltage);
            CumulativeOutputText = $"{_cumulativeOutputEnergyKwh:0.00} kWh";
        }
    }

    private void ResetOutputSession()
    {
        _outputStartedAtUtc = null;
        _lastEnergySampleUtc = null;
        _lastGraphSampleUtc = null;
        _cumulativeOutputEnergyKwh = 0;
        _outputHistoryKwh.Clear();
        OutputGraphPoints.Clear();

        _abVoltageHistory.Clear();
        _bcVoltageHistory.Clear();
        _caVoltageHistory.Clear();
        AbVoltageGraphPoints.Clear();
        BcVoltageGraphPoints.Clear();
        CaVoltageGraphPoints.Clear();

        OutputElapsedText = "--:--:--";
        EstimatedRemainingText = "-- ";
        CumulativeOutputText = "0.00 kWh";
        VoltageMaxText = "--";
        VoltageMinText = "--";
    }

    private string BuildEstimatedRemainingText()
    {
        if (!CurrentOutputPowerKw.HasValue ||
            CurrentOutputPowerKw.Value <= 0.01 ||
            !CurrentSoc.HasValue)
        {
            return "예상 시간 계산 중...";
        }

        double remainingSocPercent = CurrentSoc.Value - MinSoc;

        if (remainingSocPercent <= 0)
        {
            return "최저 SOC 도달";
        }

        double remainingKwh =
            remainingSocPercent / 100.0 * BatteryCapacityKwh;

        double remainingHours =
            remainingKwh / CurrentOutputPowerKw.Value;

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

    private int GetGraphMaxSamples()
    {
        int windowSeconds = SelectedGraphWindowOption switch
        {
            GraphWindowOption.Minutes15 => 15 * 60,
            GraphWindowOption.Minutes30 => 30 * 60,
            GraphWindowOption.Hours1 => 60 * 60,
            GraphWindowOption.Hours3 => 3 * 60 * 60,
            GraphWindowOption.Full => int.MaxValue,
            _ => 15 * 60
        };

        return windowSeconds == int.MaxValue
            ? int.MaxValue
            : Math.Max(2, windowSeconds / GraphSampleIntervalSeconds);
    }

    private void TrimGraphHistoriesToWindow()
    {
        int maxSamples = GetGraphMaxSamples();

        if (maxSamples == int.MaxValue)
        {
            return;
        }

        TrimListToMax(_outputHistoryKwh, maxSamples);
        TrimListToMax(_abVoltageHistory, maxSamples);
        TrimListToMax(_bcVoltageHistory, maxSamples);
        TrimListToMax(_caVoltageHistory, maxSamples);
    }

    private static void TrimListToMax(List<double> list, int maxSamples)
    {
        while (list.Count > maxSamples)
        {
            list.RemoveAt(0);
        }
    }

    private void AppendGraphSample(double cumulativeKwh)
    {
        _outputHistoryKwh.Add(cumulativeKwh);

        int maxSamples = GetGraphMaxSamples();

        if (maxSamples != int.MaxValue && _outputHistoryKwh.Count > maxSamples)
        {
            _outputHistoryKwh.RemoveAt(0);
        }

        RebuildGraphPoints();
    }

    private void RebuildGraphPoints()
    {
        OutputGraphPoints.Clear();

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

            OutputGraphPoints.Add(new Point(x, y));
        }
    }

    private void AppendVoltageGraphSample(
        double abVoltage,
        double bcVoltage,
        double caVoltage)
    {
        _abVoltageHistory.Add(abVoltage);
        _bcVoltageHistory.Add(bcVoltage);
        _caVoltageHistory.Add(caVoltage);

        int maxSamples = GetGraphMaxSamples();

        if (maxSamples != int.MaxValue && _abVoltageHistory.Count > maxSamples)
        {
            _abVoltageHistory.RemoveAt(0);
            _bcVoltageHistory.RemoveAt(0);
            _caVoltageHistory.RemoveAt(0);
        }

        RebuildVoltageGraphPoints();
    }

    private void RebuildVoltageGraphPoints()
    {
        AbVoltageGraphPoints.Clear();
        BcVoltageGraphPoints.Clear();
        CaVoltageGraphPoints.Clear();

        int count = _abVoltageHistory.Count;

        if (count < 2)
        {
            VoltageMaxText = "--";
            VoltageMinText = "--";
            return;
        }

        double min = double.MaxValue;
        double max = double.MinValue;

        foreach (List<double> series in new[] { _abVoltageHistory, _bcVoltageHistory, _caVoltageHistory })
        {
            foreach (double value in series)
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
        }

        VoltageMaxText = $"최고 {max:0.0} V";
        VoltageMinText = $"최저 {min:0.0} V";

        double range = max - min;

        if (range < 1)
        {
            range = 1;
        }

        FillVoltageGraphPoints(_abVoltageHistory, AbVoltageGraphPoints, min, range, count);
        FillVoltageGraphPoints(_bcVoltageHistory, BcVoltageGraphPoints, min, range, count);
        FillVoltageGraphPoints(_caVoltageHistory, CaVoltageGraphPoints, min, range, count);
    }

    private static void FillVoltageGraphPoints(
        List<double> history,
        Points target,
        double min,
        double range,
        int count)
    {
        for (int i = 0; i < count; i++)
        {
            double x = GraphWidth * i / (count - 1);
            double normalized = (history[i] - min) / range;
            double y = GraphHeight - (normalized * GraphHeight);

            target.Add(new Point(x, y));
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

    private static string GetOperatingModeName(EmsOperationMode mode) =>
        mode switch
        {
            EmsOperationMode.Standby => "Standby",
            EmsOperationMode.AutoCharge => "AC 자동충전",
            EmsOperationMode.ManualControl => "수동제어",
            EmsOperationMode.ExternalOutput => "AC 외부출력",
            EmsOperationMode.GridDischarge => "AC 계통방전",
            EmsOperationMode.DcEvFastCharge => "DC EV 급속충전",
            EmsOperationMode.DcEssFastCharge => "DC ESS 급속충전",
            EmsOperationMode.GridUpsDischarge => "UPS",
            _ => $"Unknown({mode})"
        };

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

            ClearInverterPanelValues();
            ResetOutputSession();

            ModeStatus = "EMS 미연결";
            RequestStatus =
                "EMS 통신이 연결되어 있지 않습니다.";

            OperatingModeText = "미연결";
            FaultLevelText = "확인 불가";
            CommunicationStatusText = "EMS 미연결";

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

            _currentAbVoltage =
                GetRepresentativeValue(status.Inverter1Voltage, status.Inverter2Voltage);

            _currentBcVoltage =
                GetRepresentativeValue(status.Inverter1BcVoltage, status.Inverter2BcVoltage);

            _currentCaVoltage =
                GetRepresentativeValue(status.Inverter1CaVoltage, status.Inverter2CaVoltage);

            ThreePhaseVoltageText =
                $"{_currentAbVoltage:0.0} / {_currentBcVoltage:0.0} / {_currentCaVoltage:0.0} V";
            AbVoltageText = $"{_currentAbVoltage:0.0} V";
            BcVoltageText = $"{_currentBcVoltage:0.0} V";
            CaVoltageText = $"{_currentCaVoltage:0.0} V";

            UpdateOutputState(
                status.SystemStatus1,
                status.SystemStatus2,
                status.AlarmStatus1);

            UpdateOutputSessionMetrics();

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

            ClearInverterPanelValues();
            ResetOutputSession();

            ModeStatus = "상태 읽기 실패";
            RequestStatus =
                $"EMS 상태 읽기 실패 · {ex.Message}";

            OperatingModeText = "읽기 실패";
            FaultLevelText = "확인 불가";
            CommunicationStatusText = "통신 읽기 실패";
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

        OperatingModeText = GetOperatingModeName(operatingMode);

        FaultLevelText =
            systemFaultLevel == 0 && !hasPackOrInverterFault
                ? "정상"
                : $"이상 · Sys={systemFaultLevel}, Alarm1=0x{alarmStatus1:X4}";

        CommunicationStatusText =
            hasCanFault ? "CAN Fault" : "정상";

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
    private void DecreasePhaseVoltage()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (PhaseVoltage > 200)
        {
            PhaseVoltage -= 1;
        }
    }

    [RelayCommand]
    private void IncreasePhaseVoltage()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (PhaseVoltage < 240)
        {
            PhaseVoltage += 1;
        }
    }

    [RelayCommand]
    private void DecreaseFrequency()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (FrequencyHz > 45)
        {
            FrequencyHz -= 0.5;
        }
    }

    [RelayCommand]
    private void IncreaseFrequency()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (FrequencyHz < 65)
        {
            FrequencyHz += 0.5;
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
                $"최저 SOC : {MinSoc:0}%\n" +
                $"상전압 : {PhaseVoltage:0} V\n" +
                $"주파수 : {FrequencyHz:0.0} Hz\n\n" +
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
                $"외부 출력 명령 전송 중 · 출력 제한 {OutputLimitKw:0}kW · " +
                $"최저 SOC {MinSoc:0}% · 상전압 {PhaseVoltage:0}V · 주파수 {FrequencyHz:0.0}Hz";

            await _emsService.StartExternalOutputAsync(
                OutputLimitKw,
                MinSoc,
                PhaseVoltage,
                FrequencyHz);

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
