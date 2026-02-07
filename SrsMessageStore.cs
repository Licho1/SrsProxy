using System.Buffers;
using DnsClient;
using MimeKit;
using MimeKit.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using MailKitClient = MailKit.Net.Smtp.SmtpClient;

namespace SrsProxy;

public sealed class SrsMessageStore(IConfiguration config, ILogger<SrsMessageStore> logger) : MessageStore
{
    readonly string _srsDomain = config["SrsProxy:SrsDomain"] ?? "zero-k.info";
    readonly string _srsSecret = config["SrsProxy:SrsSecret"] ?? throw new InvalidOperationException("SrsProxy:SrsSecret is required");
    readonly HashSet<string> _localDomains = [.. (config.GetSection("SrsProxy:LocalDomains").Get<string[]>() ?? [])];

    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context,
        IMessageTransaction transaction,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken)
    {
        var sender = transaction.From.AsAddress();
        var recipients = transaction.To.Select(t => t.AsAddress()).ToList();

        logger.LogInformation("Received message from {Sender} to {Recipients}", sender, string.Join(", ", recipients));

        // SRS-rewrite envelope sender if not a local domain
        var envelopeSender = sender;
        var senderDomain = transaction.From.Host;

        if (!string.IsNullOrEmpty(sender) && !_localDomains.Contains(senderDomain.ToLowerInvariant()))
        {
            envelopeSender = SrsRewriter.SrsForward(sender, _srsDomain, _srsSecret);
            logger.LogInformation("SRS rewrite: {Original} -> {Rewritten}", sender, envelopeSender);
        }
        else
        {
            logger.LogInformation("No SRS rewrite needed for local domain sender: {Sender}", sender);
        }

        // Load message from buffer
        using var stream = new MemoryStream();
        foreach (var segment in buffer)
            stream.Write(segment.Span);
        stream.Position = 0;

        var message = await MimeMessage.LoadAsync(stream, cancellationToken);

        // Ensure Message-ID exists (Gmail rejects without one)
        if (string.IsNullOrEmpty(message.MessageId))
            message.MessageId = MimeUtils.GenerateMessageId();

        // Relay to each recipient's MX
        var senderMailbox = string.IsNullOrEmpty(envelopeSender) ? null : MailboxAddress.Parse(envelopeSender);
        var byDomain = recipients.GroupBy(r => r.Split('@')[1].ToLowerInvariant());

        try
        {
            var dns = new LookupClient();
            foreach (var group in byDomain)
            {
                var mxHost = await ResolveMx(dns, group.Key);
                logger.LogInformation("Resolved MX for {Domain}: {MxHost}", group.Key, mxHost);

                using var client = new MailKitClient();
                await client.ConnectAsync(mxHost, 25, MailKit.Security.SecureSocketOptions.StartTls, cancellationToken);

                var recipientMailboxes = group.Select(r => MailboxAddress.Parse(r)).ToList();
                await client.SendAsync(message, senderMailbox!, recipientMailboxes, cancellationToken);
                await client.DisconnectAsync(true, cancellationToken);
            }

            logger.LogInformation("Message relayed successfully");
            return SmtpResponse.Ok;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to relay message");
            return new SmtpResponse(SmtpReplyCode.TransactionFailed, ex.Message);
        }
    }

    static async Task<string> ResolveMx(LookupClient dns, string domain)
    {
        var result = await dns.QueryAsync(domain, QueryType.MX);
        var mx = result.Answers.MxRecords().OrderBy(r => r.Preference).FirstOrDefault();
        return mx?.Exchange.Value?.TrimEnd('.') ?? domain;
    }
}
