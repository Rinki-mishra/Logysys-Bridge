using System.Collections.Concurrent;
using LogisysOrgConverter.Conversion;

namespace LogisysOrgConverter.GoogleDrive;

public sealed class GstCertificateReader
{
    private readonly GoogleDriveAccess _drive;

    public GstCertificateReader(GoogleDriveAccess drive)
    {
        _drive = drive;
    }

    // Keyed by the exact link in the GST document column, which is how the converter looks them up.
    public async Task<IReadOnlyDictionary<string, GstCertificate>> ReadAsync(
        IReadOnlyList<ClientRow> rows,
        CancellationToken cancellationToken = default)
    {
        var links = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Cells.TryGetValue(OrganisationConverter.GstDocumentColumn, out var link) && !string.IsNullOrWhiteSpace(link))
            {
                row.Cells.TryGetValue(OrganisationConverter.PanColumn, out var pan);
                links.TryAdd(link.Trim(), pan);
            }
        }

        var results = new ConcurrentDictionary<string, GstCertificate>(StringComparer.Ordinal);
        if (links.Count == 0)
        {
            return results;
        }

        try
        {
            await _drive.GetAccessTokenAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is GoogleDriveException or HttpRequestException or TaskCanceledException)
        {
            var problem = ex is GoogleDriveException ? ex.Message : "Google could not be reached";
            foreach (var link in links.Keys)
            {
                results[link] = GstCertificate.Failed(problem);
            }

            return results;
        }

        await Parallel.ForEachAsync(
            links,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (item, token) => results[item.Key] = await ReadOneAsync(item.Key, item.Value, token));
        return results;
    }

    public async Task<GstCertificate> ReadOneAsync(string link, string? pan, CancellationToken cancellationToken)
    {
        if (GoogleDriveAccess.FileId(link) is not { } fileId)
        {
            return GstCertificate.Failed("the GST document column is not a Google Drive link");
        }

        try
        {
            var file = await _drive.DownloadAsync(fileId, cancellationToken);
            return GstinReader.FromFile(file.Content, file.MimeType, pan);
        }
        catch (GoogleDriveException ex)
        {
            return GstCertificate.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return GstCertificate.Failed("Google Drive could not be reached");
        }
    }
}
