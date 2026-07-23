using MobileEssControl.Constants;
using MobileEssControl.Models.Admin;
using MobileEssControl.Models.System;
using MobileEssControl.Services.Interfaces;
using MobileEssControl.Services.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using static MobileEssControl.Constants.EmsControlWord1;


namespace MobileEssControl.Services.Ems;

public class EmsService
{
    private readonly IModbusService _modbusService;

    private ushort? _lastLoggedSystemSocRaw;

    // 외부출력 안전검사 기준
    // 현재 EssStatusData에는 인버터별 AB 선간전압만 있으므로 AB 전압 기준으로 검사합니다.
    private const double ExternalOutputZeroVoltageMaximumV = 5.0;
    private const double ExternalOutputNormalVoltageMinimumV = 342.0;
    private const double ExternalOutputNormalVoltageMaximumV = 418.0;
    private const double ExternalOutputNormalFrequencyMinimumHz = 57.0;
    private const double ExternalOutputNormalFrequencyMaximumHz = 63.0;
    private const int ExternalOutputRequiredConsecutiveChecks = 3;

    // 자동충전/계통방전/외부출력/정지 시퀀스 전체를 보호합니다.
    // 시퀀스가 진행되는 동안 일반 화면의 ReadStatusAsync 호출은 대기합니다.
    private readonly SemaphoreSlim _operationSequenceLock = new(1, 1);

    // 30001/30002 Read-Modify-Write 작업을 하나의 묶음으로 보호합니다.
    // 여러 화면이나 명령이 동시에 들어와 현재값을 서로 덮어쓰는 것을 방지합니다.
    private readonly SemaphoreSlim _controlWordUpdateLock = new(1, 1);

    private volatile bool _isOperationSequenceRunning;
    private volatile string? _currentOperationSequenceName;

    // 관리자 화면에서 사용자가 직접 연결 해제를 선택한 경우
    // 자동 재연결이 즉시 다시 포트를 여는 것을 방지합니다.
    private volatile bool _isAutoReconnectSuppressed;

    public bool IsOperationSequenceRunning =>
        _isOperationSequenceRunning;

    public string? CurrentOperationSequenceName =>
        _currentOperationSequenceName;

    public bool IsAutoReconnectSuppressed =>
        _isAutoReconnectSuppressed;

    // 시작/정지 시퀀스 동안 공통 화면 이동을 즉시 잠그기 위한 이벤트
    public event Action<bool>? OperationSequenceStateChanged;

    // 관리자 화면에 통신 진행 상태를 전달하는 이벤트
    public event Action<string>? CommunicationLogReceived;

    public EmsService(IModbusService modbusService)
    {
        _modbusService = modbusService;

        _modbusService.CommunicationLog += WriteLog;
    }
    public bool IsConnected => _modbusService.IsConnected;
    public string? ConnectedPortName => _modbusService.ConnectedPortName;

    private async Task BeginOperationSequenceAsync(
        string sequenceName,
        CancellationToken cancellationToken)
    {
        await _operationSequenceLock.WaitAsync(
            cancellationToken);

        _currentOperationSequenceName = sequenceName;
        _isOperationSequenceRunning = true;

        OperationSequenceStateChanged?.Invoke(true);

        WriteLog(
            $"{sequenceName} 시퀀스 시작 · " +
            "일반 화면 자동 상태 Read 일시 중지");
    }

    private void EndOperationSequence(
        string sequenceName)
    {
        _isOperationSequenceRunning = false;
        _currentOperationSequenceName = null;

        _operationSequenceLock.Release();
        OperationSequenceStateChanged?.Invoke(false);

        WriteLog(
            $"{sequenceName} 시퀀스 종료 · " +
            "일반 화면 자동 상태 Read 재개");
    }

    public async Task<bool> ConnectAsync(
        string portName,
        CancellationToken cancellationToken = default)
    {
        _isAutoReconnectSuppressed = false;

        return await _modbusService.ConnectAsync(
            portName,
            baudRate: 115200,
            cancellationToken);
    }
    private async Task CheckCanStartOperationAsync(
     EmsOperationMode requiredMode,
     string modeName,
     CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "EMS 통신이 연결되어 있지 않습니다.");
        }

        EssStatusData status = await ReadStatusCoreAsync(cancellationToken);

        // 31023 System Status2 Bit10 : EmStopSwitchSts
        // 비상정지 스위치가 ON이면 운전 관련 명령을 한 건도 전송하지 않습니다.
        bool isEmergencyStopOn =
            (status.SystemStatus2 & (1 << 10)) != 0;

        if (isEmergencyStopOn)
        {
            throw new InvalidOperationException(
                "비상정지 스위치가 ON 상태입니다.\n" +
                "비상정지를 해제한 후 다시 시작하세요.");
        }

        // 1. 이미 Run 상태인지 확인
        if (EmsSystemStatus2.IsRunning(status.SystemStatus2))
        {
            throw new InvalidOperationException(
                $"이미 운전 중입니다. 현재 모드={EmsSystemStatus1.GetOperatingMode(status.SystemStatus1)}");
        }

        // 2. System Fault Level 확인
        ushort systemFaultLevel =
            EmsSystemStatus1.GetSystemFaultLevel(status.SystemStatus1);

        if (systemFaultLevel != 0)
        {
            throw new InvalidOperationException(
                $"시스템 Fault 상태입니다. SystemFaultLevel={systemFaultLevel}");
        }

        // 3. Operating Mode 확인
        //
        // 시작 전에는 Standby 상태가 정상입니다.
        // Standby이면 아래에서 설정값 Write 후 requiredMode로 전환합니다.
        // 이미 requiredMode에 들어가 있지만 Run만 꺼져 있는 상태도 시작 허용합니다.
        // 단, 다른 운전모드이면 안전상 시작을 막습니다.
        EmsOperationMode currentMode =
            EmsSystemStatus1.GetOperatingMode(status.SystemStatus1);

        if (currentMode != EmsOperationMode.Standby &&
            currentMode != requiredMode)
        {
            throw new InvalidOperationException(
                $"{modeName} 시작 불가. 현재 모드={currentMode}, 시작 가능 모드=Standby 또는 {requiredMode}");
        }

        // 4. Pack / Inverter Fault 확인
        if (EmsSystemAlarms1.HasPackOrInverterFault(status.AlarmStatus1))
        {
            throw new InvalidOperationException(
                $"Pack/Inverter Fault 상태입니다. Alarm1=0x{status.AlarmStatus1:X4}");
        }

        // 5. CAN 통신 Fault 확인
        if (EmsSystemAlarms1.HasCanFault(status.AlarmStatus1))
        {
            throw new InvalidOperationException(
                $"Pack/Inverter CAN 통신 Fault 상태입니다. Alarm1=0x{status.AlarmStatus1:X4}");
        }
    }
    public async Task<string?> FindAndConnectAsync(
    CancellationToken cancellationToken = default)
    {
        _isAutoReconnectSuppressed = false;

        WriteLog("EMS USB 자동 연결 시작");

        string? portName =
            await _modbusService.FindAndConnectEmsAsync(
                baudRate: 115200,
                cancellationToken: cancellationToken);

        if (portName is null)
        {
            WriteLog("EMS USB 자동 연결 실패");
        }
        else
        {
            WriteLog(
                $"EMS USB 자동 연결 성공 · {portName}");
        }

        return portName;
    }

    public async Task DisconnectAsync()
    {
        _isAutoReconnectSuppressed = true;

        WriteLog(
            $"포트 연결 해제 요청 · " +
            $"{ConnectedPortName ?? "현재 포트"}");

        await _modbusService.DisconnectAsync();

        WriteLog("포트 연결 해제 완료");
    }

    public async Task<EssStatusData> ReadStatusAsync(
        CancellationToken cancellationToken = default)
    {
        // 운전 시퀀스 중에는 화면 자동 Read가 명령 사이에 끼어들지 않도록
        // 시퀀스가 끝날 때까지 비동기로 대기합니다.
        await _operationSequenceLock.WaitAsync(
            cancellationToken);

        try
        {
            return await ReadStatusCoreAsync(
                cancellationToken);
        }
        finally
        {
            _operationSequenceLock.Release();
        }
    }

    /// <summary>
    /// 모바일 TCP 전송용 상태와 설정값을 한 번의 보호 구간에서 읽습니다.
    /// 시작/정지 시퀀스 중에는 명령 사이에 상태 Read가 끼어들지 않습니다.
    /// Control ESS 읽기만 실패하면 상태값은 유지하고 설정값 배열만 비워 반환합니다.
    /// </summary>
    public async Task<(EssStatusData Status, ushort[] ControlValues)>
        ReadMobileTelemetrySnapshotAsync(
            CancellationToken cancellationToken = default)
    {
        await _operationSequenceLock.WaitAsync(
            cancellationToken);

        try
        {
            EssStatusData status =
                await ReadStatusCoreAsync(
                    cancellationToken);

            ushort[] controlValues =
                Array.Empty<ushort>();

            try
            {
                controlValues =
                    await ReadControlEssAsync(
                        cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                WriteLog(
                    $"모바일 전송용 Control ESS 읽기 실패 · " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            return (
                Status: status,
                ControlValues: controlValues);
        }
        finally
        {
            _operationSequenceLock.Release();
        }
    }

    private async Task<EssStatusData> ReadStatusCoreAsync(
        CancellationToken cancellationToken = default)
    {
        // EMS System
        // 새 주소맵 기준 System Voltage = Relative Address 20
        // 20 ~ 39까지 읽으면 System Voltage / Current / SOC / Status / Alarm을 모두 포함합니다.
        ushort[] emsValues = await _modbusService.ReadHoldingRegistersAsync(
            SlaveIds.Ems,
            ModbusAddresses.SystemVoltage,
            15,
            cancellationToken);

        int systemSocIndex =
    EmsOffsets.SystemSoc -
    EmsOffsets.SystemVoltage;

        ushort systemSocRaw =
            emsValues[systemSocIndex];

        // SOC 원시값이 바뀔 때만 관리자 통신 로그에 표시
        if (_lastLoggedSystemSocRaw != systemSocRaw)
        {
            _lastLoggedSystemSocRaw = systemSocRaw;

            WriteLog(
                $"RX EMS SOC · 31017 Raw={systemSocRaw} " +
                $"(0x{systemSocRaw:X4}) · " +
                $"/10={systemSocRaw / 10.0:0.0}% · " +
                $"/100={systemSocRaw / 100.0:0.00}% · " +
                $"/1000={systemSocRaw / 1000.0:0.000}%");
        }

        // Inverter 1 : 40001 ~ 40030
        // 3상 전압/전류, 주파수, 출력전력, 운전모드를 함께 읽습니다.
        ushort[] inverter1Values = await _modbusService.ReadHoldingRegistersAsync(
            SlaveIds.Ems,
            40001,
            30,
            cancellationToken);

        // Inverter 2 : 41001 ~ 41030
        ushort[] inverter2Values = await _modbusService.ReadHoldingRegistersAsync(
            SlaveIds.Ems,
            41001,
            30,
            cancellationToken);

        return new EssStatusData
        {
            // EMS: Offset 7부터 읽었으므로 배열 index 0 = Offset 7
            BatteryVoltage = emsValues[
                EmsOffsets.SystemVoltage - EmsOffsets.SystemVoltage] / 100.0,

            BatteryCurrent = ToInt16(emsValues[
                EmsOffsets.SystemCurrent - EmsOffsets.SystemVoltage]) / 100.0,

            // 현재 Fake EMS에서는 SOC를 0.01% 단위로 관리
            Soc = emsValues[
    EmsOffsets.SystemSoc - EmsOffsets.SystemVoltage] / 10.0,

            SystemStatus1 = emsValues[
                EmsOffsets.SystemStatus1 - EmsOffsets.SystemVoltage],

            SystemStatus2 = emsValues[
                EmsOffsets.SystemStatus2 - EmsOffsets.SystemVoltage],

            AlarmStatus1 = emsValues[
                EmsOffsets.SystemAlarm1 - EmsOffsets.SystemVoltage],

            AlarmStatus2 = emsValues[
                EmsOffsets.SystemAlarm2 - EmsOffsets.SystemVoltage],


            // Inverter 1
            // 배열 index 0/2/4 = 40001/40003/40005 A/B/C상 전압, 0.1 V
            Inverter1PhaseAVoltage =
                ToInt16(inverter1Values[0]) / 10.0,

            Inverter1PhaseBVoltage =
                ToInt16(inverter1Values[2]) / 10.0,

            Inverter1PhaseCVoltage =
                ToInt16(inverter1Values[4]) / 10.0,

            // 40007~40009 = AB/BC/CA 선간전압, 0.1 V
            Inverter1Voltage = ToInt16(inverter1Values[
                InverterOffsets.AbLineVoltage]) / 10.0,

            Inverter1BcVoltage = ToInt16(inverter1Values[
                InverterOffsets.BcLineVoltage]) / 10.0,

            Inverter1CaVoltage = ToInt16(inverter1Values[
                InverterOffsets.CaLineVoltage]) / 10.0,

            // 40002/40004/40006 = A/B/C상 전류, 0.01 A
            Inverter1PhaseACurrent = ToInt16(inverter1Values[
                InverterOffsets.APhaseCurrent]) / 100.0,

            Inverter1PhaseBCurrent = ToInt16(inverter1Values[
                InverterOffsets.BPhaseCurrent]) / 100.0,

            Inverter1PhaseCCurrent = ToInt16(inverter1Values[
                InverterOffsets.CPhaseCurrent]) / 100.0,

            Inverter1Frequency = ToInt16(inverter1Values[
                InverterOffsets.AcFrequency]) / 100.0,

            // 40018 = INT16, 단위 0.01 kW
            Inverter1PowerKw = ToInt16(inverter1Values[
                InverterOffsets.TotalActivePower]) / 100.0,

            Inverter1DcVoltage = inverter1Values[
                InverterOffsets.DcVoltage] / 10.0,

            Inverter1DcCurrent = ToInt16(inverter1Values[
                InverterOffsets.DcCurrent]) / 100.0,

            Inverter1WorkingMode = inverter1Values[
                InverterOffsets.WorkingMode],

            Inverter1PowerOnOff = inverter1Values[
                InverterOffsets.PowerOnOff],


            // Inverter 2
            // 배열 index 0/2/4 = 41001/41003/41005 A/B/C상 전압, 0.1 V
            Inverter2PhaseAVoltage =
                ToInt16(inverter2Values[0]) / 10.0,

            Inverter2PhaseBVoltage =
                ToInt16(inverter2Values[2]) / 10.0,

            Inverter2PhaseCVoltage =
                ToInt16(inverter2Values[4]) / 10.0,

            // 41007~41009 = AB/BC/CA 선간전압, 0.1 V
            Inverter2Voltage = ToInt16(inverter2Values[
                InverterOffsets.AbLineVoltage]) / 10.0,

            Inverter2BcVoltage = ToInt16(inverter2Values[
                InverterOffsets.BcLineVoltage]) / 10.0,

            Inverter2CaVoltage = ToInt16(inverter2Values[
                InverterOffsets.CaLineVoltage]) / 10.0,

            // 41002/41004/41006 = A/B/C상 전류, 0.01 A
            Inverter2PhaseACurrent = ToInt16(inverter2Values[
                InverterOffsets.APhaseCurrent]) / 100.0,

            Inverter2PhaseBCurrent = ToInt16(inverter2Values[
                InverterOffsets.BPhaseCurrent]) / 100.0,

            Inverter2PhaseCCurrent = ToInt16(inverter2Values[
                InverterOffsets.CPhaseCurrent]) / 100.0,

            Inverter2Frequency = ToInt16(inverter2Values[
                InverterOffsets.AcFrequency]) / 100.0,

            // 41018 = INT16, 단위 0.01 kW
            Inverter2PowerKw = ToInt16(inverter2Values[
                InverterOffsets.TotalActivePower]) / 100.0,

            Inverter2DcVoltage = inverter2Values[
                InverterOffsets.DcVoltage] / 10.0,

            Inverter2DcCurrent = ToInt16(inverter2Values[
                InverterOffsets.DcCurrent]) / 100.0,

            Inverter2WorkingMode = inverter2Values[
                InverterOffsets.WorkingMode],

            Inverter2PowerOnOff = inverter2Values[
                InverterOffsets.PowerOnOff]
        };
    }
    /// <summary>
    /// 관리자 화면용 상태값 읽기.
    /// 한 장치가 응답하지 않아도 나머지 장치 Read는 계속 시도합니다.
    /// EMS 프로토콜의 Register 데이터 바이트 순서는 ModbusService에서 처리합니다.
    /// 이 서비스에서는 화면에서 사용할 논리값만 다룹니다.
    /// </summary>
    public async Task<AdminStatusSnapshot> ReadAdminStatusAsync(
        CancellationToken cancellationToken = default)
    {
        // EMS Complete ESS Information + ESS Profile Information
        // Absolute 31001 ~ 31057 / 총 57 Word
        ushort[] emsValues = await ReadAdminGroupAsync(
            groupName: "EMS",
            slaveId: SlaveIds.Ems,
            startAddress: 31001,
            numberOfPoints: 57,
            cancellationToken);

        // Battery Pack 1 : 32001 ~ 32024
        ushort[] pack1Values = await ReadAdminGroupAsync(
            groupName: "Pack 1",
            slaveId: SlaveIds.Ems,
            startAddress: 32001,
            numberOfPoints: 24,
            cancellationToken);

        // Battery Pack 2 : 33001 ~ 33024
        ushort[] pack2Values = await ReadAdminGroupAsync(
            groupName: "Pack 2",
            slaveId: SlaveIds.Ems,
            startAddress: 33001,
            numberOfPoints: 24,
            cancellationToken);

        // Inverter 1 : 40001 ~ 40033
        ushort[] inverter1Values = await ReadAdminGroupAsync(
            groupName: "Inverter 1",
            slaveId: SlaveIds.Ems,
            startAddress: 40001,
            numberOfPoints: 33,
            cancellationToken);

        // Inverter 2 : 41001 ~ 41033
        ushort[] inverter2Values = await ReadAdminGroupAsync(
            groupName: "Inverter 2",
            slaveId: SlaveIds.Ems,
            startAddress: 41001,
            numberOfPoints: 33,
            cancellationToken);

        double systemVoltage =
            GetValueOrZero(emsValues, EmsOffsets.SystemVoltage) / 100.0;

        double systemCurrent =
            ToInt16(GetValueOrZero(emsValues, EmsOffsets.SystemCurrent)) / 100.0;

        ushort systemSocRaw =
    GetValueOrZero(
        emsValues,
        EmsOffsets.SystemSoc);

        double systemSocBy10 =
            systemSocRaw / 10.0;

        double systemSocBy100 =
            systemSocRaw / 100.0;

        double systemSocBy1000 =
            systemSocRaw / 1000.0;

        double inverter1AbVoltage =
            ToInt16(GetValueOrZero(inverter1Values, InverterOffsets.AbLineVoltage)) / 10.0;

        double inverter1Frequency =
            ToInt16(GetValueOrZero(inverter1Values, InverterOffsets.AcFrequency)) / 100.0;

        double inverter1PowerKw =
            ToInt16(GetValueOrZero(inverter1Values, InverterOffsets.TotalActivePower)) / 100.0;

        double inverter2AbVoltage =
            ToInt16(GetValueOrZero(inverter2Values, InverterOffsets.AbLineVoltage)) / 10.0;

        double inverter2Frequency =
            ToInt16(GetValueOrZero(inverter2Values, InverterOffsets.AcFrequency)) / 100.0;

        double inverter2PowerKw =
            ToInt16(GetValueOrZero(inverter2Values, InverterOffsets.TotalActivePower)) / 100.0;

        WriteLog(
       $"RX EMS Admin · " +
       $"SYS={systemVoltage:0.00}V / {systemCurrent:0.00}A · " +
       $"SOC Raw={systemSocRaw} (0x{systemSocRaw:X4}) · " +
       $"/10={systemSocBy10:0.0}% · " +
       $"/100={systemSocBy100:0.00}% · " +
       $"/1000={systemSocBy1000:0.000}% · " +
       $"INV1={inverter1AbVoltage:0.0}V / " +
       $"{inverter1Frequency:0.00}Hz / " +
       $"{inverter1PowerKw:0.00}kW · " +
       $"INV2={inverter2AbVoltage:0.0}V / " +
       $"{inverter2Frequency:0.00}Hz / " +
       $"{inverter2PowerKw:0.00}kW");

        return new AdminStatusSnapshot
        {
            EmsValues = emsValues,
            Pack1Values = pack1Values,
            Pack2Values = pack2Values,
            Inverter1Values = inverter1Values,
            Inverter2Values = inverter2Values
        };
    }

    /// <summary>
    /// 관리자 제어 테이블에서 Register 1개를 FC03으로 읽습니다.
    /// ModbusService에서 EMS 프로토콜 바이트 순서를 처리한 논리값을 반환합니다.
    /// </summary>
    public async Task<ushort> ReadControlTableRegisterAsync(
        ushort absoluteAddress,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "EMS 통신이 연결되어 있지 않습니다.");
        }

        WriteLog(
            $"TX Control Table · Slave={SlaveIds.Ems} · FC=03 · " +
            $"Address={absoluteAddress} · Count=1");

        ushort[] values = await _modbusService.ReadHoldingRegistersAsync(
            SlaveIds.Ems,
            absoluteAddress,
            1,
            cancellationToken);

        if (values.Length == 0)
        {
            throw new InvalidOperationException(
                $"{absoluteAddress} 응답 Register가 없습니다.");
        }

        ushort logicalValue = values[0];

        WriteLog(
            $"RX Control Table · Address={absoluteAddress} · " +
            $"Logical=0x{logicalValue:X4} ({logicalValue})");

        return logicalValue;
    }

    /// <summary>
    /// 관리자 제어 테이블에서 Register 1개를 FC06으로 씁니다.
    /// 입력은 화면 기준 논리값입니다. 실제 바이트 순서는 ModbusService에서 처리합니다.
    /// </summary>
    public async Task WriteControlTableRegisterAsync(
        ushort absoluteAddress,
        ushort logicalValue,
        CancellationToken cancellationToken = default)
    {
        if (absoluteAddress == EmsControlAddresses.ControlWord1 ||
            absoluteAddress == EmsControlAddresses.ControlWord2)
        {
            await WriteControlEssWordAsync(
                absoluteAddress,
                logicalValue,
                cancellationToken);

            return;
        }

        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "EMS 통신이 연결되어 있지 않습니다.");
        }

        WriteLog(
            $"TX Control Table · Slave={SlaveIds.Ems} · FC=06 · " +
            $"Address={absoluteAddress} · Value=0x{logicalValue:X4}");

        await _modbusService.WriteSingleRegisterAsync(
            SlaveIds.Ems,
            absoluteAddress,
            logicalValue,
            cancellationToken);

        WriteLog(
            $"RX Control Table · FC=06 OK · Address={absoluteAddress} · Logical=0x{logicalValue:X4}");
    }

    /// <summary>
    /// 관리자 제어 테이블에서 Coil 1개를 FC05로 씁니다.
    /// </summary>
    public async Task WriteControlTableCoilAsync(
        ushort coilAddress,
        bool value,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "EMS 통신이 연결되어 있지 않습니다.");
        }

        WriteLog(
            $"TX Control Table · Slave={SlaveIds.Ems} · FC=05 · " +
            $"Coil={coilAddress} · Value={(value ? 1 : 0)}");

        await _modbusService.WriteSingleCoilAsync(
            SlaveIds.Ems,
            coilAddress,
            value,
            cancellationToken);

        WriteLog(
            $"RX Control Table · FC=05 OK · Coil={coilAddress} · Value={(value ? 1 : 0)}");
    }

    /// <summary>
    /// 관리자 ESS 제어 화면용 Control ESS 영역(30001~30012)을 읽습니다.
    /// 읽기 응답은 ModbusService에서 EMS 프로토콜 바이트 순서를 처리한 값으로 반환합니다.
    /// </summary>
    public async Task<ushort[]> ReadControlEssAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "EMS 통신이 연결되어 있지 않습니다.");
        }

        const ushort startAddress = 30001;
        const ushort numberOfPoints = 12;

        WriteLog(
            $"TX Control ESS · Slave={SlaveIds.Ems} · FC=03 · " +
            $"Start={startAddress} · Count={numberOfPoints}");

        ushort[] values = await _modbusService.ReadHoldingRegistersAsync(
            SlaveIds.Ems,
            startAddress,
            numberOfPoints,
            cancellationToken);

        string raw1 = values.Length > 0
            ? $"0x{values[0]:X4}"
            : "-";
        string raw2 = values.Length > 1
            ? $"0x{values[1]:X4}"
            : "-";

        WriteLog(
            $"RX Control ESS · {values.Length}개 수신 · " +
            $"30001={raw1} · 30002={raw2}");

        return values;
    }

    /// <summary>
    /// Control ESS의 Bit Field Word(30001 또는 30002)를 FC06으로 씁니다.
    /// 입력은 화면 기준 논리값입니다. 실제 바이트 순서는 ModbusService에서 처리합니다.
    /// </summary>
    public async Task WriteControlEssWordAsync(
        ushort absoluteAddress,
        ushort value,
        CancellationToken cancellationToken = default)
    {
        await _controlWordUpdateLock.WaitAsync(cancellationToken);

        try
        {
            await WriteControlEssWordCoreAsync(
                absoluteAddress,
                value,
                cancellationToken);
        }
        finally
        {
            _controlWordUpdateLock.Release();
        }
    }

    private async Task WriteControlEssWordCoreAsync(
        ushort absoluteAddress,
        ushort value,
        CancellationToken cancellationToken)
    {
        if (absoluteAddress != EmsControlAddresses.ControlWord1 &&
            absoluteAddress != EmsControlAddresses.ControlWord2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(absoluteAddress),
                "Control ESS Bit Field Write 주소는 30001 또는 30002만 허용됩니다.");
        }

        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "EMS 통신이 연결되어 있지 않습니다.");
        }

        WriteLog(
            $"TX Control ESS · Slave={SlaveIds.Ems} · FC=06 · " +
            $"Address={absoluteAddress} · Value=0x{value:X4}");

        await _modbusService.WriteSingleRegisterAsync(
            SlaveIds.Ems,
            absoluteAddress,
            value,
            cancellationToken);

        WriteLog(
            $"RX Control ESS · FC=06 OK · " +
            $"Address={absoluteAddress} · Logical=0x{value:X4}");
    }


    /// <summary>
    /// 현재 30001 값을 EMS에서 다시 읽은 뒤 지정한 비트만 추가합니다.
    /// 관리자 화면의 체크 상태 Write처럼 기존 비트는 그대로 유지됩니다.
    /// </summary>
    private Task<ushort> AddControlWord1BitsAsync(
        ushort bitsToAdd,
        CancellationToken cancellationToken = default)
    {
        return UpdateControlEssWordAsync(
            absoluteAddress: EmsControlAddresses.ControlWord1,
            editableMask: bitsToAdd,
            desiredBits: bitsToAdd,
            operationName: $"30001 비트 추가 0x{bitsToAdd:X4}",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 현재 30001의 Pack / Inverter 등 다른 제어 비트는 유지하고 Mode 비트만 변경합니다.
    /// 모드 변경 순간 즉시 운전되는 것을 막기 위해 기존 SystemRun 비트는 제거합니다.
    /// </summary>
    private Task<ushort> SetControlWord1ModeAsync(
        EmsOperationMode mode,
        CancellationToken cancellationToken = default)
    {
        ushort editableMask = (ushort)(
            EmsControlWord1.ModeMask |
            EmsControlWord1.SystemRun);

        return UpdateControlEssWordAsync(
            absoluteAddress: EmsControlAddresses.ControlWord1,
            editableMask: editableMask,
            desiredBits: (ushort)mode,
            operationName: $"30001 모드 변경 {mode} + 기존 Run 해제",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 30001 또는 30002의 실제값을 다시 읽고,
    /// editableMask에 포함된 비트만 desiredBits 값으로 교체합니다.
    /// 선택하지 않은 비트와 Reserved 비트는 그대로 보존됩니다.
    /// </summary>
    public async Task<ushort> UpdateControlEssWordAsync(
        ushort absoluteAddress,
        ushort editableMask,
        ushort desiredBits,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        if (absoluteAddress != EmsControlAddresses.ControlWord1 &&
            absoluteAddress != EmsControlAddresses.ControlWord2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(absoluteAddress),
                "Read-Modify-Write 주소는 30001 또는 30002만 허용됩니다.");
        }

        if (editableMask == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(editableMask),
                "변경할 비트 Mask가 0입니다.");
        }

        await _controlWordUpdateLock.WaitAsync(cancellationToken);

        try
        {
            ushort currentValue =
                await ReadControlTableRegisterAsync(
                    absoluteAddress,
                    cancellationToken);

            ushort updatedValue = (ushort)(
                (currentValue & ~editableMask) |
                (desiredBits & editableMask));

            WriteLog(
                $"Control Word Read-Modify-Write · {operationName} · " +
                $"Address={absoluteAddress} · " +
                $"Before=0x{currentValue:X4} · " +
                $"Mask=0x{editableMask:X4} · " +
                $"Desired=0x{desiredBits:X4} · " +
                $"Write=0x{updatedValue:X4}");

            if (updatedValue != currentValue)
            {
                await WriteControlEssWordCoreAsync(
                    absoluteAddress,
                    updatedValue,
                    cancellationToken);
            }
            else
            {
                WriteLog(
                    $"Control Word Write 생략 · Address={absoluteAddress} · 변경값 없음");
            }

            ushort confirmedValue = currentValue;

            for (int attempt = 1; attempt <= 5; attempt++)
            {
                if (attempt > 1)
                {
                    await Task.Delay(120, cancellationToken);
                }

                confirmedValue =
                    await ReadControlTableRegisterAsync(
                        absoluteAddress,
                        cancellationToken);

                if (confirmedValue == updatedValue)
                {
                    break;
                }
            }

            WriteLog(
                $"Control Word Readback · Address={absoluteAddress} · " +
                $"Expected=0x{updatedValue:X4} · " +
                $"Actual=0x{confirmedValue:X4}");

            return confirmedValue;
        }
        finally
        {
            _controlWordUpdateLock.Release();
        }
    }

    /// <summary>
    /// Control ESS의 제한값 영역 30003~30012를 FC06으로 하나씩 씁니다.
    /// FC16은 사용하지 않습니다.
    /// values[0]은 30003, values[9]는 30012에 대응합니다.
    /// </summary>
    public async Task WriteControlEssLimitsAsync(
        ushort[] values,
        CancellationToken cancellationToken = default)
    {
        if (values is null || values.Length != 10)
        {
            throw new ArgumentException(
                "Control ESS 제한값은 30003~30012의 10개 Register가 필요합니다.",
                nameof(values));
        }

        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "EMS 통신이 연결되어 있지 않습니다.");
        }

        WriteLog(
            "TX Control ESS Limits · FC06 순차 Write 시작 · 30003~30012");

        for (int i = 0; i < values.Length; i++)
        {
            ushort address = (ushort)(30003 + i);
            ushort value = values[i];

            WriteLog(
                $"TX Control ESS · Slave={SlaveIds.Ems} · FC=06 · " +
                $"Address={address} · Value=0x{value:X4}");

            await _modbusService.WriteSingleRegisterAsync(
                SlaveIds.Ems,
                address,
                value,
                cancellationToken);

            WriteLog(
                $"RX Control ESS · FC=06 OK · " +
                $"Address={address} · Logical=0x{value:X4}");

            await Task.Delay(20, cancellationToken);
        }

        WriteLog(
            "RX Control ESS Limits · FC06 순차 Write 완료 · 30003~30012 적용 완료");
    }


    /// <summary>
    /// 관리자 인버터 설정 화면용 SET 영역을 읽습니다.
    /// 실제 EMS는 Slave 1에서 Inverter Absolute Address를 제공하므로,
    /// Inverter 1 = 40024~40063, Inverter 2 = 41024~41063을 각각 FC03으로 읽습니다.
    /// ModbusService에서 EMS 프로토콜 바이트 순서를 처리한 논리값을 반환합니다.
    /// </summary>
    public async Task<(ushort[] Inverter1Values, ushort[] Inverter2Values)>
        ReadInverterSettingsAsync(
            CancellationToken cancellationToken = default)
    {
        const ushort numberOfPoints = 40;

        ushort[] inverter1Values = await ReadAdminGroupAsync(
            groupName: "Inverter 1 SET",
            slaveId: SlaveIds.Ems,
            startAddress: 40024,
            numberOfPoints: numberOfPoints,
            cancellationToken);

        ushort[] inverter2Values = await ReadAdminGroupAsync(
            groupName: "Inverter 2 SET",
            slaveId: SlaveIds.Ems,
            startAddress: 41024,
            numberOfPoints: numberOfPoints,
            cancellationToken);

        return (inverter1Values, inverter2Values);
    }

    /// <summary>
    /// 인버터 SET 영역의 Register 하나를 FC06으로 씁니다.
    /// targetIndex: 0=Inverter1, 1=Inverter2, 2=두 인버터 동시 적용.
    /// offset은 Inverter 주소맵 Relative Address(23~62)입니다.
    /// </summary>
    public async Task WriteInverterSettingAsync(
        int targetIndex,
        ushort offset,
        ushort logicalValue,
        CancellationToken cancellationToken = default)
    {
        if (offset is < 23 or > 62)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "Inverter SET Write Offset은 23~62만 허용됩니다.");
        }

        if (targetIndex is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetIndex),
                "인버터 대상은 0=INV1, 1=INV2, 2=INV1+INV2만 허용됩니다.");
        }

        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "EMS 통신이 연결되어 있지 않습니다.");
        }

        if (targetIndex is 0 or 2)
        {
            await WriteInverterSettingToAddressAsync(
                inverterName: "Inverter 1 SET",
                absoluteAddress: (ushort)(40001 + offset),
                logicalValue: logicalValue,
                cancellationToken: cancellationToken);
        }

        if (targetIndex is 1 or 2)
        {
            await WriteInverterSettingToAddressAsync(
                inverterName: "Inverter 2 SET",
                absoluteAddress: (ushort)(41001 + offset),
                logicalValue: logicalValue,
                cancellationToken: cancellationToken);
        }
    }

    private async Task WriteInverterSettingToAddressAsync(
        string inverterName,
        ushort absoluteAddress,
        ushort logicalValue,
        CancellationToken cancellationToken)
    {
        WriteLog(
            $"TX {inverterName} · Slave={SlaveIds.Ems} · FC=06 · " +
            $"Address={absoluteAddress} · Value=0x{logicalValue:X4}");

        await _modbusService.WriteSingleRegisterAsync(
            SlaveIds.Ems,
            absoluteAddress,
            logicalValue,
            cancellationToken);

        WriteLog(
            $"RX {inverterName} · FC=06 OK · " +
            $"Address={absoluteAddress} · Logical=0x{logicalValue:X4}");
    }
    /// <summary>
    /// 운전 화면에서 사용하는 설정 Register 1개만 FC06으로 쓰고,
    /// 같은 주소를 다시 읽어 실제 적용값을 확인합니다.
    /// 다른 Control ESS 설정값은 변경하지 않습니다.
    /// </summary>
    private async Task WriteAndConfirmOperationSettingAsync(
        ushort absoluteAddress,
        ushort logicalValue,
        string settingName,
        CancellationToken cancellationToken)
    {
        await WriteControlTableRegisterAsync(
            absoluteAddress,
            logicalValue,
            cancellationToken);

        await Task.Delay(
            120,
            cancellationToken);

        ushort confirmedValue =
            await ReadControlTableRegisterAsync(
                absoluteAddress,
                cancellationToken);

        if (confirmedValue != logicalValue)
        {
            throw new InvalidOperationException(
                $"{settingName} 설정값이 EMS에 정상 적용되지 않았습니다. " +
                $"Address={absoluteAddress}, " +
                $"Expected=0x{logicalValue:X4} ({logicalValue}), " +
                $"Actual=0x{confirmedValue:X4} ({confirmedValue})");
        }

        WriteLog(
            $"{settingName} 설정 확인 완료 · " +
            $"Address={absoluteAddress} · " +
            $"Value=0x{confirmedValue:X4} ({confirmedValue})");
    }

    /// <summary>
    /// 외부출력 운전 중 인버터 AB 선간전압과 주파수가 정상인지 확인합니다.
    /// 두 인버터 중 최소 한 대는 정상 출력이어야 하며,
    /// 전압이 발생한 인버터는 전압과 주파수가 모두 허용범위 안에 있어야 합니다.
    /// 전압이 5V 이하인 인버터는 현재 출력에 참여하지 않는 것으로 처리합니다.
    /// </summary>
    public bool IsExternalOutputElectricalStateNormal(
        EssStatusData status,
        out string detail)
    {
        if (status is null)
        {
            detail = "외부출력 상태값이 없습니다.";
            return false;
        }

        bool inverter1Energized =
            Math.Abs(status.Inverter1Voltage) >
            ExternalOutputZeroVoltageMaximumV;

        bool inverter2Energized =
            Math.Abs(status.Inverter2Voltage) >
            ExternalOutputZeroVoltageMaximumV;

        bool inverter1Normal =
            IsExternalOutputInverterElectricalStateNormal(
                status.Inverter1Voltage,
                status.Inverter1Frequency);

        bool inverter2Normal =
            IsExternalOutputInverterElectricalStateNormal(
                status.Inverter2Voltage,
                status.Inverter2Frequency);

        if (inverter1Energized &&
            !inverter1Normal)
        {
            detail =
                $"INV1 출력 이상 · " +
                $"AB={status.Inverter1Voltage:0.0}V · " +
                $"Freq={status.Inverter1Frequency:0.00}Hz";

            return false;
        }

        if (inverter2Energized &&
            !inverter2Normal)
        {
            detail =
                $"INV2 출력 이상 · " +
                $"AB={status.Inverter2Voltage:0.0}V · " +
                $"Freq={status.Inverter2Frequency:0.00}Hz";

            return false;
        }

        if (!inverter1Normal &&
            !inverter2Normal)
        {
            detail =
                "정상 외부출력 전압을 확인할 수 없습니다. " +
                $"INV1={status.Inverter1Voltage:0.0}V/" +
                $"{status.Inverter1Frequency:0.00}Hz · " +
                $"INV2={status.Inverter2Voltage:0.0}V/" +
                $"{status.Inverter2Frequency:0.00}Hz";

            return false;
        }

        detail =
            $"외부출력 정상 · " +
            $"INV1={status.Inverter1Voltage:0.0}V/" +
            $"{status.Inverter1Frequency:0.00}Hz · " +
            $"INV2={status.Inverter2Voltage:0.0}V/" +
            $"{status.Inverter2Frequency:0.00}Hz";

        return true;
    }

    private static bool IsExternalOutputInverterElectricalStateNormal(
        double voltage,
        double frequency)
    {
        return
            !double.IsNaN(voltage) &&
            !double.IsInfinity(voltage) &&
            !double.IsNaN(frequency) &&
            !double.IsInfinity(frequency) &&
            voltage >= ExternalOutputNormalVoltageMinimumV &&
            voltage <= ExternalOutputNormalVoltageMaximumV &&
            frequency >= ExternalOutputNormalFrequencyMinimumHz &&
            frequency <= ExternalOutputNormalFrequencyMaximumHz;
    }

    /// <summary>
    /// ExternalOutput 모드만 적용되고 SystemRun은 OFF인 상태에서
    /// 인버터 1/2 AB 선간전압이 5V 이하인지 3회 연속 확인합니다.
    /// 조건을 만족하지 않으면 SystemRun을 전송하지 않습니다.
    /// </summary>
    private async Task ConfirmExternalOutputVoltageIsZeroBeforeRunAsync(
        CancellationToken cancellationToken)
    {
        const int maxRetryCount = 20;
        const int retryDelayMilliseconds = 250;

        int consecutiveZeroCount = 0;
        EssStatusData? lastStatus = null;

        for (int retry = 1;
             retry <= maxRetryCount;
             retry++)
        {
            await Task.Delay(
                retryDelayMilliseconds,
                cancellationToken);

            EssStatusData status =
                await ReadStatusCoreAsync(
                    cancellationToken);

            lastStatus = status;

            EmsOperationMode currentMode =
                EmsSystemStatus1.GetOperatingMode(
                    status.SystemStatus1);

            bool isRunning =
                EmsSystemStatus2.IsRunning(
                    status.SystemStatus2);

            bool inverter1Zero =
                Math.Abs(status.Inverter1Voltage) <=
                ExternalOutputZeroVoltageMaximumV;

            bool inverter2Zero =
                Math.Abs(status.Inverter2Voltage) <=
                ExternalOutputZeroVoltageMaximumV;

            bool zeroVoltageConfirmed =
                currentMode == EmsOperationMode.ExternalOutput &&
                !isRunning &&
                inverter1Zero &&
                inverter2Zero;

            if (isRunning)
            {
                throw new InvalidOperationException(
                    "외부출력 SystemRun 전 확인 중 EMS가 이미 Run 상태입니다. " +
                    $"Mode={currentMode}, " +
                    $"INV1_AB={status.Inverter1Voltage:0.0}V, " +
                    $"INV2_AB={status.Inverter2Voltage:0.0}V");
            }

            if (zeroVoltageConfirmed)
            {
                consecutiveZeroCount++;
            }
            else
            {
                consecutiveZeroCount = 0;
            }

            WriteLog(
                $"외부출력 Run 전 0V 확인 " +
                $"{retry}/{maxRetryCount} · " +
                $"Mode={currentMode} · " +
                $"Run={(isRunning ? "Run" : "Stop")} · " +
                $"INV1_AB={status.Inverter1Voltage:0.0}V · " +
                $"INV2_AB={status.Inverter2Voltage:0.0}V · " +
                $"연속정상={consecutiveZeroCount}/" +
                $"{ExternalOutputRequiredConsecutiveChecks}");

            if (consecutiveZeroCount >=
                ExternalOutputRequiredConsecutiveChecks)
            {
                WriteLog(
                    "외부출력 Run 전 0V 확인 완료 · " +
                    $"INV1_AB={status.Inverter1Voltage:0.0}V · " +
                    $"INV2_AB={status.Inverter2Voltage:0.0}V");

                return;
            }
        }

        throw new InvalidOperationException(
     "외부출력 시작 전 인버터 선간전압이 감지되었습니다.\n" +
     "안전을 위해 외부출력을 시작하지 않습니다.");
    }

    /// <summary>
    /// 외부출력 시작 중 오류가 발생하면 현재 시퀀스 안에서
    /// SystemRun을 OFF하고 Standby로 복귀시킵니다.
    /// StopAllAsync를 호출하면 같은 시퀀스 Lock을 다시 기다리므로
    /// 여기서는 30001을 직접 Read-Modify-Write 합니다.
    /// </summary>
    private async Task TryReturnExternalOutputToSafeStandbyAsync(
        string reason)
    {
        try
        {
            ushort safeControlWord =
                await UpdateControlEssWordAsync(
                    absoluteAddress:
                        EmsControlAddresses.ControlWord1,
                    editableMask: (ushort)(
                        EmsControlWord1.ModeMask |
                        EmsControlWord1.SystemRun),
                    desiredBits:
                        (ushort)EmsOperationMode.Standby,
                    operationName:
                        $"외부출력 시작 실패 안전복귀 · {reason}",
                    cancellationToken:
                        CancellationToken.None);

            WriteLog(
                $"외부출력 안전복귀 완료 · " +
                $"Standby + SystemRun OFF · " +
                $"30001=0x{safeControlWord:X4}");
        }
        catch (Exception safeStopException)
        {
            WriteLog(
                "외부출력 안전복귀 실패 · " +
                $"{safeStopException.GetType().Name}: " +
                $"{safeStopException.Message}");

            FileAppLogger.Error(
                "OPERATION",
                "외부출력 시작 실패 후 Standby 안전복귀 실패",
                safeStopException);
        }
    }

    public async Task StartAutoChargeAsync(
        double targetSoc,
        double chargeCurrentA,
        CancellationToken cancellationToken = default)
    {
        bool operationSequenceStarted = false;

        try
        {
            await BeginOperationSequenceAsync(
                "자동충전",
                cancellationToken);

            operationSequenceStarted = true;

            FileAppLogger.Info(
                "OPERATION",
                $"자동충전 시작 요청 | " +
                $"TargetSOC={targetSoc:0.0}% | " +
                $"ChargeCurrent={chargeCurrentA:0.00}A");

            try
            {
                if (double.IsNaN(targetSoc) ||
                    double.IsInfinity(targetSoc) ||
                    targetSoc < 10 ||
                    targetSoc > 100)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(targetSoc),
                        "목표 SOC는 10 ~ 100% 범위여야 합니다.");
                }

                if (double.IsNaN(chargeCurrentA) ||
                    double.IsInfinity(chargeCurrentA) ||
                    chargeCurrentA < 0 ||
                    chargeCurrentA > 120.0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(chargeCurrentA),
                        "충전 전류는 0 ~ 120 A 범위여야 합니다.");
                }

                await CheckCanStartOperationAsync(
                    requiredMode: EmsOperationMode.AutoCharge,
                    modeName: "충전",
                    cancellationToken);

                const int commandDelayMilliseconds = 1000;

                // 1단계: 30001에서 Mode만 AutoCharge로 변경하고
                // SystemRun은 OFF로 둡니다.
                // Pack / Inverter / Reserved 비트는 현재값 그대로 유지합니다.
                ushort appliedCommand =
                    await SetControlWord1ModeAsync(
                        EmsOperationMode.AutoCharge,
                        cancellationToken);

                if ((appliedCommand & EmsControlWord1.ModeMask) !=
                        (ushort)EmsOperationMode.AutoCharge ||
                    (appliedCommand & EmsControlWord1.SystemRun) != 0)
                {
                    throw new InvalidOperationException(
                        "자동충전 모드 설정값이 30001에 정상 적용되지 않았습니다. " +
                        $"30001=0x{appliedCommand:X4}");
                }

                WriteLog(
                    $"자동충전 시퀀스 1/4 · " +
                    $"Mode=AutoCharge · SystemRun OFF · " +
                    $"다른 30001 비트 유지 · " +
                    $"30001=0x{appliedCommand:X4}");

                await Task.Delay(
                    commandDelayMilliseconds,
                    cancellationToken);

                // 2단계: 사용자가 설정한 목표 SOC만 30008에 씁니다.
                ushort targetSocRaw =
                    (ushort)Math.Round(
                        targetSoc * 10.0);

                await WriteAndConfirmOperationSettingAsync(
                    EmsControlAddresses.TargetChargeSoc,
                    targetSocRaw,
                    "자동충전 목표 SOC",
                    cancellationToken);

                WriteLog(
                    $"자동충전 시퀀스 2/4 · " +
                    $"30008 목표 SOC={targetSocRaw} " +
                    $"({targetSoc:0.0}%)");

                await Task.Delay(
                    commandDelayMilliseconds,
                    cancellationToken);

                // 3단계: 사용자가 설정한 충전 전류를 30004에 씁니다.
                ushort chargeCurrentRaw =
                    (ushort)Math.Round(
                        chargeCurrentA * 100.0);

                await WriteAndConfirmOperationSettingAsync(
                    EmsControlAddresses.ChargingMaxLimitCurrentPerPack,
                    chargeCurrentRaw,
                    "자동충전 충전 전류",
                    cancellationToken);

                WriteLog(
                    $"자동충전 시퀀스 3/4 · " +
                    $"30004 충전전류={chargeCurrentRaw} " +
                    $"({chargeCurrentA:0.00}A)");

                await Task.Delay(
                    commandDelayMilliseconds,
                    cancellationToken);

                // 4단계: 현재 30001을 다시 읽고 SystemRun 비트만 ON 합니다.
                appliedCommand =
                    await AddControlWord1BitsAsync(
                        EmsControlWord1.SystemRun,
                        cancellationToken);

                if ((appliedCommand & EmsControlWord1.ModeMask) !=
                        (ushort)EmsOperationMode.AutoCharge ||
                    (appliedCommand & EmsControlWord1.SystemRun) == 0)
                {
                    throw new InvalidOperationException(
                        "자동충전 SystemRun 명령이 30001에 정상 적용되지 않았습니다. " +
                        $"30001=0x{appliedCommand:X4}");
                }

                WriteLog(
                    $"자동충전 시퀀스 4/4 · " +
                    $"SystemRun ON · EMS 내부 시퀀스 시작 요청 · " +
                    $"30001=0x{appliedCommand:X4}");

                const int maxRetryCount = 25;
                const int retryDelayMilliseconds = 200;

                ushort lastSystemStatus1 = 0;
                ushort lastSystemStatus2 = 0;
                bool started = false;

                for (int retry = 1; retry <= maxRetryCount; retry++)
                {
                    await Task.Delay(
                        retryDelayMilliseconds,
                        cancellationToken);

                    EssStatusData status =
                        await ReadStatusCoreAsync(
                            cancellationToken);

                    lastSystemStatus1 = status.SystemStatus1;
                    lastSystemStatus2 = status.SystemStatus2;

                    EmsOperationMode currentMode =
                        EmsSystemStatus1.GetOperatingMode(
                            status.SystemStatus1);

                    bool isRunning =
                        EmsSystemStatus2.IsRunning(
                            status.SystemStatus2);

                    WriteLog(
                        $"자동충전 상태 확인 {retry}/{maxRetryCount} · " +
                        $"Mode={currentMode} · " +
                        $"Run={(isRunning ? "Run" : "Stop")} · " +
                        $"31022=0x{status.SystemStatus1:X4} · " +
                        $"31023=0x{status.SystemStatus2:X4}");

                    if (currentMode == EmsOperationMode.AutoCharge &&
                        isRunning)
                    {
                        started = true;
                        break;
                    }
                }

                if (!started)
                {
                    EmsOperationMode lastMode =
                        EmsSystemStatus1.GetOperatingMode(
                            lastSystemStatus1);

                    bool lastIsRunning =
                        EmsSystemStatus2.IsRunning(
                            lastSystemStatus2);

                    throw new InvalidOperationException(
                        "자동충전 모드, 설정값, SystemRun 명령은 전송됐지만 " +
                        "EMS가 제한시간 안에 AutoCharge/Run 상태로 " +
                        "전환되지 않았습니다. " +
                        $"현재모드={lastMode}, " +
                        $"RunStop={(lastIsRunning ? "Run" : "Stop")}, " +
                        $"31022=0x{lastSystemStatus1:X4}, " +
                        $"31023=0x{lastSystemStatus2:X4}");
                }

                WriteLog(
                    $"자동충전 시작 완료 · " +
                    $"목표SOC={targetSoc:0.0}% · " +
                    $"충전전류={chargeCurrentA:0.00}A · " +
                    $"최종명령=0x{appliedCommand:X4}");

                FileAppLogger.Info(
                    "OPERATION",
                    $"자동충전 시작 완료 | " +
                    $"TargetSOC={targetSoc:0.0}% | " +
                    $"ChargeCurrent={chargeCurrentA:0.00}A | " +
                    $"FinalCommand=0x{appliedCommand:X4}");
            }
            catch (OperationCanceledException)
            {
                FileAppLogger.Warning(
                    "OPERATION",
                    $"자동충전 시작 취소 | " +
                    $"ChargeCurrent={chargeCurrentA:0.00}A");

                throw;
            }
            catch (Exception ex)
            {
                FileAppLogger.Error(
                    "OPERATION",
                    $"자동충전 시작 실패 | " +
                    $"ChargeCurrent={chargeCurrentA:0.00}A",
                    ex);

                throw;
            }
        }
        finally
        {
            if (operationSequenceStarted)
            {
                EndOperationSequence("자동충전");
            }
        }
    }

    public async Task StartExternalOutputAsync(
        double totalOutputPowerKw,
        double minimumSoc,
        double phaseVoltage = 220,
        double frequencyHz = 60,
        CancellationToken cancellationToken = default)
    {
        bool operationSequenceStarted = false;
        bool externalOutputModeApplied = false;

        try
        {
            await BeginOperationSequenceAsync(
                "외부 전원 출력",
                cancellationToken);

            operationSequenceStarted = true;

            FileAppLogger.Info(
                "OPERATION",
                $"외부 전원 출력 시작 요청 | " +
                $"TargetOutputPower={totalOutputPowerKw:0.00}kW | " +
                $"MinimumSOC={minimumSoc:0.0}%");

            try
            {
                if (double.IsNaN(totalOutputPowerKw) ||
                    double.IsInfinity(totalOutputPowerKw) ||
                    totalOutputPowerKw < 1 ||
                    totalOutputPowerKw > 327.67)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(totalOutputPowerKw),
                        "외부 출력 제한전력은 1.00 ~ 327.67 kW 범위여야 합니다.");
                }

                if (double.IsNaN(minimumSoc) ||
                    double.IsInfinity(minimumSoc) ||
                    minimumSoc < 10 ||
                    minimumSoc > 90)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(minimumSoc),
                        "최저 SOC는 10 ~ 90% 범위여야 합니다.");
                }

                if (double.IsNaN(phaseVoltage) ||
                    double.IsInfinity(phaseVoltage) ||
                    phaseVoltage < 200 ||
                    phaseVoltage > 240)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(phaseVoltage),
                        "외부 출력 상전압은 200 ~ 240 V 범위여야 합니다.");
                }

                if (double.IsNaN(frequencyHz) ||
                    double.IsInfinity(frequencyHz) ||
                    frequencyHz < 45 ||
                    frequencyHz > 65)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(frequencyHz),
                        "외부 출력 주파수는 45 ~ 65 Hz 범위여야 합니다.");
                }

                await CheckCanStartOperationAsync(
                    requiredMode:
                        EmsOperationMode.ExternalOutput,
                    modeName:
                        "외부 전원 출력",
                    cancellationToken);

                const int commandDelayMilliseconds = 1000;

                // 1단계: ExternalOutput 모드 + SystemRun OFF
                // 다른 30001 비트는 현재값 그대로 유지합니다.
                ushort appliedCommand =
                    await SetControlWord1ModeAsync(
                        EmsOperationMode.ExternalOutput,
                        cancellationToken);

                if ((appliedCommand &
                        EmsControlWord1.ModeMask) !=
                        (ushort)EmsOperationMode.ExternalOutput ||
                    (appliedCommand &
                        EmsControlWord1.SystemRun) != 0)
                {
                    throw new InvalidOperationException(
                        "외부출력 모드 설정값이 30001에 정상 적용되지 않았습니다. " +
                        $"30001=0x{appliedCommand:X4}");
                }

                externalOutputModeApplied = true;

                WriteLog(
                    $"외부출력 시퀀스 1/7 · " +
                    $"Mode=ExternalOutput · SystemRun OFF · " +
                    $"다른 30001 비트 유지 · " +
                    $"30001=0x{appliedCommand:X4}");

                // 2단계: Mode=ExternalOutput / Run=OFF 상태에서
                // 인버터 1/2 AB 선간전압이 5V 이하인지 3회 연속 확인합니다.
                // 조건을 만족하지 않으면 설정값 및 SystemRun 단계로 진행하지 않습니다.
                await ConfirmExternalOutputVoltageIsZeroBeforeRunAsync(
                    cancellationToken);

                WriteLog(
                    $"외부출력 시퀀스 2/7 · " +
                    $"Run 전 선간전압 0V 확인 완료 · " +
                    $"기준≤{ExternalOutputZeroVoltageMaximumV:0.0}V");

                // 3단계: 사용자가 설정한 최저 SOC만 30009에 씁니다.
                ushort minimumSocRaw =
                    (ushort)Math.Round(
                        minimumSoc * 10.0);

                await WriteAndConfirmOperationSettingAsync(
                    EmsControlAddresses.TargetDischargeSoc,
                    minimumSocRaw,
                    "외부출력 최저 SOC",
                    cancellationToken);

                WriteLog(
                    $"외부출력 시퀀스 3/7 · " +
                    $"30009 최저 SOC={minimumSocRaw} " +
                    $"({minimumSoc:0.0}%)");

                await Task.Delay(
                    commandDelayMilliseconds,
                    cancellationToken);

                // 4단계: 사용자가 설정한 Off-Grid 출력전력만 30012에 씁니다.
                ushort outputPowerRaw =
                    (ushort)Math.Round(
                        totalOutputPowerKw * 100.0);

                await WriteAndConfirmOperationSettingAsync(
                    EmsControlAddresses.AcMaxDischargePowerOffGrid,
                    outputPowerRaw,
                    "외부출력 최대 전력",
                    cancellationToken);

                WriteLog(
                    $"외부출력 시퀀스 4/7 · " +
                    $"30012 출력전력={outputPowerRaw} " +
                    $"({totalOutputPowerKw:0.00}kW)");

                await Task.Delay(
                    commandDelayMilliseconds,
                    cancellationToken);

                // 5단계: 사용자가 설정한 출력 상전압을 30014에 씁니다.
                ushort phaseVoltageRaw =
                    (ushort)Math.Round(
                        phaseVoltage * 100.0);

                await WriteAndConfirmOperationSettingAsync(
                    EmsControlAddresses.AcOutputPhaseVoltage,
                    phaseVoltageRaw,
                    "외부출력 상전압",
                    cancellationToken);

                WriteLog(
                    $"외부출력 시퀀스 5/7 · " +
                    $"30014 상전압={phaseVoltageRaw} " +
                    $"({phaseVoltage:0.0}V)");

                await Task.Delay(
                    commandDelayMilliseconds,
                    cancellationToken);

                // 6단계: 사용자가 설정한 출력 주파수를 30016에 씁니다.
                ushort frequencyRaw =
                    (ushort)Math.Round(
                        frequencyHz * 100.0);

                await WriteAndConfirmOperationSettingAsync(
                    EmsControlAddresses.AcOutputFrequency,
                    frequencyRaw,
                    "외부출력 주파수",
                    cancellationToken);

                WriteLog(
                    $"외부출력 시퀀스 6/7 · " +
                    $"30016 주파수={frequencyRaw} " +
                    $"({frequencyHz:0.00}Hz)");

                await Task.Delay(
                    commandDelayMilliseconds,
                    cancellationToken);

                // Run 직전에도 전압이 계속 0V 수준인지 한 번 더 확인합니다.
                EssStatusData beforeRunStatus =
                    await ReadStatusCoreAsync(
                        cancellationToken);

                if (Math.Abs(beforeRunStatus.Inverter1Voltage) >
                        ExternalOutputZeroVoltageMaximumV ||
                    Math.Abs(beforeRunStatus.Inverter2Voltage) >
                        ExternalOutputZeroVoltageMaximumV ||
                    EmsSystemStatus2.IsRunning(
                        beforeRunStatus.SystemStatus2))
                {
                    throw new InvalidOperationException(
                        "외부출력 SystemRun 직전 선간전압 또는 Run 상태가 비정상입니다. " +
                        $"INV1_AB={beforeRunStatus.Inverter1Voltage:0.0}V, " +
                        $"INV2_AB={beforeRunStatus.Inverter2Voltage:0.0}V, " +
                        $"31023=0x{beforeRunStatus.SystemStatus2:X4}");
                }

                // 7단계: SystemRun만 ON
                appliedCommand =
                    await AddControlWord1BitsAsync(
                        EmsControlWord1.SystemRun,
                        cancellationToken);

                if ((appliedCommand &
                        EmsControlWord1.ModeMask) !=
                        (ushort)EmsOperationMode.ExternalOutput ||
                    (appliedCommand &
                        EmsControlWord1.SystemRun) == 0)
                {
                    throw new InvalidOperationException(
                        "외부출력 SystemRun 명령이 30001에 정상 적용되지 않았습니다. " +
                        $"30001=0x{appliedCommand:X4}");
                }

                WriteLog(
                    $"외부출력 시퀀스 7/7 · " +
                    $"SystemRun ON · EMS 내부 시퀀스 시작 요청 · " +
                    $"30001=0x{appliedCommand:X4}");

                // 최대 10초 동안 Mode/Run과 실제 전압·주파수를 함께 확인합니다.
                // 200ms × 50회 = 약 10초
                const int maxRetryCount = 50;
                const int retryDelayMilliseconds = 200;

                ushort lastSystemStatus1 = 0;
                ushort lastSystemStatus2 = 0;
                EssStatusData? lastStatus = null;

                int consecutiveNormalCount = 0;
                bool started = false;

                for (int retry = 1;
                     retry <= maxRetryCount;
                     retry++)
                {
                    await Task.Delay(
                        retryDelayMilliseconds,
                        cancellationToken);

                    EssStatusData status =
                        await ReadStatusCoreAsync(
                            cancellationToken);

                    lastStatus = status;
                    lastSystemStatus1 = status.SystemStatus1;
                    lastSystemStatus2 = status.SystemStatus2;

                    EmsOperationMode currentMode =
                        EmsSystemStatus1.GetOperatingMode(
                            status.SystemStatus1);

                    bool isRunning =
                        EmsSystemStatus2.IsRunning(
                            status.SystemStatus2);

                    ushort systemFaultLevel =
                        EmsSystemStatus1.GetSystemFaultLevel(
                            status.SystemStatus1);

                    bool hasFault =
                        systemFaultLevel != 0 ||
                        EmsSystemAlarms1.HasPackOrInverterFault(
                            status.AlarmStatus1) ||
                        EmsSystemAlarms1.HasCanFault(
                            status.AlarmStatus1);

                    bool electricalNormal =
                        IsExternalOutputElectricalStateNormal(
                            status,
                            out string electricalDetail);

                    if (currentMode ==
                            EmsOperationMode.ExternalOutput &&
                        isRunning &&
                        !hasFault &&
                        electricalNormal)
                    {
                        consecutiveNormalCount++;
                    }
                    else
                    {
                        consecutiveNormalCount = 0;
                    }

                    WriteLog(
                        $"외부출력 상태 확인 {retry}/{maxRetryCount} · " +
                        $"Mode={currentMode} · " +
                        $"Run={(isRunning ? "Run" : "Stop")} · " +
                        $"Fault={(hasFault ? "YES" : "NO")} · " +
                        $"INV1={status.Inverter1Voltage:0.0}V/" +
                        $"{status.Inverter1Frequency:0.00}Hz · " +
                        $"INV2={status.Inverter2Voltage:0.0}V/" +
                        $"{status.Inverter2Frequency:0.00}Hz · " +
                        $"전기상태={(electricalNormal ? "정상" : electricalDetail)} · " +
                        $"연속정상={consecutiveNormalCount}/" +
                        $"{ExternalOutputRequiredConsecutiveChecks}");

                    if (hasFault)
                    {
                        throw new InvalidOperationException(
                            "외부출력 시작 확인 중 Fault가 감지되었습니다. " +
                            $"SystemFault={systemFaultLevel}, " +
                            $"Alarm1=0x{status.AlarmStatus1:X4}");
                    }

                    if (consecutiveNormalCount >=
                        ExternalOutputRequiredConsecutiveChecks)
                    {
                        started = true;
                        break;
                    }
                }

                if (!started)
                {
                    EmsOperationMode lastMode =
                        EmsSystemStatus1.GetOperatingMode(
                            lastSystemStatus1);

                    bool lastIsRunning =
                        EmsSystemStatus2.IsRunning(
                            lastSystemStatus2);

                    string electricalDetail =
                        "상태값 없음";

                    if (lastStatus is not null)
                    {
                        IsExternalOutputElectricalStateNormal(
                            lastStatus,
                            out electricalDetail);
                    }

                    throw new InvalidOperationException(
                        "외부출력 SystemRun 명령은 전송됐지만 " +
                        "EMS가 제한시간 안에 정상 출력 상태로 전환되지 않았습니다. " +
                        $"현재모드={lastMode}, " +
                        $"RunStop={(lastIsRunning ? "Run" : "Stop")}, " +
                        $"{electricalDetail}, " +
                        $"31022=0x{lastSystemStatus1:X4}, " +
                        $"31023=0x{lastSystemStatus2:X4}");
                }

                WriteLog(
                    $"외부 전원 출력 시작 완료 · " +
                    $"최저SOC={minimumSoc:0.0}% · " +
                    $"제한={totalOutputPowerKw:0.00}kW · " +
                    $"INV1={(lastStatus?.Inverter1Voltage ?? 0):0.0}V/" +
                    $"{(lastStatus?.Inverter1Frequency ?? 0):0.00}Hz · " +
                    $"INV2={(lastStatus?.Inverter2Voltage ?? 0):0.0}V/" +
                    $"{(lastStatus?.Inverter2Frequency ?? 0):0.00}Hz · " +
                    $"최종명령=0x{appliedCommand:X4}");

                FileAppLogger.Info(
                    "OPERATION",
                    $"외부 전원 출력 시작 완료 | " +
                    $"MinimumSOC={minimumSoc:0.0}% | " +
                    $"TargetOutputPower={totalOutputPowerKw:0.00}kW | " +
                    $"INV1={(lastStatus?.Inverter1Voltage ?? 0):0.0}V/" +
                    $"{(lastStatus?.Inverter1Frequency ?? 0):0.00}Hz | " +
                    $"INV2={(lastStatus?.Inverter2Voltage ?? 0):0.0}V/" +
                    $"{(lastStatus?.Inverter2Frequency ?? 0):0.00}Hz | " +
                    $"FinalCommand=0x{appliedCommand:X4}");
            }
            catch (OperationCanceledException)
            {
                if (externalOutputModeApplied)
                {
                    await TryReturnExternalOutputToSafeStandbyAsync(
                        "시작 작업 취소");
                }

                FileAppLogger.Warning(
                    "OPERATION",
                    "외부 전원 출력 시작 취소");

                throw;
            }
            catch (Exception ex)
            {
                if (externalOutputModeApplied)
                {
                    await TryReturnExternalOutputToSafeStandbyAsync(
                        ex.Message);
                }

                FileAppLogger.Error(
                    "OPERATION",
                    $"외부 전원 출력 시작 실패 | " +
                    $"TargetOutputPower={totalOutputPowerKw:0.00}kW | " +
                    $"MinimumSOC={minimumSoc:0.0}%",
                    ex);

                throw;
            }
        }
        finally
        {
            if (operationSequenceStarted)
            {
                EndOperationSequence("외부 전원 출력");
            }
        }
    }

    public async Task StartGridDischargeAsync(
        double totalDischargePowerKw,
        double minimumSoc,
        CancellationToken cancellationToken = default)
    {
        bool operationSequenceStarted = false;

        try
        {
            await BeginOperationSequenceAsync(
                "계통방전",
                cancellationToken);

            operationSequenceStarted = true;

            FileAppLogger.Info(
                "OPERATION",
                $"계통방전 시작 요청 | " +
                $"TargetDischargePower={totalDischargePowerKw:0.00}kW | " +
                $"MinimumSOC={minimumSoc:0.0}%");

            try
            {
                if (double.IsNaN(totalDischargePowerKw) ||
                    double.IsInfinity(totalDischargePowerKw) ||
                    totalDischargePowerKw < 1 ||
                    totalDischargePowerKw > 327.67)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(totalDischargePowerKw),
                        "계통 방전 제한전력은 1.00 ~ 327.67 kW 범위여야 합니다.");
                }

                if (double.IsNaN(minimumSoc) ||
                    double.IsInfinity(minimumSoc) ||
                    minimumSoc < 10 ||
                    minimumSoc > 90)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(minimumSoc),
                        "최저 SOC는 10 ~ 90% 범위여야 합니다.");
                }

                await CheckCanStartOperationAsync(
                    requiredMode: EmsOperationMode.GridDischarge,
                    modeName: "계통 방전",
                    cancellationToken);

                // 시작 버튼을 누른 시점의 최신 SOC를 서비스에서도 다시 확인합니다.
                EssStatusData latestStatus =
                    await ReadStatusCoreAsync(
                        cancellationToken);

                if (latestStatus.Soc <= minimumSoc)
                {
                    throw new InvalidOperationException(
                        $"현재 SOC {latestStatus.Soc:0.0}%가 " +
                        $"최저 SOC {minimumSoc:0.0}% 이하이므로 " +
                        "계통 방전을 시작할 수 없습니다.");
                }

                WriteLog(
                    $"계통방전 시작 전 SOC 확인 완료 · " +
                    $"현재SOC={latestStatus.Soc:0.0}% · " +
                    $"최저SOC={minimumSoc:0.0}%");

                const int commandDelayMilliseconds = 1000;

                // 1단계: GridDischarge 모드 + SystemRun OFF
                // 다른 30001 비트는 현재값 그대로 유지합니다.
                ushort appliedCommand =
                    await SetControlWord1ModeAsync(
                        EmsOperationMode.GridDischarge,
                        cancellationToken);

                if ((appliedCommand & EmsControlWord1.ModeMask) !=
                        (ushort)EmsOperationMode.GridDischarge ||
                    (appliedCommand & EmsControlWord1.SystemRun) != 0)
                {
                    throw new InvalidOperationException(
                        "계통방전 모드 설정값이 30001에 정상 적용되지 않았습니다. " +
                        $"30001=0x{appliedCommand:X4}");
                }

                WriteLog(
                    $"계통방전 시퀀스 1/4 · " +
                    $"Mode=GridDischarge · SystemRun OFF · " +
                    $"다른 30001 비트 유지 · " +
                    $"30001=0x{appliedCommand:X4}");

                await Task.Delay(
                    commandDelayMilliseconds,
                    cancellationToken);

                // 2단계: 사용자가 설정한 최저 SOC만 30009에 씁니다.
                ushort minimumSocRaw =
                    (ushort)Math.Round(
                        minimumSoc * 10.0);

                await WriteAndConfirmOperationSettingAsync(
                    EmsControlAddresses.TargetDischargeSoc,
                    minimumSocRaw,
                    "계통방전 최저 SOC",
                    cancellationToken);

                WriteLog(
                    $"계통방전 시퀀스 2/4 · " +
                    $"30009 최저 SOC={minimumSocRaw} " +
                    $"({minimumSoc:0.0}%)");

                await Task.Delay(
                    commandDelayMilliseconds,
                    cancellationToken);

                // 3단계: 사용자가 설정한 계통 방전전력만 30011에 씁니다.
                ushort dischargePowerRaw =
                    (ushort)Math.Round(
                        totalDischargePowerKw * 100.0);

                await WriteAndConfirmOperationSettingAsync(
                    EmsControlAddresses.AcMaxDischargePowerOnGrid,
                    dischargePowerRaw,
                    "계통방전 최대 전력",
                    cancellationToken);

                WriteLog(
                    $"계통방전 시퀀스 3/4 · " +
                    $"30011 방전전력={dischargePowerRaw} " +
                    $"({totalDischargePowerKw:0.00}kW)");

                await Task.Delay(
                    commandDelayMilliseconds,
                    cancellationToken);

                // 4단계: SystemRun만 ON
                appliedCommand =
                    await AddControlWord1BitsAsync(
                        EmsControlWord1.SystemRun,
                        cancellationToken);

                if ((appliedCommand & EmsControlWord1.ModeMask) !=
                        (ushort)EmsOperationMode.GridDischarge ||
                    (appliedCommand & EmsControlWord1.SystemRun) == 0)
                {
                    throw new InvalidOperationException(
                        "계통방전 SystemRun 명령이 30001에 정상 적용되지 않았습니다. " +
                        $"30001=0x{appliedCommand:X4}");
                }

                WriteLog(
                    $"계통방전 시퀀스 4/4 · " +
                    $"SystemRun ON · EMS 내부 시퀀스 시작 요청 · " +
                    $"30001=0x{appliedCommand:X4}");

                const int maxRetryCount = 25;
                const int retryDelayMilliseconds = 200;

                ushort lastSystemStatus1 = 0;
                ushort lastSystemStatus2 = 0;
                bool started = false;

                for (int retry = 1; retry <= maxRetryCount; retry++)
                {
                    await Task.Delay(
                        retryDelayMilliseconds,
                        cancellationToken);

                    EssStatusData status =
                        await ReadStatusCoreAsync(
                            cancellationToken);

                    lastSystemStatus1 = status.SystemStatus1;
                    lastSystemStatus2 = status.SystemStatus2;

                    EmsOperationMode currentMode =
                        EmsSystemStatus1.GetOperatingMode(
                            status.SystemStatus1);

                    bool isRunning =
                        EmsSystemStatus2.IsRunning(
                            status.SystemStatus2);

                    WriteLog(
                        $"계통방전 상태 확인 {retry}/{maxRetryCount} · " +
                        $"Mode={currentMode} · " +
                        $"Run={(isRunning ? "Run" : "Stop")} · " +
                        $"31022=0x{status.SystemStatus1:X4} · " +
                        $"31023=0x{status.SystemStatus2:X4}");

                    if (currentMode == EmsOperationMode.GridDischarge &&
                        isRunning)
                    {
                        started = true;
                        break;
                    }
                }

                if (!started)
                {
                    EmsOperationMode lastMode =
                        EmsSystemStatus1.GetOperatingMode(
                            lastSystemStatus1);

                    bool lastIsRunning =
                        EmsSystemStatus2.IsRunning(
                            lastSystemStatus2);

                    throw new InvalidOperationException(
                        "계통방전 모드, 설정값, SystemRun 명령은 전송됐지만 " +
                        "EMS가 제한시간 안에 GridDischarge/Run 상태로 " +
                        "전환되지 않았습니다. " +
                        $"현재모드={lastMode}, " +
                        $"RunStop={(lastIsRunning ? "Run" : "Stop")}, " +
                        $"31022=0x{lastSystemStatus1:X4}, " +
                        $"31023=0x{lastSystemStatus2:X4}");
                }

                WriteLog(
                    $"계통방전 시작 완료 · " +
                    $"최저SOC={minimumSoc:0.0}% · " +
                    $"제한={totalDischargePowerKw:0.00}kW · " +
                    $"최종명령=0x{appliedCommand:X4}");

                FileAppLogger.Info(
                    "OPERATION",
                    $"계통방전 시작 완료 | " +
                    $"MinimumSOC={minimumSoc:0.0}% | " +
                    $"TargetDischargePower={totalDischargePowerKw:0.00}kW | " +
                    $"FinalCommand=0x{appliedCommand:X4}");
            }
            catch (OperationCanceledException)
            {
                FileAppLogger.Warning(
                    "OPERATION",
                    "계통방전 시작 취소");

                throw;
            }
            catch (Exception ex)
            {
                FileAppLogger.Error(
                    "OPERATION",
                    $"계통방전 시작 실패 | " +
                    $"TargetDischargePower={totalDischargePowerKw:0.00}kW | " +
                    $"MinimumSOC={minimumSoc:0.0}%",
                    ex);

                throw;
            }
        }
        finally
        {
            if (operationSequenceStarted)
            {
                EndOperationSequence("계통방전");
            }
        }
    }



    public async Task StopAllAsync(
        CancellationToken cancellationToken = default)
    {
        bool operationSequenceStarted = false;

        try
        {
            await BeginOperationSequenceAsync(
                "운전 정지",
                cancellationToken);

            operationSequenceStarted = true;

            FileAppLogger.Info(
                "OPERATION",
                "운전 정지 요청 | OperatingMode=Standby | SystemRun B12=0");

            try
            {
                if (!IsConnected)
                {
                    throw new InvalidOperationException(
                        "EMS 통신이 연결되어 있지 않습니다.");
                }

                // 현재 30001 실제값을 읽은 뒤
                // Bit0~2 Operating Mode는 Standby(0),
                // Bit12 SystemRun은 Stop(0)으로 변경합니다.
                // Pack / Inverter 등 나머지 비트는 그대로 유지합니다.
                ushort stoppedControlWord =
                    await UpdateControlEssWordAsync(
                        absoluteAddress: EmsControlAddresses.ControlWord1,
                        editableMask: (ushort)(
                            EmsControlWord1.ModeMask |
                            EmsControlWord1.SystemRun),
                        desiredBits: (ushort)EmsOperationMode.Standby,
                        operationName: "Standby 전환 + SystemRun B12 Stop",
                        cancellationToken: cancellationToken);

                WriteLog(
                    $"운전 정지 명령 전송 완료 · " +
                    $"30001=0x{stoppedControlWord:X4} · " +
                    "OperatingMode=Standby · SystemRun B12=0 · " +
                    "실제 Standby/Stop 상태 확인 시작");

                // 최대 5초 동안 실제 상태를 확인합니다.
                // 200ms × 25회 = 약 5초
                const int maxRetryCount = 25;
                const int retryDelayMilliseconds = 200;

                ushort lastSystemStatus1 = 0;
                ushort lastSystemStatus2 = 0;

                for (int retry = 1; retry <= maxRetryCount; retry++)
                {
                    await Task.Delay(
                        retryDelayMilliseconds,
                        cancellationToken);

                    EssStatusData status =
                        await ReadStatusCoreAsync(cancellationToken);

                    lastSystemStatus1 = status.SystemStatus1;
                    lastSystemStatus2 = status.SystemStatus2;

                    EmsOperationMode currentMode =
                        EmsSystemStatus1.GetOperatingMode(
                            status.SystemStatus1);

                    bool isRunning =
                        EmsSystemStatus2.IsRunning(
                            status.SystemStatus2);

                    WriteLog(
                        $"운전 정지 상태 확인 {retry}/{maxRetryCount} · " +
                        $"Mode={currentMode} · " +
                        $"Run={(isRunning ? "Run" : "Stop")} · " +
                        $"31022=0x{status.SystemStatus1:X4} · " +
                        $"31023=0x{status.SystemStatus2:X4}");

                    // Operating Mode가 Standby이고
                    // SystemRunStop 상태가 Stop이면 정지 완료입니다.
                    if (currentMode == EmsOperationMode.Standby &&
                        !isRunning)
                    {
                        WriteLog(
                            $"운전 정지 완료 · " +
                            $"Mode={currentMode} · " +
                            $"Run=Stop · " +
                            $"30001=0x{stoppedControlWord:X4} · " +
                            $"31022=0x{status.SystemStatus1:X4} · " +
                            $"31023=0x{status.SystemStatus2:X4}");

                        FileAppLogger.Info(
                            "OPERATION",
                            $"운전 정지 완료 | " +
                            $"Mode={currentMode} | " +
                            $"Run=Stop | " +
                            $"ControlWord1=0x{stoppedControlWord:X4} | " +
                            $"SystemStatus1=0x{status.SystemStatus1:X4} | " +
                            $"SystemStatus2=0x{status.SystemStatus2:X4}");

                        return;
                    }
                }

                EmsOperationMode lastMode =
                    EmsSystemStatus1.GetOperatingMode(
                        lastSystemStatus1);

                bool lastIsRunning =
                    EmsSystemStatus2.IsRunning(
                        lastSystemStatus2);

                throw new InvalidOperationException(
                    "정지 명령은 전송됐지만 EMS가 제한시간 안에 " +
                    "Standby/Stop 상태로 전환되지 않았습니다. " +
                    $"현재모드={lastMode}, " +
                    $"RunStop={(lastIsRunning ? "Run" : "Stop")}, " +
                    $"31022=0x{lastSystemStatus1:X4}, " +
                    $"31023=0x{lastSystemStatus2:X4}");
            }
            catch (OperationCanceledException)
            {
                FileAppLogger.Warning(
                    "OPERATION",
                    "운전 정지 요청 취소");

                throw;
            }
            catch (Exception ex)
            {
                FileAppLogger.Error(
                    "OPERATION",
                    "운전 정지 실패",
                    ex);

                throw;
            }
        }
        finally
        {
            if (operationSequenceStarted)
            {
                EndOperationSequence("운전 정지");
            }
        }
    }

    private async Task<ushort[]> ReadAdminGroupAsync(
    string groupName,
    byte slaveId,
    ushort startAddress,
    ushort numberOfPoints,
    CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return Array.Empty<ushort>();
        }
        try
        {
            WriteLog(
                $"TX {groupName} · " +
                $"Slave={slaveId} · FC=03 · " +
                $"Start={startAddress} · Count={numberOfPoints}");

            ushort[] values =
     await _modbusService.ReadHoldingRegistersAsync(
         slaveId,
         startAddress,
         numberOfPoints,
         cancellationToken);

            WriteLog(
                $"RX {groupName} · " +
                $"{values.Length}개 수신 성공");

            return values;
        }
        catch (OperationCanceledException)
        {
            WriteLog(
                $"{groupName} 읽기 취소");

            return Array.Empty<ushort>();
        }
        catch (TimeoutException ex)
        {
            WriteLog(
                $"TIMEOUT {groupName} · " +
                $"{ex.Message}");

            return Array.Empty<ushort>();
        }
        catch (InvalidOperationException) when (!IsConnected)
        {
            return Array.Empty<ushort>();
        }
        catch (Exception ex)
        {
            WriteLog(
                $"ERROR {groupName} · " +
                $"{ex.GetType().Name}: {ex.Message}");

            return Array.Empty<ushort>();
        }
    }

    private void WriteLog(string message)
    {
        CommunicationLogReceived?.Invoke(
            $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }
    private static ushort GetValueOrZero(
    ushort[] values,
    int index)
    {
        if (index < 0 || index >= values.Length)
        {
            return 0;
        }

        return values[index];
    }
    private static short ToInt16(ushort rawValue)
    {
        return unchecked((short)rawValue);
    }



}