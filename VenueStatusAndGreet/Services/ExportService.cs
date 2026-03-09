using ClosedXML.Excel;
using Dalamud.Plugin.Services;

namespace VenueStatusAndGreet.Services;

public sealed class ExportService
{
    private readonly DatabaseService database;
    private readonly IPluginLog log;

    public ExportService(DatabaseService database, IPluginLog log)
    {
        this.database = database;
        this.log = log;
    }

    public string ExportRangeToExcel(DateOnly fromInclusive, DateOnly toInclusive, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var fileName = $"venue-stats-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";
        var outputPath = Path.Combine(outputDirectory, fileName);

        var dailyRows = this.database.GetDailyStats(fromInclusive, toInclusive);
        var visitorRows = this.database.GetVisitorsForRange(fromInclusive, toInclusive);
        var sampleRows = this.database.GetSamplesForRange(fromInclusive, toInclusive);

        using var workbook = new XLWorkbook();

        var dailySheet = workbook.Worksheets.Add("DailyStats");
        dailySheet.Cell(1, 1).Value = "Date";
        dailySheet.Cell(1, 2).Value = "Max Guests";
        dailySheet.Cell(1, 3).Value = "Min Guests";
        dailySheet.Cell(1, 4).Value = "Unique Guests";
        dailySheet.Cell(1, 5).Value = "Total Visits";
        dailySheet.Cell(1, 6).Value = "Total Guest Time (hours)";
        for (var i = 0; i < dailyRows.Count; i++)
        {
            var row = dailyRows[i];
            var excelRow = i + 2;
            dailySheet.Cell(excelRow, 1).Value = row.NightDate.ToString("yyyy-MM-dd");
            dailySheet.Cell(excelRow, 2).Value = row.MaxGuests;
            dailySheet.Cell(excelRow, 3).Value = row.MinGuests;
            dailySheet.Cell(excelRow, 4).Value = row.UniqueGuests;
            dailySheet.Cell(excelRow, 5).Value = row.TotalVisits;
            dailySheet.Cell(excelRow, 6).Value = row.TotalGuestTime.TotalHours;
        }

        var visitorSheet = workbook.Worksheets.Add("Visitors");
        visitorSheet.Cell(1, 1).Value = "Date";
        visitorSheet.Cell(1, 2).Value = "Character";
        visitorSheet.Cell(1, 3).Value = "Home World";
        visitorSheet.Cell(1, 4).Value = "Visits";
        visitorSheet.Cell(1, 5).Value = "Total Time (minutes)";
        visitorSheet.Cell(1, 6).Value = "Greeted";
        for (var i = 0; i < visitorRows.Count; i++)
        {
            var row = visitorRows[i];
            var excelRow = i + 2;
            visitorSheet.Cell(excelRow, 1).Value = row.NightDate.ToString("yyyy-MM-dd");
            visitorSheet.Cell(excelRow, 2).Value = row.CharacterName;
            visitorSheet.Cell(excelRow, 3).Value = row.HomeWorld;
            visitorSheet.Cell(excelRow, 4).Value = row.Visits;
            visitorSheet.Cell(excelRow, 5).Value = row.TotalTime.TotalMinutes;
            visitorSheet.Cell(excelRow, 6).Value = row.Greeted ? "Yes" : "No";
        }

        var sampleSheet = workbook.Worksheets.Add("GuestSamples");
        sampleSheet.Cell(1, 1).Value = "Date";
        sampleSheet.Cell(1, 2).Value = "Sample Time";
        sampleSheet.Cell(1, 3).Value = "Guest Count";
        for (var i = 0; i < sampleRows.Count; i++)
        {
            var row = sampleRows[i];
            var excelRow = i + 2;
            sampleSheet.Cell(excelRow, 1).Value = row.NightDate.ToString("yyyy-MM-dd");
            sampleSheet.Cell(excelRow, 2).Value = row.SampleTimeLocal.ToString("yyyy-MM-dd HH:mm");
            sampleSheet.Cell(excelRow, 3).Value = row.GuestCount;
        }

        foreach (var worksheet in workbook.Worksheets)
        {
            worksheet.Columns().AdjustToContents();
        }

        workbook.SaveAs(outputPath);
        this.log.Information($"Exported venue analytics to {outputPath}");
        return outputPath;
    }
}
