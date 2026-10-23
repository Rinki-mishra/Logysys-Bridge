using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace LogisysOrgConverter.Notifications;

public sealed record DeliveryResult(string Delivery, bool Succeeded, string? Error);

public sealed class MailDelivery
{
    private readonly EmailOptions _options;
    private readonly string _pickupFolder;

    public MailDelivery(IOptions<EmailOptions> options, IWebHostEnvironment environment)
    {
        _options = options.Value;
        _pickupFolder = Path.IsPathRooted(_options.PickupFolder)
            ? _options.PickupFolder
            : Path.Combine(environment.ContentRootPath, _options.PickupFolder);
    }

    public bool UsesSmtp => _options.UsesSmtp;

    public string PickupFolder => _pickupFolder;

    public async Task<DeliveryResult> SendAsync(EmailDraft draft, CancellationToken cancellationToken)
    {
        var delivery = _options.UsesSmtp ? "SMTP" : "File";
        try
        {
            var message = Build(draft);
            if (_options.UsesSmtp)
            {
                using var client = new SmtpClient();
                var security = _options.Smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
                await client.ConnectAsync(_options.Smtp.Host, _options.Smtp.Port, security, cancellationToken);
                if (!string.IsNullOrWhiteSpace(_options.Smtp.UserName))
                {
                    await client.AuthenticateAsync(_options.Smtp.UserName, _options.Smtp.Password, cancellationToken);
                }

                await client.SendAsync(message, cancellationToken);
                await client.DisconnectAsync(true, cancellationToken);
            }
            else
            {
                Directory.CreateDirectory(_pickupFolder);
                var safe = string.Concat(draft.RecipientEmail.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' ? ch : '_'));
                var path = Path.Combine(_pickupFolder, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safe}-{Guid.NewGuid():N}.eml");
                await message.WriteToAsync(path, cancellationToken);
            }

            return new DeliveryResult(delivery, true, null);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(delivery, false, ex.Message);
        }
    }

    private MimeMessage Build(EmailDraft draft)
    {
        var from = string.IsNullOrWhiteSpace(_options.FromAddress) ? "organisation-master@localhost" : _options.FromAddress.Trim();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, from));
        message.To.Add(new MailboxAddress(draft.RecipientName ?? string.Empty, draft.RecipientEmail));
        foreach (var cc in _options.Cc.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            message.Cc.Add(MailboxAddress.Parse(cc));
        }

        message.Subject = draft.Subject;
        message.Body = new TextPart("plain") { Text = draft.Body };
        return message;
    }
}
