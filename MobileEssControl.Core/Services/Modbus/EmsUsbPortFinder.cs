using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace MobileEssControl.Services.Modbus;

public static class EmsUsbPortFinder
{
    // Windows 장치 관리자에서 사용하는 형식
    private const string WindowsVendorId = "VID_04D8";
    private const string WindowsProductId = "PID_000A";

    // Linux sysfs에서 사용하는 형식
    private const string LinuxVendorId = "04d8";
    private const string LinuxProductId = "000a";

    private static readonly Regex ComPortRegex =
        new(@"\((COM\d+)\)", RegexOptions.IgnoreCase);

    /// <summary>
    /// 현재 운영체제에서 EMS USB 통신 포트를 검색합니다.
    ///
    /// Windows:
    ///     COM3, COM9 등의 포트 이름 반환
    ///
    /// Linux:
    ///     /dev/ttyACM0, /dev/ttyUSB0 등의 장치 경로 반환
    /// </summary>
    public static string? FindEmsComPort()
    {
        if (OperatingSystem.IsWindows())
        {
            return FindWindowsEmsComPort();
        }

        if (OperatingSystem.IsLinux())
        {
            return FindLinuxEmsPort();
        }

        return null;
    }

    /// <summary>
    /// Windows에서 VID/PID가 일치하는 COM 포트를 검색합니다.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? FindWindowsEmsComPort()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity");

            return searcher
                .Get()
                .Cast<ManagementObject>()
                .Select(device =>
                {
                    string deviceName =
                        device["Name"]?.ToString() ??
                        string.Empty;

                    string pnpDeviceId =
                        device["PNPDeviceID"]?.ToString() ??
                        string.Empty;

                    return new
                    {
                        DeviceName = deviceName,
                        PnpDeviceId = pnpDeviceId
                    };
                })
                .Where(device =>
                    device.PnpDeviceId.Contains(
                        WindowsVendorId,
                        StringComparison.OrdinalIgnoreCase) &&
                    device.PnpDeviceId.Contains(
                        WindowsProductId,
                        StringComparison.OrdinalIgnoreCase))
                .Select(device =>
                {
                    Match match =
                        ComPortRegex.Match(device.DeviceName);

                    return match.Success
                        ? match.Groups[1].Value
                        : null;
                })
                .FirstOrDefault(port =>
                    !string.IsNullOrWhiteSpace(port));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Linux에서 EMS USB 포트를 검색합니다.
    ///
    /// 검색 순서:
    /// 1. /dev/serial/by-id
    /// 2. /dev/ttyACM*
    /// 3. /dev/ttyUSB*
    ///
    /// 장치 파일만 보고 선택하지 않고 sysfs의
    /// idVendor/idProduct를 확인합니다.
    /// </summary>
    private static string? FindLinuxEmsPort()
    {
        try
        {
            // 먼저 안정적인 장치 식별 경로를 검사합니다.
            string? byIdPort =
                FindLinuxEmsPortFromById();

            if (!string.IsNullOrWhiteSpace(byIdPort))
            {
                return byIdPort;
            }

            // by-id가 없는 시스템에서는 실제 장치 노드를 검사합니다.
            return FindLinuxEmsPortFromDeviceNodes();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// /dev/serial/by-id 심볼릭 링크를 통해 EMS를 찾습니다.
    /// SerialPort에는 최종적으로 /dev/ttyACM0 등의 실제 경로를 반환합니다.
    /// </summary>
    private static string? FindLinuxEmsPortFromById()
    {
        const string byIdDirectory =
            "/dev/serial/by-id";

        if (!Directory.Exists(byIdDirectory))
        {
            return null;
        }

        IEnumerable<string> links;

        try
        {
            links = Directory
                .EnumerateFileSystemEntries(byIdDirectory)
                .OrderBy(path => path);
        }
        catch
        {
            return null;
        }

        foreach (string linkPath in links)
        {
            string? resolvedDevicePath =
                ResolveSymbolicLink(linkPath);

            if (string.IsNullOrWhiteSpace(resolvedDevicePath))
            {
                continue;
            }

            if (IsLinuxEmsSerialDevice(resolvedDevicePath))
            {
                return resolvedDevicePath;
            }
        }

        return null;
    }

    /// <summary>
    /// /dev/ttyACM* 및 /dev/ttyUSB*를 직접 검사합니다.
    /// </summary>
    private static string? FindLinuxEmsPortFromDeviceNodes()
    {
        IEnumerable<string> candidates;

        try
        {
            IEnumerable<string> ttyAcmPorts =
                Directory.EnumerateFiles(
                    "/dev",
                    "usb*");

            IEnumerable<string> ttyUsbPorts =
                Directory.EnumerateFiles(
                    "/dev",
                    "ttyUSB*");

            candidates = ttyAcmPorts
                .Concat(ttyUsbPorts)
                .OrderBy(path => path);
        }
        catch
        {
            return null;
        }

        foreach (string candidate in candidates)
        {
            if (IsLinuxEmsSerialDevice(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Linux sysfs에서 해당 tty 장치의 USB VID/PID를 확인합니다.
    ///
    /// 예:
    /// /dev/ttyACM0
    /// → /sys/class/tty/ttyACM0/device
    /// → 상위 USB 장치의 idVendor/idProduct 확인
    /// </summary>
    private static bool IsLinuxEmsSerialDevice(
        string devicePath)
    {
        try
        {
            string ttyName =
                Path.GetFileName(devicePath);

            if (string.IsNullOrWhiteSpace(ttyName))
            {
                return false;
            }

            string sysDevicePath =
                Path.Combine(
                    "/sys/class/tty",
                    ttyName,
                    "device");

            if (!Directory.Exists(sysDevicePath))
            {
                return false;
            }

            DirectoryInfo sysDeviceDirectory =
                new(sysDevicePath);

            FileSystemInfo? resolvedDevice =
                sysDeviceDirectory.ResolveLinkTarget(
                    returnFinalTarget: true);

            DirectoryInfo? currentDirectory =
                resolvedDevice as DirectoryInfo;

            if (currentDirectory is null)
            {
                return false;
            }

            // tty 장치에서 USB 장치 루트까지 부모 폴더를 따라 올라가며
            // idVendor와 idProduct 파일을 찾습니다.
            while (currentDirectory is not null)
            {
                string vendorFilePath =
                    Path.Combine(
                        currentDirectory.FullName,
                        "idVendor");

                string productFilePath =
                    Path.Combine(
                        currentDirectory.FullName,
                        "idProduct");

                if (File.Exists(vendorFilePath) &&
                    File.Exists(productFilePath))
                {
                    string vendorId =
                        File.ReadAllText(vendorFilePath)
                            .Trim();

                    string productId =
                        File.ReadAllText(productFilePath)
                            .Trim();

                    return
                        string.Equals(
                            vendorId,
                            LinuxVendorId,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            productId,
                            LinuxProductId,
                            StringComparison.OrdinalIgnoreCase);
                }

                currentDirectory =
                    currentDirectory.Parent;
            }
        }
        catch
        {
            // 특정 장치 검사 실패는 무시하고 다음 장치를 검사합니다.
        }

        return false;
    }

    /// <summary>
    /// Linux 심볼릭 링크의 최종 실제 장치 경로를 반환합니다.
    /// </summary>
    private static string? ResolveSymbolicLink(
        string linkPath)
    {
        try
        {
            FileInfo linkInfo =
                new(linkPath);

            FileSystemInfo? target =
                linkInfo.ResolveLinkTarget(
                    returnFinalTarget: true);

            return target?.FullName;
        }
        catch
        {
            return null;
        }
    }
}