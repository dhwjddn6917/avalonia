using System;
using System.IO;
using System.Text;

namespace MobileEssControl.Services.Logging;

public static class FileAppLogger
{
    private static readonly object _syncLock = new();

    // 나중에 관리자 화면에서 ON/OFF 할 상세 통신 로그 설정값
    public static bool IsDetailedCommunicationLogEnabled { get; set; }

    // 실제 프로그램 실행 파일이 있는 위치 아래에 Logs 폴더를 생성한다.
    public static string LogDirectoryPath =>
        Path.Combine(AppContext.BaseDirectory, "Logs");

    // 오늘 날짜 기준 로그 파일 경로
    public static string CurrentLogFilePath =>
        Path.Combine(
            LogDirectoryPath,
            $"mobileess-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string category, string message)
    {
        Write("INFO", category, message);
    }

    public static void Warning(string category, string message)
    {
        Write("WARN", category, message);
    }

    public static void Error(
        string category,
        string message,
        Exception? exception = null)
    {
        if (exception != null)
        {
            message += Environment.NewLine + exception;
        }

        Write("ERROR", category, message);
    }

    // 상세 통신 로그를 켠 경우에만 기록한다.
    public static void Detailed(string category, string message)
    {
        if (!IsDetailedCommunicationLogEnabled)
        {
            return;
        }

        Write("DEBUG", category, message);
    }

    private static void Write(
        string level,
        string category,
        string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectoryPath);

            string logLine =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} " +
                $"[{level,-5}] " +
                $"[{category}] " +
                $"{message}";

            lock (_syncLock)
            {
                File.AppendAllText(
                    CurrentLogFilePath,
                    logLine + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // 로그 저장 실패 때문에 ESS 제어 프로그램 자체가 멈추면 안 된다.
            // 로그 파일 쓰기 실패는 여기서는 무시한다.
        }
    }
}