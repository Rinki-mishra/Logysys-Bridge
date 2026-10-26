using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LogisysOrgConverter.Pages.Existing;

public class IndexModel : PageModel
{
    private const int ShowLimit = 200;

    private readonly ExistingOrganisationStore _store;

    public IndexModel(ExistingOrganisationStore store)
    {
        _store = store;
    }

    public int Count { get; private set; }

    public DateTime? LastAddedUtc { get; private set; }

    public IReadOnlyList<ExistingOrganisation> Organisations { get; private set; } = [];

    public int Limit => ShowLimit;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [TempData]
    public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Count = await _store.CountAsync(cancellationToken);
        LastAddedUtc = await _store.LastAddedAsync(cancellationToken);
        Organisations = await _store.SearchAsync(Q, ShowLimit, cancellationToken);
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? list, CancellationToken cancellationToken)
    {
        if (list is null || list.Length == 0)
        {
            Message = "Choose the organisation list exported from Logi-Sys.";
            return RedirectToPage();
        }

        try
        {
            await using var stream = list.OpenReadStream();
            var names = OrganisationListReader.Read(stream, list.FileName);
            if (names.Count == 0)
            {
                Message = "No organisation names were found. The file needs a Name column, or names in column A.";
                return RedirectToPage();
            }

            var saved = await _store.ReplaceAsync(names, cancellationToken);
            Message = $"Saved {saved} organisations from {Path.GetFileName(list.FileName)}. They will not be created again.";
        }
        catch (ClientSheetException ex)
        {
            Message = ex.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddAsync(string? name, CancellationToken cancellationToken)
    {
        var text = name?.Trim() ?? string.Empty;
        Message = text.Length == 0
            ? "Enter the organisation name."
            : await _store.AddAsync(text, cancellationToken)
                ? $"Added {text}."
                : $"{text} is already on the list.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id, CancellationToken cancellationToken)
    {
        await _store.RemoveAsync(id, cancellationToken);
        Message = "Removed.";
        return RedirectToPage(new { Q });
    }
}
