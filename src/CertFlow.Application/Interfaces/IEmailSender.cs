namespace CertFlow.Application.Interfaces;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);

    // Sends as a threaded reply to an existing Graph message. The subject is left to Graph
    // ("RE: <original>") so Outlook keeps the conversation together — correlation travels in
    // the body as [REF:token].
    Task ReplyAsync(string originalGraphMessageId, string htmlBody, CancellationToken ct = default);
}
