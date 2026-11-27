using ClosedXML.Excel;
using ElectricalTestingMachineMain.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ElectricalTestingMachineMain.Service
{
    /// <summary>将每个工位的测试结果即时追加到当天对应的 Excel 文件。</summary>
    public class SaveResultService
    {
        private static readonly SaveResultService instance = new Lazy<SaveResultService>(() => new SaveResultService()).Value;
        private readonly object _syncRoot = new();
        private readonly Dictionary<string, ProcessData> _tempRecords = new();

        public static SaveResultService Instance => instance;
        public List<ProcessData> ResumedProcessData { get; } = new();

        public string ResultDirectory
        {
            get
            {
                string configured = ConfigData.Instance.ResultDirectory;
                string fallback = Path.Combine(AppContext.BaseDirectory, "Result");
                try
                {
                    string directory = string.IsNullOrWhiteSpace(configured)
                        ? fallback
                        : Path.GetFullPath(configured.Trim());
                    Directory.CreateDirectory(directory);
                    return directory;
                }
                catch (Exception ex) when (!string.IsNullOrWhiteSpace(configured)
                    && ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    Directory.CreateDirectory(fallback);
                    ConfigData.Instance.ResultDirectory = string.Empty;
                    LocalLogger.Instance.Warn($"配置的结果目录不可用：{configured}。已改用：{fallback}");
                    return fallback;
                }
            }
        }

        public event Action<int>? StationScanConsumed;

        public void RecordStationStart(DateTime time, string stationName, int stationNo)
        {
            // Take the SN atomically, so the next scan cannot change this test's identity.
            string? serialNumber = ScanBatchService.Instance.TakeSerialNumber(stationNo, out var recipe);
            RecordStart(time, stationName, serialNumber ?? string.Empty, recipe?.RecipeName);
            StationScanConsumed?.Invoke(stationNo);
        }

        public void RecordStart(DateTime time, string stationName = "工位 1", string? serialNumber = null, string? formula = null)
        {
            lock (_syncRoot)
            {
                _tempRecords[stationName] = new ProcessData
                {
                    StationName = stationName,
                    SerialNumber = serialNumber ?? GetScannedSerialNumber(stationName),
                    FormulaHeader = formula,
                    StartTime = time
                };
            }
        }

        public void RecordEnd(DateTime endTime, double length, double maxForce, string result, string? formula = null, double? ctTime = null, string stationName = "工位 1", int stationNo = 1, bool uploadToMes = true)
        {
            ProcessData record;
            lock (_syncRoot)
            {
                if (!_tempRecords.TryGetValue(stationName, out record!))
                {
                    record = new ProcessData { StationName = stationName };
                }

                record.EndTime = endTime;
                record.MaxForce = maxForce;
                record.LengthAt100N = length;
                record.Result = result;
                record.FormulaHeader = string.IsNullOrWhiteSpace(formula) ? record.FormulaHeader ?? ScanBatchService.Instance.Recipe?.RecipeName : formula;
                record.SerialNumber ??= GetScannedSerialNumber(stationName);
                record.CtTime = ctTime ?? 0;
                ResumedProcessData.Add(record);
                _tempRecords.Remove(stationName);
            }

            try
            {
                AppendToExcel(record);
            }
            catch (Exception ex)
            {
                LocalLogger.Instance.Error($"[{stationName}] 保存测试结果 Excel 失败：{ex.Message}");
            }
            if (uploadToMes) MesUploadService.Instance.QueueUpload(record, stationNo);
        }

        // 结果在 RecordEnd 时已即时保存，保留此方法以兼容退出流程，避免重复写入。
        public void SaveRecord() { }

        public int ReadTodayCount(string folderPath, string? stationName = null)
        {
            if (string.IsNullOrWhiteSpace(stationName)) return 0;
            try
            {
                string filePath = GetExcelFilePath(folderPath, DateTime.Today, stationName);
                if (!File.Exists(filePath)) return 0;

                using var workbook = new XLWorkbook(filePath);
                string sheetName = DateTime.Today.ToString("yyyy-MM-dd");
                var worksheet = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name == sheetName)
                    ?? workbook.Worksheets.FirstOrDefault(sheet => sheet.Name == "测试结果");
                if (worksheet is null) return 0;

                int headerRow = worksheet.Cell(1, 1).GetString() == "总计测量根数" ? 2 : 1;
                return Math.Max(0, (worksheet.LastRowUsed()?.RowNumber() ?? headerRow) - headerRow);
            }
            catch (Exception ex)
            {
                LocalLogger.Instance.Warn($"[{stationName}] 读取当天 Excel 计数失败：{ex.Message}");
                return 0;
            }
        }

        private void AppendToExcel(ProcessData record)
        {
            string stationName = string.IsNullOrWhiteSpace(record.StationName) ? "工位 1" : record.StationName;
            string folderPath = ResultDirectory;
            Directory.CreateDirectory(folderPath);
            DateTime recordDate = record.EndTime ?? DateTime.Now;
            string filePath = GetExcelFilePath(folderPath, recordDate, stationName);

            using var workbook = File.Exists(filePath) ? new XLWorkbook(filePath) : new XLWorkbook();
            var worksheet = GetOrCreateResultWorksheet(workbook, recordDate);
            EnsureTargetLayout(worksheet);

            int row = Math.Max(2, worksheet.LastRowUsed()?.RowNumber() ?? 2) + 1;
            worksheet.Cell(row, 1).Value = record.FormulaHeader ?? string.Empty;
            if (string.IsNullOrWhiteSpace(record.SerialNumber))
                worksheet.Cell(row, 2).Clear(XLClearOptions.Contents);
            else
                worksheet.Cell(row, 2).Value = record.SerialNumber;
            worksheet.Cell(row, 3).Value = record.LengthAt100N;
            if (record.StartTime.HasValue) worksheet.Cell(row, 4).Value = record.StartTime.Value;
            worksheet.Cell(row, 5).Value = record.EndTime ?? DateTime.Now;
            worksheet.Cell(row, 6).Value = record.MaxForce;
            worksheet.Cell(row, 7).Value = record.CtTime;
            worksheet.Cell(row, 8).Value = record.Result ?? string.Empty;
            worksheet.Cell(row, 4).Style.DateFormat.Format = "yyyy-MM-dd HH:mm:ss";
            worksheet.Cell(row, 5).Style.DateFormat.Format = "yyyy-MM-dd HH:mm:ss";
            worksheet.Cell(1, 2).Value = row - 2;
            ApplyReadableColumnWidths(worksheet);

            workbook.SaveAs(filePath);
            LocalLogger.Instance.Info($"[{stationName}] 测试结果已保存到 Excel：{filePath}");
        }

        private static void ApplyReadableColumnWidths(IXLWorksheet worksheet)
        {
            worksheet.Columns(1, 8).AdjustToContents();
            double[] minimumWidths = { 16, 22, 15, 22, 22, 12, 15, 12 };
            for (int column = 1; column <= minimumWidths.Length; column++)
            {
                var excelColumn = worksheet.Column(column);
                excelColumn.Width = Math.Max(excelColumn.Width, minimumWidths[column - 1]);
            }
        }

        private static IXLWorksheet GetOrCreateResultWorksheet(XLWorkbook workbook, DateTime date)
        {
            string sheetName = date.ToString("yyyy-MM-dd");
            var datedSheet = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name == sheetName);
            if (datedSheet is not null) return datedSheet;

            var legacySheet = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name == "测试结果");
            if (legacySheet is not null)
            {
                legacySheet.Name = sheetName;
                return legacySheet;
            }

            return workbook.AddWorksheet(sheetName);
        }

        private static void EnsureTargetLayout(IXLWorksheet worksheet)
        {
            if (worksheet.LastRowUsed() is null)
            {
                WriteTargetHeaders(worksheet, 0);
                return;
            }

            if (worksheet.Cell(1, 1).GetString() == "总计测量根数") return;

            // 兼容升级前已经生成的“单行表头”文件：保留旧数据，无法追溯的
            // 开始时间留空，原“时间”作为结束时间。
            bool isLegacyLayout = worksheet.Cell(1, 1).GetString() == "配方名称"
                && worksheet.Cell(1, 4).GetString() == "时间";
            if (!isLegacyLayout)
                throw new InvalidDataException($"工作表“{worksheet.Name}”不是可识别的测试结果格式，已停止写入以避免覆盖数据。");

            int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
            for (int sourceRow = lastRow; sourceRow >= 2; sourceRow--)
            {
                int targetRow = sourceRow + 1;
                string rawRecipe = worksheet.Cell(sourceRow, 1).GetString();
                string recipe = SanitizeLegacyRecipeName(rawRecipe);
                string serialNumber = worksheet.Cell(sourceRow, 2).GetString();
                // 旧文件把空共享字符串误读成其 shared-string 索引 9；仅在同时
                // 命中旧版损坏配方特征时清空，避免把伪值当作真实 SN。
                bool hasLegacyNullPadding = rawRecipe.Contains('\0')
                    || rawRecipe.Contains("_x0000_", StringComparison.OrdinalIgnoreCase);
                if (serialNumber == "9" && hasLegacyNullPadding)
                    serialNumber = string.Empty;
                var result = worksheet.Cell(sourceRow, 3).Value;
                var endTime = worksheet.Cell(sourceRow, 4).Value;
                var maxForce = worksheet.Cell(sourceRow, 6).Value;
                var length = worksheet.Cell(sourceRow, 7).Value;
                var testTime = worksheet.Cell(sourceRow, 8).Value;

                worksheet.Cell(targetRow, 1).Value = recipe;
                if (string.IsNullOrEmpty(serialNumber))
                    worksheet.Cell(targetRow, 2).Clear(XLClearOptions.Contents);
                else
                    worksheet.Cell(targetRow, 2).Value = serialNumber;
                worksheet.Cell(targetRow, 3).Value = length;
                worksheet.Cell(targetRow, 4).Clear(XLClearOptions.Contents);
                worksheet.Cell(targetRow, 5).Value = endTime;
                worksheet.Cell(targetRow, 6).Value = maxForce;
                worksheet.Cell(targetRow, 7).Value = testTime;
                worksheet.Cell(targetRow, 8).Value = result;
                worksheet.Cell(targetRow, 5).Style.DateFormat.Format = "yyyy-MM-dd HH:mm:ss";
            }

            WriteTargetHeaders(worksheet, Math.Max(0, lastRow - 1));
        }

        private static void WriteTargetHeaders(IXLWorksheet worksheet, int count)
        {
            worksheet.Range(1, 1, 2, 8).Clear(XLClearOptions.Contents);
            worksheet.Cell(1, 1).Value = "总计测量根数";
            worksheet.Cell(1, 2).Value = count;
            string[] headers = { "配方名称", "SN码", "100N时长度", "开始时间", "结束时间", "最大拉力", "测试时间（s）", "运行结果" };
            for (int column = 0; column < headers.Length; column++)
                worksheet.Cell(2, column + 1).Value = headers[column];

            worksheet.Range(1, 1, 2, 8).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            worksheet.Range(1, 1, 2, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            worksheet.Range(1, 1, 1, 2).Style.Font.Bold = true;
            worksheet.Range(2, 1, 2, 8).Style.Font.Bold = true;
            worksheet.SheetView.FreezeRows(2);
        }

        private static string SanitizeLegacyRecipeName(string value)
        {
            int escapedNullIndex = value.IndexOf("_x0000_", StringComparison.OrdinalIgnoreCase);
            if (escapedNullIndex >= 0) value = value[..escapedNullIndex];

            int actualNullIndex = value.IndexOf('\0');
            if (actualNullIndex >= 0) value = value[..actualNullIndex];
            return value.Trim();
        }

        private static string GetExcelFilePath(string folderPath, DateTime date, string stationName) =>
            Path.Combine(folderPath, $"{date:yyyy-MM-dd}_{SanitizeFileName(stationName)}.xlsx");

        private static string SanitizeFileName(string name)
        {
            foreach (char invalidChar in Path.GetInvalidFileNameChars()) name = name.Replace(invalidChar, '_');
            return string.IsNullOrWhiteSpace(name) ? "工位1" : name;
        }

        private static string? GetScannedSerialNumber(string stationName)
        {
            string digits = new string(stationName.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out int stationNo) ? ScanBatchService.Instance.GetSerialNumber(stationNo) : null;
        }
    }
}
