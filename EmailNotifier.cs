// Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.

using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace ExchangeAppPoolMonitor;

/// <summary>
/// Sends the notification emails, using the "Email" section of monitorsettings.json.
/// </summary>
/// <remarks>
/// Uses the built in System.Net.Mail.SmtpClient. Microsoft recommends MailKit for new code, but
/// SmtpClient is fine for sending plain text through an Exchange receive connector, and using it
/// keeps the service free of extra dependencies.
/// The settings are read on every send, so recipient changes apply without a restart.
/// </remarks>
public sealed class EmailNotifier
{
    private readonly IOptionsMonitor<EmailSettings> _settings;
    private readonly ActionLog _log;

    public EmailNotifier(IOptionsMonitor<EmailSettings> settings, ActionLog log)
    {
        _settings = settings;
        _log = log;
    }

    /// <summary>
    /// Sends a plain text email to every address in Email:To.
    /// Never throws: a mail failure must not stop the monitor, so failures go to the action log
    /// instead (which also means the status page shows them).
    /// </summary>
    public async Task SendAsync(string subject, string body, string? pool, CancellationToken ct)
    {
        var s = _settings.CurrentValue;
        var to = s.To.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        // Email turned off or not set up: nothing to do. Not an error, so nothing is logged.
        if (!s.Enabled || to.Count == 0 || string.IsNullOrWhiteSpace(s.From))
            return;

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(s.From),
                Subject = $"{s.SubjectPrefix} {subject}".Trim(),
                Body = body,
                IsBodyHtml = false,
            };
            foreach (var address in to) message.To.Add(address);

            using var client = new SmtpClient(s.SmtpServer, s.Port)
            {
                // Comes from the settings on purpose: the usual setup is the Exchange server's own
                // anonymous relay on port 25, which does not use TLS. Set Email:EnableSsl to true
                // when sending through an authenticated server (for example port 587).
                EnableSsl = s.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 30_000, // milliseconds; keeps a dead mail server from stalling the monitor loop for long
            };
            // No username means an anonymous relay, such as the server's own receive connector.
            if (!string.IsNullOrEmpty(s.Username))
                client.Credentials = new NetworkCredential(s.Username, s.Password);

            await client.SendMailAsync(message, ct);
            _log.Info($"Email sent to {string.Join(", ", to)}: {message.Subject}", pool);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"Email notification failed: {ex.Message}", pool);
        }
    }
}
