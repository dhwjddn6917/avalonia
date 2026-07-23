using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using MobileEssControl.Services.Interfaces;
using MobileEssControl.Services.Logging;

namespace MobileEssControl.Services.Modbus;

public class ModbusService : IModbusService, IDisposable
{
    private readonly SemaphoreSlim _communicationLock = new(1, 1);

    private SerialPort? _serialPort;

    private const int ResponseTimeoutMilliseconds = 1000;

    // EMS 상태값 Read 응답은 Low Byte -> High Byte 순서로 들어옵니다.
    // 예: 340.80V = 0x8520 이 응답에서는 20 85 로 들어옵니다.
    private const bool UseLittleEndianRegisterReadData = true;

    // EMS 제어 Write 요청은 Modbus 표준 High Byte -> Low Byte 순서로 보내야 합니다.
    // 예: Operation Mode 1 = 0x0001 은 00 01 로 보내야 합니다.
    private const bool UseLittleEndianRegisterWriteData = false;

    public event Action<string>? CommunicationLog;

    public bool IsConnected => _serialPort?.IsOpen == true;

    public string? ConnectedPortName => _serialPort?.PortName;

    public async Task<bool> ConnectAsync(
        string portName,
        int baudRate = 115200,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _communicationLock.WaitAsync(cancellationToken);

        try
        {
            FileAppLogger.Info(
                "MODBUS",
                $"연결 요청 | COM={portName} | BaudRate={baudRate} | Format=8N1");

            DisconnectInternal();

            _serialPort = new SerialPort(
                portName,
                baudRate,
                Parity.None,
                8,
                StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = ResponseTimeoutMilliseconds,
                WriteTimeout = ResponseTimeoutMilliseconds
            };

            _serialPort.Open();

            PublishLog(
                $"[CONNECT] COM={portName}, {baudRate} bps, 8N1");
            PublishLog(
               "[MODBUS ORDER] Read=LITTLE / Write=STANDARD · Logical 0x0001 -> Write 00 01");

            FileAppLogger.Info(
                "MODBUS",
                $"연결 성공 | COM={portName} | BaudRate={baudRate} | Format=8N1");

            return true;
        }
        catch (Exception ex)
        {
            PublishLog(
                $"[ERROR] COM={portName} 연결 실패 · {ex.Message}");

            FileAppLogger.Error(
                "MODBUS",
                $"연결 실패 | COM={portName} | BaudRate={baudRate}",
                ex);

            DisconnectInternal();

            return false;
        }
        finally
        {
            _communicationLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _communicationLock.WaitAsync();

        try
        {
            string? portName = ConnectedPortName;

            FileAppLogger.Info(
                "MODBUS",
                $"연결 해제 요청 | COM={portName ?? "-"}");

            DisconnectInternal();

            PublishLog(
                $"[DISCONNECT] COM={portName ?? "-"}");

            FileAppLogger.Info(
                "MODBUS",
                $"연결 해제 완료 | COM={portName ?? "-"}");
        }
        finally
        {
            _communicationLock.Release();
        }
    }

    public async Task<string?> FindAndConnectEmsAsync(
        int baudRate = 115200,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PublishLog(
            "[AUTO CONNECT] EMS USB 검색 시작 · " +
            "VID_04D8 / PID_000A");

        FileAppLogger.Info(
            "MODBUS",
            "EMS USB 자동 검색 시작 | VID=04D8 | PID=000A");

        string? emsPortName =
            EmsUsbPortFinder.FindEmsComPort();

        if (string.IsNullOrWhiteSpace(emsPortName))
        {
            PublishLog(
                "[AUTO CONNECT] EMS USB 장치를 찾지 못했습니다. " +
                "USB A-B 케이블과 EMS 보드 전원을 확인하세요.");

            FileAppLogger.Warning(
                "MODBUS",
                "EMS USB 자동 검색 실패 | 장치를 찾지 못했습니다.");

            return null;
        }

        PublishLog(
            $"[AUTO CONNECT] EMS USB 장치 발견 · {emsPortName}");

        FileAppLogger.Info(
            "MODBUS",
            $"EMS USB 장치 발견 | COM={emsPortName}");

        bool connected = await ConnectAsync(
            emsPortName,
            baudRate,
            cancellationToken);

        if (!connected)
        {
            PublishLog(
                $"[AUTO CONNECT] EMS 포트 열기 실패 · {emsPortName}");

            FileAppLogger.Error(
                "MODBUS",
                $"EMS USB 자동 연결 실패 | COM={emsPortName}");

            return null;
        }

        PublishLog(
            $"[AUTO CONNECT] EMS USB 포트 열기 성공 · {emsPortName}");

        FileAppLogger.Info(
            "MODBUS",
            $"EMS USB 자동 연결 성공 | COM={emsPortName} | BaudRate={baudRate}");

        return emsPortName;
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort numberOfPoints,
        CancellationToken cancellationToken = default)
    {
        if (numberOfPoints == 0 || numberOfPoints > 125)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numberOfPoints),
                "FC03 Holding Register Read는 1~125개만 읽을 수 있습니다.");
        }

        byte[] requestWithoutCrc =
        {
            slaveId,
            0x03,
            (byte)(startAddress >> 8),
            (byte)(startAddress & 0xFF),
            (byte)(numberOfPoints >> 8),
            (byte)(numberOfPoints & 0xFF)
        };

        byte[] requestFrame = ModbusCrc16.AddCrc(
            requestWithoutCrc);

        byte[] responseFrame = await SendAndReceiveReadResponseAsync(
            requestFrame,
            cancellationToken);

        ValidateResponse(
            responseFrame,
            slaveId,
            0x03);

        int byteCount = responseFrame[2];
        int expectedByteCount = numberOfPoints * 2;

        if (byteCount != expectedByteCount)
        {
            throw new InvalidOperationException(
                $"FC03 응답 데이터 길이가 다릅니다. " +
                $"요청={expectedByteCount} byte, 응답={byteCount} byte");
        }

        ushort[] values = new ushort[numberOfPoints];

        for (int i = 0; i < numberOfPoints; i++)
        {
            int dataIndex = 3 + (i * 2);

            values[i] = ReadRegisterValue(
                responseFrame[dataIndex],
                responseFrame[dataIndex + 1]);
        }

        return values;
    }

    public async Task WriteSingleCoilAsync(
        byte slaveId,
        ushort address,
        bool value,
        CancellationToken cancellationToken = default)
    {
        ushort coilValue = value ? (ushort)0xFF00 : (ushort)0x0000;

        byte[] requestWithoutCrc =
        {
            slaveId,
            0x05,
            (byte)(address >> 8),
            (byte)(address & 0xFF),
            (byte)(coilValue >> 8),
            (byte)(coilValue & 0xFF)
        };

        byte[] requestFrame = ModbusCrc16.AddCrc(
            requestWithoutCrc);

        byte[] responseFrame = await SendAndReceiveFixedResponseAsync(
            requestFrame,
            expectedResponseLength: 8,
            cancellationToken);

        ValidateResponse(
            responseFrame,
            slaveId,
            0x05);

        for (int i = 0; i < 6; i++)
        {
            if (responseFrame[i] != requestFrame[i])
            {
                throw new InvalidOperationException(
                    "FC05 Write Single Coil 응답값이 요청값과 다릅니다.");
            }
        }
    }

    public async Task WriteSingleRegisterAsync(
        byte slaveId,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default)
    {
        byte[] requestWithoutCrc =
        {
            slaveId,
            0x06,
            (byte)(address >> 8),
            (byte)(address & 0xFF),
            GetRegisterValueFirstByte(value),
            GetRegisterValueSecondByte(value)
        };

        PublishLog(
            $"[FC06 VALUE] Addr={address} Value=0x{value:X4} Data={requestWithoutCrc[4]:X2} {requestWithoutCrc[5]:X2}");

        byte[] requestFrame = ModbusCrc16.AddCrc(
            requestWithoutCrc);

        byte[] responseFrame = await SendAndReceiveFixedResponseAsync(
            requestFrame,
            expectedResponseLength: 8,
            cancellationToken);

        ValidateResponse(
            responseFrame,
            slaveId,
            0x06);

        for (int i = 0; i < 6; i++)
        {
            if (responseFrame[i] != requestFrame[i])
            {
                throw new InvalidOperationException(
                    "FC06 Write Single Register 응답값이 요청값과 다릅니다.");
            }
        }
    }

    public async Task WriteMultipleRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort[] values,
        CancellationToken cancellationToken = default)
    {
        if (values is null || values.Length == 0 || values.Length > 123)
        {
            throw new ArgumentException(
                "FC16 Write Multiple Registers는 1~123개만 쓸 수 있습니다.",
                nameof(values));
        }

        int byteCount = values.Length * 2;

        byte[] requestWithoutCrc = new byte[7 + byteCount];

        requestWithoutCrc[0] = slaveId;
        requestWithoutCrc[1] = 0x10;

        requestWithoutCrc[2] = (byte)(startAddress >> 8);
        requestWithoutCrc[3] = (byte)(startAddress & 0xFF);

        requestWithoutCrc[4] = (byte)(values.Length >> 8);
        requestWithoutCrc[5] = (byte)(values.Length & 0xFF);

        requestWithoutCrc[6] = (byte)byteCount;

        for (int i = 0; i < values.Length; i++)
        {
            int dataIndex = 7 + (i * 2);

            requestWithoutCrc[dataIndex] =
                GetRegisterValueFirstByte(values[i]);

            requestWithoutCrc[dataIndex + 1] =
                GetRegisterValueSecondByte(values[i]);
        }

        byte[] requestFrame = ModbusCrc16.AddCrc(
            requestWithoutCrc);

        byte[] responseFrame = await SendAndReceiveFixedResponseAsync(
            requestFrame,
            expectedResponseLength: 8,
            cancellationToken);

        ValidateResponse(
            responseFrame,
            slaveId,
            0x10);

        ushort responseStartAddress = (ushort)(
            (responseFrame[2] << 8) |
            responseFrame[3]);

        ushort responseQuantity = (ushort)(
            (responseFrame[4] << 8) |
            responseFrame[5]);

        if (responseStartAddress != startAddress ||
            responseQuantity != values.Length)
        {
            throw new InvalidOperationException(
                "FC16 Write Multiple Registers 응답값이 요청값과 다릅니다.");
        }
    }

    private static ushort ReadRegisterValue(byte firstByte, byte secondByte)
    {
        if (UseLittleEndianRegisterReadData)
        {
            return (ushort)((secondByte << 8) | firstByte);
        }

        return (ushort)((firstByte << 8) | secondByte);
    }

    private static byte GetRegisterValueFirstByte(ushort value)
    {
        return UseLittleEndianRegisterWriteData
            ? (byte)(value & 0xFF)
            : (byte)(value >> 8);
    }

    private static byte GetRegisterValueSecondByte(ushort value)
    {
        return UseLittleEndianRegisterWriteData
            ? (byte)(value >> 8)
            : (byte)(value & 0xFF);
    }

    private async Task<byte[]> SendAndReceiveFixedResponseAsync(
        byte[] requestFrame,
        int expectedResponseLength,
        CancellationToken cancellationToken)
    {
        await _communicationLock.WaitAsync(cancellationToken);

        try
        {
            SerialPort serialPort = GetConnectedSerialPort();

            return await Task.Run(
                () => SendAndReceiveFixedResponseBlocking(
                    serialPort,
                    requestFrame,
                    expectedResponseLength,
                    cancellationToken),
                CancellationToken.None);
        }
        catch (Exception ex) when (
            IsPhysicalCommunicationFailure(ex))
        {
            HandlePhysicalCommunicationFailure(
                "고정 길이 응답 통신",
                ex);

            throw;
        }
        finally
        {
            _communicationLock.Release();
        }
    }

    private async Task<byte[]> SendAndReceiveReadResponseAsync(
        byte[] requestFrame,
        CancellationToken cancellationToken)
    {
        await _communicationLock.WaitAsync(cancellationToken);

        try
        {
            SerialPort serialPort = GetConnectedSerialPort();

            return await Task.Run(
                () => SendAndReceiveReadResponseBlocking(
                    serialPort,
                    requestFrame,
                    cancellationToken),
                CancellationToken.None);
        }
        catch (Exception ex) when (
            IsPhysicalCommunicationFailure(ex))
        {
            HandlePhysicalCommunicationFailure(
                "FC03 응답 통신",
                ex);

            throw;
        }
        finally
        {
            _communicationLock.Release();
        }
    }

    private byte[] SendAndReceiveFixedResponseBlocking(
        SerialPort serialPort,
        byte[] requestFrame,
        int expectedResponseLength,
        CancellationToken cancellationToken)
    {
        SendRequestBlocking(
            serialPort,
            requestFrame,
            cancellationToken);

        byte[] responseFrame = ReadExactlyBlocking(
            serialPort,
            expectedResponseLength,
            cancellationToken);

        PublishLog(
            $"[RX] COM={serialPort.PortName}  {ToHex(responseFrame)}");

        FileAppLogger.Detailed(
            "MODBUS",
            $"RX | COM={serialPort.PortName} | Raw={ToHex(responseFrame)}");

        return responseFrame;
    }

    private byte[] SendAndReceiveReadResponseBlocking(
        SerialPort serialPort,
        byte[] requestFrame,
        CancellationToken cancellationToken)
    {
        SendRequestBlocking(
            serialPort,
            requestFrame,
            cancellationToken);

        byte[] header = ReadExactlyBlocking(
            serialPort,
            3,
            cancellationToken);

        int remainingLength;

        if ((header[1] & 0x80) != 0)
        {
            // Slave + Function + ExceptionCode + CRC(2)
            remainingLength = 2;
        }
        else
        {
            int byteCount = header[2];

            if (byteCount < 0 || byteCount > 250)
            {
                throw new InvalidOperationException(
                    $"FC03 응답 Byte Count가 올바르지 않습니다. {byteCount}");
            }

            // Data + CRC(2)
            remainingLength = byteCount + 2;
        }

        byte[] remaining = ReadExactlyBlocking(
            serialPort,
            remainingLength,
            cancellationToken);

        byte[] responseFrame = new byte[
            header.Length + remaining.Length];

        Buffer.BlockCopy(
            header,
            0,
            responseFrame,
            0,
            header.Length);

        Buffer.BlockCopy(
            remaining,
            0,
            responseFrame,
            header.Length,
            remaining.Length);

        PublishLog(
            $"[RX] COM={serialPort.PortName}  {ToHex(responseFrame)}");

        FileAppLogger.Detailed(
            "MODBUS",
            $"RX | COM={serialPort.PortName} | Raw={ToHex(responseFrame)}");

        return responseFrame;
    }

    private void SendRequestBlocking(
        SerialPort serialPort,
        byte[] requestFrame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        serialPort.DiscardInBuffer();
        serialPort.DiscardOutBuffer();

        try
        {
            PublishLog(
                $"[TX] COM={serialPort.PortName}  {ToHex(requestFrame)}");

            FileAppLogger.Detailed(
                "MODBUS",
                $"TX | COM={serialPort.PortName} | Raw={ToHex(requestFrame)}");

            serialPort.Write(
                requestFrame,
                0,
                requestFrame.Length);
        }
        catch (TimeoutException ex)
        {
            PublishLog(
                $"[ERROR] TX 시간 초과 · COM={serialPort.PortName}");

            FileAppLogger.Error(
                "MODBUS",
                $"TX 시간 초과 | COM={serialPort.PortName} | Timeout={serialPort.WriteTimeout}ms",
                ex);

            throw new TimeoutException(
                $"Modbus 요청 전송 시간 초과 · " +
                $"COM={serialPort.PortName} · " +
                $"{serialPort.WriteTimeout} ms",
                ex);
        }
        catch (InvalidOperationException ex)
        {
            PublishLog(
                $"[ERROR] TX 포트 쓰기 실패 · " +
                $"COM={serialPort.PortName} · {ex.Message}");

            FileAppLogger.Error(
                "MODBUS",
                $"TX 포트 쓰기 실패 | COM={serialPort.PortName}",
                ex);

            throw new InvalidOperationException(
                $"Modbus 포트 쓰기 실패 · " +
                $"COM={serialPort.PortName} · " +
                $"{ex.Message}",
                ex);
        }
    }

    private byte[] ReadExactlyBlocking(
        SerialPort serialPort,
        int length,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[length];

        int totalRead = 0;

        while (totalRead < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                int readLength = serialPort.Read(
                    buffer,
                    totalRead,
                    length - totalRead);

                if (readLength <= 0)
                {
                    throw new TimeoutException(
                        "Modbus 응답을 받지 못했습니다.");
                }

                totalRead += readLength;
            }
            catch (TimeoutException ex)
            {
                PublishLog(
                    $"[ERROR] RX 시간 초과 · " +
                    $"COM={serialPort.PortName} · " +
                    $"{serialPort.ReadTimeout} ms");

                FileAppLogger.Error(
                    "MODBUS",
                    $"RX 시간 초과 | COM={serialPort.PortName} | Timeout={serialPort.ReadTimeout}ms",
                    ex);

                throw new TimeoutException(
                    $"Modbus 응답 시간 초과 · " +
                    $"{serialPort.ReadTimeout} ms 동안 응답이 없습니다.",
                    ex);
            }
        }

        return buffer;
    }

    private static bool IsPhysicalCommunicationFailure(
        Exception exception)
    {
        return exception is TimeoutException ||
               exception is IOException ||
               exception is UnauthorizedAccessException ||
               exception is InvalidOperationException;
    }

    private void HandlePhysicalCommunicationFailure(
        string operationName,
        Exception exception)
    {
        string? portName =
            ConnectedPortName;

        PublishLog(
            $"[CONNECTION LOST] {operationName} 실패 · " +
            $"COM={portName ?? "-"} · " +
            $"{exception.GetType().Name}: {exception.Message}");

        FileAppLogger.Error(
            "MODBUS",
            $"통신 연결 끊김 감지 | " +
            $"Operation={operationName} | " +
            $"COM={portName ?? "-"}",
            exception);

        // 현재 메서드는 _communicationLock을 이미 보유하고 있으므로
        // DisconnectAsync를 호출하지 않고 내부 포트를 직접 닫습니다.
        // 이후 IsConnected가 false가 되어 공통 화면의 자동 재연결이 시작됩니다.
        DisconnectInternal();
    }

    private SerialPort GetConnectedSerialPort()
    {
        SerialPort? serialPort = _serialPort;

        if (serialPort is null || !serialPort.IsOpen)
        {
            throw new InvalidOperationException(
                "Modbus COM 포트가 연결되어 있지 않습니다.");
        }

        return serialPort;
    }

    private static void ValidateResponse(
        byte[] responseFrame,
        byte expectedSlaveId,
        byte expectedFunctionCode)
    {
        if (!ModbusCrc16.IsValid(responseFrame))
        {
            throw new InvalidOperationException(
                "Modbus 응답 CRC가 올바르지 않습니다.");
        }

        if (responseFrame[0] != expectedSlaveId)
        {
            throw new InvalidOperationException(
                $"응답 Slave ID가 다릅니다. " +
                $"요청: {expectedSlaveId}, 응답: {responseFrame[0]}");
        }

        if ((responseFrame[1] & 0x80) != 0)
        {
            byte exceptionCode = responseFrame[2];

            throw new InvalidOperationException(
                $"Modbus Exception 응답: 0x{exceptionCode:X2}");
        }

        if (responseFrame[1] != expectedFunctionCode)
        {
            throw new InvalidOperationException(
                $"응답 Function Code가 다릅니다. " +
                $"요청: 0x{expectedFunctionCode:X2}, " +
                $"응답: 0x{responseFrame[1]:X2}");
        }
    }

    private void PublishLog(string message)
    {
        Action<string>? handlers = CommunicationLog;

        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string>)handler)(message);
            }
            catch
            {
                // 화면 로그 표시 오류가 통신을 막으면 안 됨
            }
        }
    }

    private static string ToHex(byte[] data)
    {
        return BitConverter
            .ToString(data)
            .Replace("-", " ");
    }

    private void DisconnectInternal()
    {
        if (_serialPort is null)
        {
            return;
        }

        try
        {
            if (_serialPort.IsOpen)
            {
                _serialPort.Close();
            }

            _serialPort.Dispose();
        }
        catch
        {
            // 이미 닫힌 포트는 무시
        }
        finally
        {
            _serialPort = null;
        }
    }

    public void Dispose()
    {
        DisconnectInternal();

        _communicationLock.Dispose();
    }
}