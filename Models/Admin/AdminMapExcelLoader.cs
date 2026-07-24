using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MobileEssControl.Models.Admin;

/// <summary>
/// 관리자 모드 EMS 탭의 비트필드 표시를 회사 표준 문서
/// "MobileESS_ModBus_AddressMap_*.xlsx" 원본에서 바로 읽어옵니다.
///
/// 이 문서는 두 종류의 시트로 구성됩니다.
///   1) 마스터 시트: 모든 레지스터를 한 줄씩 나열 (Absolute Address / Word Length 헤더 보유)
///   2) 상세 시트(System Status / System Alarms / Battery Pack Alarms / Battery Pack Status 등):
///      Bit Field 레지스터 하나당 비트별 이름/값 의미를 나열 (Bit Position / Data Length 헤더 보유)
///
/// 마스터 시트의 Remark 열에 있는 "Refer to XXX Sheet" 문구로 어느 상세 시트를 볼지 찾고,
/// 상세 시트가 정의한 비트 패턴 개수보다 대상 레지스터가 더 많으면(Pack1/Pack2처럼 같은 패턴을
/// 반복 사용하는 경우) 순서대로 돌려가며(cycle) 적용합니다.
///
/// 숫자 레지스터의 Scale/Unit(예: "10mV", "0.001")은 표기 관례가 레지스터마다 달라
/// 자동 해석이 위험하므로 다루지 않습니다. 이 로더는 비트필드 라벨만 갱신합니다.
/// </summary>
public static class AdminMapExcelLoader
{
    private const int RemarkNameWidth = 46;

    private static readonly Regex ReferSheetPattern =
        new(@"Refer\s+to\s+(.+?)\s+Sheet", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LabelPairPattern =
        new(@"(-?\d+)\s*:\s*(.*?)(?=(?:-?\d+\s*:)|$)", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// 절대주소 문자열(예: "31022") → 그 레지스터의 비트필드 디코더.
    /// 대상 레지스터를 찾지 못했거나 상세 시트를 해석할 수 없으면 그 항목은 결과에서 빠집니다
    /// (호출 쪽에서 코드 내장 기본 디코더를 그대로 씁니다 — 안전하게 실패합니다).
    /// </summary>
    public static IReadOnlyDictionary<string, Func<ushort, string>> LoadBitFieldDecoders(string filePath)
    {
        using XLWorkbook workbook = new(filePath);

        IXLWorksheet masterSheet = FindMasterSheet(workbook);

        List<(int AbsoluteAddress, string DetailSheetName)> bitFieldRegisters =
            ReadMasterBitFieldRegisters(masterSheet);

        if (bitFieldRegisters.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{masterSheet.Name}' 시트에서 Bit Field 레지스터를 찾지 못했습니다. " +
                "'Data Type' 열이 'Bit Field'인 행이 있는지 확인해주세요.");
        }

        Dictionary<string, Func<ushort, string>> result = new();

        foreach (IGrouping<string, (int AbsoluteAddress, string DetailSheetName)> group in
            bitFieldRegisters.GroupBy(r => r.DetailSheetName, StringComparer.OrdinalIgnoreCase))
        {
            IXLWorksheet? detailSheet = FindWorksheetByName(workbook, group.Key);

            if (detailSheet is null)
            {
                continue;
            }

            List<List<BitFieldEntry>> blocks = ParseDetailSheetBlocks(detailSheet);

            if (blocks.Count == 0)
            {
                continue;
            }

            List<(int AbsoluteAddress, string DetailSheetName)> targets = group.ToList();

            for (int i = 0; i < targets.Count; i++)
            {
                // 상세 시트가 정의한 패턴보다 대상 레지스터가 더 많으면(Pack1/Pack2 등)
                // 순서대로 돌려가며 같은 패턴을 재사용합니다.
                List<BitFieldEntry> block = blocks[i % blocks.Count];

                result[targets[i].AbsoluteAddress.ToString()] = BuildDecoder(block);
            }
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException(
                "Bit Field 레지스터는 찾았지만, 연결된 상세 시트(System Status 등)를 하나도 읽지 못했습니다. " +
                "시트 이름과 'Refer to ... Sheet' 문구가 일치하는지 확인해주세요.");
        }

        return result;
    }

    private readonly record struct BitFieldEntry(string FieldName, int BitStart, int BitWidth, string Remark);

    private static Func<ushort, string> BuildDecoder(List<BitFieldEntry> fields)
    {
        return raw =>
        {
            List<string> lines = new(fields.Count);

            foreach (BitFieldEntry field in fields)
            {
                int mask = (1 << field.BitWidth) - 1;
                int value = (raw >> field.BitStart) & mask;
                string label = LookupLabel(field.Remark, value);

                lines.Add($"{field.FieldName.PadRight(RemarkNameWidth)} : {label}");
            }

            return string.Join(Environment.NewLine, lines);
        };
    }

    /// <summary>
    /// "0: Normal\n1: Warning\n2: Fault" / "0: Off / 1: On" / "0: Normal 1: Fault" 등
    /// 문서마다 다른 구분자 표기를 모두 "숫자 다음에 콜론" 패턴으로 찾아 처리합니다.
    /// </summary>
    private static string LookupLabel(string remark, int value)
    {
        if (string.IsNullOrWhiteSpace(remark))
        {
            return "Reserved";
        }

        foreach (Match match in LabelPairPattern.Matches(remark))
        {
            if (!int.TryParse(match.Groups[1].Value, out int key))
            {
                continue;
            }

            if (key != value)
            {
                continue;
            }

            string label = match.Groups[2].Value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim()
                .TrimEnd('/')
                .Trim();

            return string.IsNullOrEmpty(label) ? "Reserved" : label;
        }

        return "Reserved";
    }

    private static List<(int AbsoluteAddress, string DetailSheetName)> ReadMasterBitFieldRegisters(
        IXLWorksheet masterSheet)
    {
        IXLRow headerRow = FindHeaderRow(masterSheet, "Absolute Address");

        int absoluteAddressCol = FindColumn(headerRow, "Absolute Address");
        int dataTypeCol = FindColumn(headerRow, "Data Type");
        int remarkCol = FindColumn(headerRow, "Remark");

        List<(int, string)> results = new();
        string? currentDetailSheetName = null;

        foreach (IXLRow row in masterSheet.RowsUsed())
        {
            if (row.RowNumber() <= headerRow.RowNumber())
            {
                continue;
            }

            string remark = row.Cell(remarkCol).GetString();
            Match referMatch = ReferSheetPattern.Match(remark);

            if (referMatch.Success)
            {
                currentDetailSheetName = referMatch.Groups[1].Value.Trim();
            }

            string dataType = row.Cell(dataTypeCol).GetString().Trim();

            if (!string.Equals(dataType, "Bit Field", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string addressText = row.Cell(absoluteAddressCol).GetString().Trim();

            if (!int.TryParse(addressText, out int address))
            {
                // #REF! 등 깨진 주소는 건너뜁니다.
                continue;
            }

            if (currentDetailSheetName is null)
            {
                continue;
            }

            results.Add((address, currentDetailSheetName));
        }

        return results;
    }

    private static List<List<BitFieldEntry>> ParseDetailSheetBlocks(IXLWorksheet detailSheet)
    {
        IXLRow? headerRow = TryFindHeaderRow(detailSheet, "Bit Position");

        if (headerRow is null)
        {
            return new List<List<BitFieldEntry>>();
        }

        List<int> nameColumns = FindAllColumns(headerRow, "Name");

        if (nameColumns.Count == 0)
        {
            return new List<List<BitFieldEntry>>();
        }

        int registerNameCol = nameColumns.Min();
        int fieldNameCol = nameColumns.Max();
        int bitPositionCol = FindColumn(headerRow, "Bit Position");
        int dataLengthCol = FindColumn(headerRow, "Data Length");
        int remarkCol = FindColumn(headerRow, "Remark");

        List<List<BitFieldEntry>> blocks = new();
        List<BitFieldEntry>? currentBlock = null;

        foreach (IXLRow row in detailSheet.RowsUsed())
        {
            if (row.RowNumber() <= headerRow.RowNumber())
            {
                continue;
            }

            string bitPositionText = row.Cell(bitPositionCol).GetString().Trim();

            if (string.IsNullOrEmpty(bitPositionText))
            {
                continue;
            }

            if (!int.TryParse(bitPositionText, out int bitStart))
            {
                continue;
            }

            string registerName = row.Cell(registerNameCol).GetString().Trim();

            if (!string.IsNullOrEmpty(registerName))
            {
                currentBlock = new List<BitFieldEntry>();
                blocks.Add(currentBlock);
            }

            if (currentBlock is null)
            {
                continue;
            }

            string fieldName = row.Cell(fieldNameCol).GetString().Trim();

            if (string.IsNullOrEmpty(fieldName) ||
                fieldName.Equals("Reserved", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int bitWidth = int.TryParse(row.Cell(dataLengthCol).GetString().Trim(), out int parsedWidth)
                ? Math.Max(1, parsedWidth)
                : 1;

            string remark = row.Cell(remarkCol).GetString();

            currentBlock.Add(new BitFieldEntry(fieldName, bitStart, bitWidth, remark));
        }

        return blocks;
    }

    private static IXLWorksheet FindMasterSheet(XLWorkbook workbook)
    {
        foreach (IXLWorksheet sheet in workbook.Worksheets)
        {
            IXLRow? headerRow = TryFindHeaderRow(sheet, "Absolute Address");

            if (headerRow is null)
            {
                continue;
            }

            if (HasColumn(headerRow, "Word Length"))
            {
                return sheet;
            }
        }

        throw new InvalidOperationException(
            "전체 레지스터 목록 시트를 찾지 못했습니다. " +
            "'Absolute Address'와 'Word Length' 헤더가 있는 시트가 필요합니다.");
    }

    private static IXLWorksheet? FindWorksheetByName(XLWorkbook workbook, string name)
    {
        foreach (IXLWorksheet sheet in workbook.Worksheets)
        {
            if (string.Equals(sheet.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return sheet;
            }
        }

        // 정확히 일치하는 시트가 없으면 이름이 포함된 시트로 한 번 더 시도합니다.
        foreach (IXLWorksheet sheet in workbook.Worksheets)
        {
            if (sheet.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(sheet.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return sheet;
            }
        }

        return null;
    }

    private static IXLRow FindHeaderRow(IXLWorksheet sheet, string requiredHeaderText)
    {
        return TryFindHeaderRow(sheet, requiredHeaderText)
            ?? throw new InvalidOperationException(
                $"'{sheet.Name}' 시트에서 '{requiredHeaderText}' 헤더를 찾지 못했습니다.");
    }

    private static IXLRow? TryFindHeaderRow(IXLWorksheet sheet, string requiredHeaderText)
    {
        foreach (IXLRow row in sheet.RowsUsed().Take(10))
        {
            if (HasColumn(row, requiredHeaderText))
            {
                return row;
            }
        }

        return null;
    }

    private static bool HasColumn(IXLRow headerRow, string headerText)
    {
        foreach (IXLCell cell in headerRow.CellsUsed())
        {
            if (string.Equals(cell.GetString().Trim(), headerText, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int FindColumn(IXLRow headerRow, string headerText)
    {
        foreach (IXLCell cell in headerRow.CellsUsed())
        {
            if (string.Equals(cell.GetString().Trim(), headerText, StringComparison.OrdinalIgnoreCase))
            {
                return cell.Address.ColumnNumber;
            }
        }

        throw new InvalidOperationException(
            $"'{headerRow.Worksheet.Name}' 시트 헤더 행에서 '{headerText}' 열을 찾지 못했습니다.");
    }

    private static List<int> FindAllColumns(IXLRow headerRow, string headerText)
    {
        List<int> columns = new();

        foreach (IXLCell cell in headerRow.CellsUsed())
        {
            if (string.Equals(cell.GetString().Trim(), headerText, StringComparison.OrdinalIgnoreCase))
            {
                columns.Add(cell.Address.ColumnNumber);
            }
        }

        return columns;
    }
}
