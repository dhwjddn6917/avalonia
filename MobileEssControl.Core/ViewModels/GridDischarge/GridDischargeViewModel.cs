using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MobileEssControl.Constants;
using MobileEssControl.Services.Dialogs;
using MobileEssControl.Services.Ems;
using System;
using System.Collections.Generic;
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

public enum GraphWindowOption
{
    Minutes15,
    Minutes30,
    Hours1,
    Hours3,
    Full
}

public partial class GridDischargeViewModel : ViewModelBase, IDisposable
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

    private DateTime? _dischargeStartedAtUtc;
    private DateTime? _lastEnergySampleUtc;
    private DateTime? _lastGraphSampleUtc;
    private double _cumulativeDischargeEnergyKwh;
    private double _currentAbVoltage;
    private double _currentBcVoltage;
    private double _currentCaVoltage;

    public Points OutputGraphPoints { get; } = new();
    public Points AbVoltageGraphPoints { get; } = new();
    public Points BcVoltageGraphPoints { get; } = new();
    public Points CaVoltageGraphPoints { get; } = new();

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
    private GraphWindowOption selectedGraphWindowOption = GraphWindowOption.Minutes15;

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
    private string operatingModeText = "Standby";

    [ObservableProperty]
    private string faultLevelText = "정상";

    [ObservableProperty]
    private string communicationStatusText = "정상";

    [ObservableProperty]
    private string threePhaseVoltageText =
        "-- / -- / -- V";

    [ObservableProperty]
    private string abVoltageText = "-- V";

    [ObservableProperty]
    private string bcVoltageText = "-- V";

    [ObservableProperty]
    private string caVoltageText = "-- V";

    [ObservableProperty]
    private string threePhaseCurrentText =
        "-- / -- / -- A";

    [ObservableProperty]
    private string gridFrequencyText =
        "-- Hz";

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
    private string dischargeElapsedText = "--:--:--";

    [ObservableProperty]
    private string estimatedRemainingText = "-- ";

    [ObservableProperty]
    private string cumulativeOutputText = "0.00 kWh";

    [ObservableProperty]
    private string voltageMaxText = "--";

    [ObservableProperty]
    private string voltageMinText = "--";

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
            _ => "방전 시작"
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

    public bool IsOperatingModeBlinking =>
        OperatingModeText != "Standby" &&
        OperatingModeText != "미연결" &&
        OperatingModeText != "읽기 실패";

    public bool IsDischargeTransitionActive =>
        DischargeState == GridDischargeFlowState.Starting ||
        DischargeState == GridDischargeFlowState.Stopping;

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

    public IBrush FlowBrush =>
        DischargeState switch
        {
            GridDischargeFlowState.Fault =>
                new SolidColorBrush(Color.Parse("#DC2626")),

            GridDischargeFlowState.Starting =>
                new SolidColorBrush(Color.Parse("#D97706")),

            GridDischargeFlowState.Discharging =>
                new SolidColorBrush(Color.Parse("#C2410C")),

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
                new SolidColorBrush(Color.Parse("#FFF7ED")),

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
                new SolidColorBrush(Color.Parse("#FDBA74")),

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
        OnPropertyChanged(nameof(IsDischargeTransitionActive));

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
        ThreePhaseCurrentText = "-- / -- / -- A";
        GridFrequencyText = "-- Hz";
    }

    private void UpdateDischargeSessionMetrics()
    {
        if (DischargeState != GridDischargeFlowState.Discharging)
        {
            if (DischargeState == GridDischargeFlowState.Ready ||
                DischargeState == GridDischargeFlowState.Stopped ||
                DischargeState == GridDischargeFlowState.Disconnected)
            {
                ResetDischargeSession();
            }

            return;
        }

        DateTime now = DateTime.UtcNow;

        if (_dischargeStartedAtUtc is null)
        {
            _dischargeStartedAtUtc = now;
            _lastEnergySampleUtc = now;
            _lastGraphSampleUtc = now;
            _cumulativeDischargeEnergyKwh = 0;
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

        if (CurrentDischargePowerKw.HasValue && elapsedHours > 0)
        {
            _cumulativeDischargeEnergyKwh +=
                CurrentDischargePowerKw.Value * elapsedHours;
        }

        TimeSpan elapsed = now - _dischargeStartedAtUtc.Value;

        DischargeElapsedText =
            elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        EstimatedRemainingText = BuildEstimatedRemainingText();

        if (_lastGraphSampleUtc is null ||
            (now - _lastGraphSampleUtc.Value).TotalSeconds >= GraphSampleIntervalSeconds)
        {
            _lastGraphSampleUtc = now;
            AppendGraphSample(_cumulativeDischargeEnergyKwh);
            AppendVoltageGraphSample(_currentAbVoltage, _currentBcVoltage, _currentCaVoltage);
            CumulativeOutputText = $"{_cumulativeDischargeEnergyKwh:0.00} kWh";
        }
    }

    private void ResetDischargeSession()
    {
        _dischargeStartedAtUtc = null;
        _lastEnergySampleUtc = null;
        _lastGraphSampleUtc = null;
        _cumulativeDischargeEnergyKwh = 0;
        _outputHistoryKwh.Clear();
        OutputGraphPoints.Clear();

        _abVoltageHistory.Clear();
        _bcVoltageHistory.Clear();
        _caVoltageHistory.Clear();
        AbVoltageGraphPoints.Clear();
        BcVoltageGraphPoints.Clear();
        CaVoltageGraphPoints.Clear();

        DischargeElapsedText = "--:--:--";
        EstimatedRemainingText = "-- ";
        CumulativeOutputText = "0.00 kWh";
        VoltageMaxText = "--";
        VoltageMinText = "--";
    }

    private string BuildEstimatedRemainingText()
    {
        if (!CurrentDischargePowerKw.HasValue ||
            CurrentDischargePowerKw.Value <= 0.01 ||
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
            remainingKwh / CurrentDischargePowerKw.Value;

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

    private void UpdateInverterAcRepresentativeValues(
        MobileEssControl.Models.System.EssStatusData status)
    {
        // 운전모드, SystemRun, WorkingMode, PowerOnOff 상태와 관계없이
        // EMS에서 읽은 인버터 1/2의 실제 AC 상태값을 항상 표시합니다.
        //
        // 두 인버터 모두 값이 있으면 평균값을 표시하고,
        // 한 대만 값이 있으면 해당 인버터 값을 표시합니다.
        // 두 값 모두 0이면 0을 표시합니다.

        _currentAbVoltage =
            GetRepresentativeValue(
                status.Inverter1Voltage,
                status.Inverter2Voltage);

        _currentBcVoltage =
            GetRepresentativeValue(
                status.Inverter1BcVoltage,
                status.Inverter2BcVoltage);

        _currentCaVoltage =
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
            $"{_currentAbVoltage:0.0} / " +
            $"{_currentBcVoltage:0.0} / " +
            $"{_currentCaVoltage:0.0} V";

        AbVoltageText = $"{_currentAbVoltage:0.0} V";
        BcVoltageText = $"{_currentBcVoltage:0.0} V";
        CaVoltageText = $"{_currentCaVoltage:0.0} V";

        ThreePhaseCurrentText =
            $"{phaseACurrent:0.00} / " +
            $"{phaseBCurrent:0.00} / " +
            $"{phaseCCurrent:0.00} A";

        GridFrequencyText =
            $"{frequency:0.00} Hz";
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
            ClearInverterPanelValues();
            ResetDischargeSession();

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

            CurrentDischargePowerKw =
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

            UpdateInverterAcRepresentativeValues(status);

            UpdateDischargeState(
                status.SystemStatus1,
                status.SystemStatus2,
                status.AlarmStatus1);

            UpdateDischargeSessionMetrics();
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
            ClearInverterPanelValues();
            ResetDischargeSession();

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

        OperatingModeText = GetOperatingModeName(operatingMode);

        FaultLevelText =
            systemFaultLevel == 0 && !hasPackOrInverterFault
                ? "정상"
                : $"이상 · Sys={systemFaultLevel}, Alarm1=0x{alarmStatus1:X4}";

        CommunicationStatusText =
            hasCanFault ? "CAN Fault" : "정상";

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
