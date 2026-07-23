using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Globalization;

namespace MobileEssControl.Models.Admin;

/// <summary>
/// 관리자 제어 탭에서 EEP Parameter 화면처럼 Read 값과 Write 값을 나누어 표시하는 행입니다.
/// Read는 FC03, Register Write는 FC06, Coil Write는 FC05를 사용합니다.
/// </summary>
public enum AdminControlWriteKind
{
    Register,
    Coil,
    BitField
}

public partial class AdminControlTableRow : ObservableObject
{
    [ObservableProperty]
    private bool isChecked;

    [ObservableProperty]
    private string rValue = "-";

    [ObservableProperty]
    private string wValue = string.Empty;

    [ObservableProperty]
    private string rawValue = "-";

    public required ushort Address { get; init; }

    public string AddressText
    {
        get
        {
            if (WriteKind == AdminControlWriteKind.BitField && BitPosition >= 0 && BitLength > 0)
            {
                return BitLength == 1
                    ? $"{Address} B{BitPosition}"
                    : $"{Address} B{BitPosition}~{BitPosition + BitLength - 1}";
            }

            return Address.ToString(CultureInfo.InvariantCulture);
        }
    }

    public required string ItemName { get; init; }

    public string Unit { get; init; } = "-";

    public string DataType { get; init; } = "UINT16";

    public double Scale { get; init; } = 1.0;

    public bool IsSigned { get; init; }

    public int DecimalPlaces { get; init; }

    public AdminControlWriteKind WriteKind { get; init; } = AdminControlWriteKind.Register;

    /// <summary>
    /// WriteKind가 BitField일 때 사용하는 시작 bit 위치입니다.
    /// 예: 30001 Operating Mode는 BitPosition=0, BitLength=3입니다.
    /// </summary>
    public int BitPosition { get; init; } = -1;

    /// <summary>
    /// WriteKind가 BitField일 때 사용하는 bit 길이입니다.
    /// 1bit On/Off 항목은 1, 0~7 Enum 항목은 3입니다.
    /// </summary>
    public int BitLength { get; init; }

    public string FunctionText => WriteKind switch
    {
        AdminControlWriteKind.Coil => "0x03 / 0x05",
        AdminControlWriteKind.BitField => "0x03 + 0x06",
        _ => "0x03 / 0x06"
    };

    public string Remark { get; init; } = string.Empty;

    /// <summary>
    /// Reserved 같은 확인 전용 항목은 false로 두어 W.Value 입력/Write 대상에서 제외합니다.
    /// </summary>
    public bool IsWriteEnabled { get; init; } = true;

    public string FormatReadValue(ushort logicalRaw)
    {
        RawValue = $"0x{logicalRaw:X4} ({logicalRaw})";

        if (WriteKind == AdminControlWriteKind.Coil)
        {
            RValue = logicalRaw == 0 ? "0" : "1";
            return RValue;
        }

        if (WriteKind == AdminControlWriteKind.BitField)
        {
            int fieldValue = ExtractBitField(logicalRaw);
            RValue = fieldValue.ToString(CultureInfo.InvariantCulture);
            return RValue;
        }

        double sourceValue = IsSigned
            ? unchecked((short)logicalRaw)
            : logicalRaw;

        double scaledValue = sourceValue * Scale;
        RValue = scaledValue.ToString($"F{DecimalPlaces}", CultureInfo.InvariantCulture);
        return RValue;
    }

    public int ExtractBitField(ushort logicalRaw)
    {
        if (WriteKind != AdminControlWriteKind.BitField)
        {
            return logicalRaw;
        }

        ValidateBitField();

        int fieldValue = 0;

        for (int i = 0; i < BitLength; i++)
        {
            int mapBit = BitPosition + i;
            int rawBit = ConvertMapBitToRawBit(mapBit);
            int bitValue = (logicalRaw >> rawBit) & 0x01;

            fieldValue |= bitValue << i;
        }

        return fieldValue;
    }

    public ushort ApplyBitFieldValue(ushort currentRaw, int fieldValue)
    {
        ValidateBitField();

        int fieldMax = (1 << BitLength) - 1;

        if (fieldValue < 0 || fieldValue > fieldMax)
        {
            throw new ArgumentOutOfRangeException(
                ItemName,
                $"{ItemName} W.Value는 0~{fieldMax} 범위여야 합니다.");
        }

        ushort result = currentRaw;

        for (int i = 0; i < BitLength; i++)
        {
            int mapBit = BitPosition + i;
            int rawBit = ConvertMapBitToRawBit(mapBit);
            ushort mask = (ushort)(1 << rawBit);

            result = (ushort)(result & ~mask);

            if (((fieldValue >> i) & 0x01) == 1)
            {
                result = (ushort)(result | mask);
            }
        }

        return result;
    }

    private void ValidateBitField()
    {
        if (BitPosition < 0 || BitPosition > 15 ||
            BitLength <= 0 || BitLength > 16 ||
            BitPosition + BitLength > 16)
        {
            throw new InvalidOperationException($"{ItemName} BitField 설정이 잘못되었습니다.");
        }
    }

    /// Address Map의 Bit 표기는 Register 숫자값의 LSB 기준입니다.
    /// B0은 Raw bit0, B8은 Raw bit8입니다.
    /// 예:
    /// 30001 B0=1  → Raw 0x0001
    /// 30001 B8=1  → Raw 0x0100
    /// B0과 B8이 같이 1이면 Raw 0x0101입니다.
    public static int ConvertMapBitToRawBit(int mapBit)
    {
        if (mapBit < 0 || mapBit > 15)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mapBit),
                "Bit 번호는 0~15 범위여야 합니다.");
        }

        return mapBit;
    }
}
