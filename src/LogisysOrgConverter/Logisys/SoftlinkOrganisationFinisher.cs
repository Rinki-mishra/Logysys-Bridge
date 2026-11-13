using LogisysOrgConverter.Data;

namespace LogisysOrgConverter.Logisys;

public sealed record SoftlinkFinishSummary(string Message, CodMarkSummary Cod, NoteUploadSummary Notes);

// After Softlink Excel import + Mark Created: tick Cash On Delivery, then attach Drive docs to Notes.
public sealed class SoftlinkOrganisationFinisher
{
    private readonly OrganisationCodMarker _cod;
    private readonly OrganisationNoteUploader _notes;

    public SoftlinkOrganisationFinisher(OrganisationCodMarker cod, OrganisationNoteUploader notes)
    {
        _cod = cod;
        _notes = notes;
    }

    public async Task<SoftlinkFinishSummary> FinishAsync(Guid runId, string? onlyName, CancellationToken cancellationToken)
    {
        var cod = await _cod.MarkAsync(runId, onlyName, cancellationToken);
        var notes = await _notes.AttachAsync(runId, onlyName, cancellationToken);
        var message =
            $"Finished Softlink setup for new organisations. {cod.Message} {notes.Message}";
        return new SoftlinkFinishSummary(message, cod, notes);
    }
}
