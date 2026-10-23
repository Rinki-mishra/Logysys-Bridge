using LogisysOrgConverter.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LogisysOrgConverter.Pages.Runs;

public class DetailModel : PageModel
{
    private readonly ConversionStore _store;

    public DetailModel(ConversionStore store)
    {
        _store = store;
    }

    public ConversionRun? Run { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Run = await _store.GetRunAsync(id, cancellationToken);
        if (Run is null)
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnGetFileAsync(Guid id, string which, CancellationToken cancellationToken)
    {
        var run = await _store.GetRunAsync(id, cancellationToken);
        if (run is null)
        {
            return NotFound();
        }

        var india = string.Equals(which, "india", StringComparison.OrdinalIgnoreCase);
        var bytes = india ? run.IndiaFile : run.OverseasFile;
        if (bytes is null || bytes.Length == 0)
        {
            return NotFound();
        }

        var stamp = IndiaTime(run.CreatedAtUtc).ToString("yyyyMMdd-HHmm");
        var name = india ? $"OrgMaster-IN-{stamp}.xlsx" : $"OrgMaster-Overseas-{stamp}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }

    public static DateTime IndiaTime(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), IndiaZone);

    private static readonly TimeZoneInfo IndiaZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
}
