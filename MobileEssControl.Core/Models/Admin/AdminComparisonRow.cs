using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace MobileEssControl.Models.Admin;

/// <summary>
/// 같은 항목의 Pack 1 / Pack 2 또는 Inverter 1 / Inverter 2 값을
/// 한 줄에서 비교 표시하기 위한 관리자 화면용 모델입니다.
/// Bit Field는 접어 두고 펼쳤을 때 좌측/우측 값을 별도 열에 표시합니다.
/// </summary>
public partial class AdminComparisonRow : ObservableObject
{
    public required ushort RelativeAddress { get; init; }

    public required string AbsoluteAddressText { get; init; }

    public required string Name { get; init; }

    public required string Unit { get; init; }

    public required string DataType { get; init; }

    public string LeftCaption { get; init; } = "1번";

    public string RightCaption { get; init; } = "2번";

    public double Scale { get; init; } = 1.0;

    public bool IsSigned { get; init; }

    public bool IsBitField { get; init; }

    public Func<ushort, string>? BitFieldDecoder { get; init; }

    public Func<ushort, string>? ValueFormatter { get; init; }

    public int DecimalPlaces { get; init; } = 2;

    /// <summary>
    /// Bit Field일 때만 상세 비교 항목을 한 줄씩 담습니다.
    /// </summary>
    public ObservableCollection<AdminBitComparisonDetailRow> BitDetails { get; } = new();

    // 기존 "0.00 / 0.00" 한 줄 표시 대신 좌·우 값을 각각 별도 열에 표시합니다.
    [ObservableProperty]
    private string leftDisplayValue = "-";

    [ObservableProperty]
    private string rightDisplayValue = "-";

    // Bit Field의 접힌 상태 요약 문구에만 사용합니다.
    [ObservableProperty]
    private string displayValue = "-";

    [ObservableProperty]
    private string rawValue = "-";

    [ObservableProperty]
    private bool isExpanded;

    public string ExpandButtonText => IsExpanded ? "− 접기" : "+ 상세";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandButtonText));
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        if (IsBitField)
        {
            IsExpanded = !IsExpanded;
        }
    }

    public void Update(ushort leftRaw, ushort rightRaw)
    {
        RawValue = $"0x{leftRaw:X4} / 0x{rightRaw:X4}";

        if (IsBitField)
        {
            string leftText = BitFieldDecoder?.Invoke(leftRaw) ?? $"0x{leftRaw:X4}";
            string rightText = BitFieldDecoder?.Invoke(rightRaw) ?? $"0x{rightRaw:X4}";

            UpdateBitDetails(leftText, rightText);

            // Bit Field는 아래 + 상세에서 항목별로 비교한다.
            // 기본 행에는 펼칠 수 있는 상태임을 간단히 표시한다.
            LeftDisplayValue = "상세 상태";
            RightDisplayValue = $"{BitDetails.Count}개 항목";
            DisplayValue = $"상세 상태 {BitDetails.Count}개 항목";
            return;
        }

        LeftDisplayValue = FormatValue(leftRaw);
        RightDisplayValue = FormatValue(rightRaw);
        DisplayValue = $"{LeftDisplayValue} / {RightDisplayValue}";
    }

    private string FormatValue(ushort raw)
    {
        if (ValueFormatter is not null)
        {
            return ValueFormatter(raw);
        }

        double sourceValue = IsSigned
            ? unchecked((short)raw)
            : raw;

        double scaledValue = sourceValue * Scale;
        string numberFormat = $"F{DecimalPlaces}";

        return scaledValue.ToString(numberFormat, CultureInfo.InvariantCulture);
    }

    private void UpdateBitDetails(string leftText, string rightText)
    {
        List<(string Name, string Value)> leftRows =
            AdminRegisterRow.ParseBitFieldLines(leftText);

        List<(string Name, string Value)> rightRows =
            AdminRegisterRow.ParseBitFieldLines(rightText);

        int rowCount = Math.Max(leftRows.Count, rightRows.Count);

        for (int i = 0; i < rowCount; i++)
        {
            string name = i < leftRows.Count
                ? leftRows[i].Name
                : rightRows[i].Name;

            string leftValue = i < leftRows.Count
                ? leftRows[i].Value
                : "-";

            string rightValue = i < rightRows.Count
                ? rightRows[i].Value
                : "-";

            if (i < BitDetails.Count)
            {
                BitDetails[i].Update(name, leftValue, rightValue);
            }
            else
            {
                BitDetails.Add(
                    new AdminBitComparisonDetailRow(name, leftValue, rightValue));
            }
        }

        while (BitDetails.Count > rowCount)
        {
            BitDetails.RemoveAt(BitDetails.Count - 1);
        }
    }
}

/// <summary>
/// 비교형 Bit Field 내부 상태를 "항목 / 좌측 값 / 우측 값"으로 나누어 표시합니다.
/// </summary>
public partial class AdminBitComparisonDetailRow : ObservableObject
{
    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string leftValue;

    [ObservableProperty]
    private string rightValue;

    public AdminBitComparisonDetailRow(
        string name,
        string leftValue,
        string rightValue)
    {
        this.name = name;
        this.leftValue = leftValue;
        this.rightValue = rightValue;
    }

    public void Update(string name, string leftValue, string rightValue)
    {
        Name = name;
        LeftValue = leftValue;
        RightValue = rightValue;
    }
}
