using System.Collections.Concurrent;
using LogisysOrgConverter.Conversion;

namespace LogisysOrgConverter.GoogleDrive;

public sealed class GstCertificateReader
{
    private readonly GoogleDriveAccess _drive;
    private readonly GoogleVisitorAccess _visitor;

    public GstCertificateReader(GoogleDriveAccess drive, GoogleVisitorAccess visitor)
    {
        _drive = drive;
        _visitor = visitor;
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

        if (await UsesGoogleSignInAsync(cancellationToken))
        {
            try
            {
                await _drive.GetAccessTokenAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is GoogleDriveException or HttpRequestException or TaskCanceledException)
            {
                return FailAll(links.Keys, ex is GoogleDriveException ? ex.Message : "Google could not be reached");
            }

            await Parallel.ForEachAsync(
                links,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (item, token) => results[item.Key] = await ReadAsync(item.Key, item.Value, _drive.DownloadAsync, token));
            return results;
        }

        if (!_visitor.IsConfigured)
        {
            return FailAll(links.Keys, "Google Drive is not connected; see the Google Drive page");
        }

        GoogleVisitorSession session;
        try
        {
            session = await _visitor.OpenAsync(cancellationToken);
        }
        catch (GoogleDriveException ex)
        {
            return FailAll(links.Keys, ex.Message);
        }

        await using (session)
        {
            foreach (var (link, pan) in links)
            {
                results[link] = await ReadAsync(link, pan, session.DownloadAsync, cancellationToken);
            }
        }

        return results;
    }

    public async Task<GstCertificate> ReadOneAsync(string link, string? pan, CancellationToken cancellationToken)
    {
        if (await UsesGoogleSignInAsync(cancellationToken))
        {
            return await ReadAsync(link, pan, _drive.DownloadAsync, cancellationToken);
        }

        if (!_visitor.IsConfigured)
        {
            return GstCertificate.Failed("Google Drive is not connected; see the Google Drive page");
        }

        try
        {
            await using var session = await _visitor.OpenAsync(cancellationToken);
            return await ReadAsync(link, pan, session.DownloadAsync, cancellationToken);
        }
        catch (GoogleDriveException ex)
        {
            return GstCertificate.Failed(ex.Message);
        }
    }

    private async Task<bool> UsesGoogleSignInAsync(CancellationToken cancellationToken) =>
        _drive.IsConfigured && await _drive.GetConnectionAsync(cancellationToken) is not null;

    private static async Task<GstCertificate> ReadAsync(
        string link,
        string? pan,
        Func<string, CancellationToken, Task<DriveFile>> download,
        CancellationToken cancellationToken)
    {
        if (GoogleDriveAccess.FileId(link) is not { } fileId)
        {
            return GstCertificate.Failed("the GST document column is not a Google Drive link");
        }

        try
        {
            var file = await download(fileId, cancellationToken);
            return GstinReader.FromFile(file.Content, file.MimeType, pan);
        }
        catch (GoogleDriveException ex)
        {
            return GstCertificate.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or Microsoft.Playwright.PlaywrightException)
        {
            return GstCertificate.Failed("Google Drive could not be reached");
        }
    }

    private static ConcurrentDictionary<string, GstCertificate> FailAll(IEnumerable<string> links, string problem) =>
        new(links.Select(link => KeyValuePair.Create(link, GstCertificate.Failed(problem))), StringComparer.Ordinal);
}
