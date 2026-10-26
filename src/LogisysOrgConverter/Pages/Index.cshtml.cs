using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Data;
using LogisysOrgConverter.GoogleDrive;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LogisysOrgConverter.Pages;

public class IndexModel : PageModel
{
    private readonly ConversionStore _store;
    private readonly GoogleDriveAccess _drive;

    public IndexModel(ConversionStore store, GoogleDriveAccess drive)
    {
        _store = store;
        _drive = drive;
    }

    public string? DriveAccount { get; private set; }

    [BindProperty]
    public IFormFile? ClientFile { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadDriveAccountAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await LoadDriveAccountAsync(cancellationToken);
        if (ClientFile is null || ClientFile.Length == 0)
        {
            ErrorMessage = "Choose the client workbook first.";
            return Page();
        }

        if (!Path.GetExtension(ClientFile.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "The client file must be an .xlsx workbook.";
            return Page();
        }

        try
        {
            await using var stream = ClientFile.OpenReadStream();
            var id = await _store.ConvertAsync(ClientFile.FileName, stream, cancellationToken);
            return RedirectToPage("/Runs/Detail", new { id });
        }
        catch (ClientSheetException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }

    private async Task LoadDriveAccountAsync(CancellationToken cancellationToken)
    {
        if (_drive.IsConfigured)
        {
            DriveAccount = (await _drive.GetConnectionAsync(cancellationToken))?.Email;
        }
    }
}
