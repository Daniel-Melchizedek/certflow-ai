using CertFlow.Application.Interfaces;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.Messages.Item.Reply;
using Microsoft.Graph.Users.Item.SendMail;

namespace CertFlow.Infrastructure.Email;

public class GraphEmailSender(GraphServiceClient graphClient, string sharedMailboxEmail) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var body = new SendMailPostRequestBody
        {
            Message = new Message
            {
                Subject = subject,
                Body = new ItemBody { ContentType = BodyType.Html, Content = htmlBody },
                ToRecipients = [new Recipient { EmailAddress = new EmailAddress { Address = to } }]
            },
            SaveToSentItems = true
        };

        await graphClient.Users[sharedMailboxEmail].SendMail.PostAsync(body, cancellationToken: ct);
    }

    public async Task ReplyAsync(string originalGraphMessageId, string htmlBody, CancellationToken ct = default)
    {
        // Deliberately does not set Message.Subject. Exchange derives ConversationTopic from
        // the subject, so overriding it with our own text starts a brand new conversation and
        // Outlook shows the reply as an unrelated message. Letting Graph produce the natural
        // "RE: <original>" keeps the thread intact; the [REF:token] correlation lives in the
        // body instead, which the reply consumers scan.
        await graphClient.Users[sharedMailboxEmail]
            .Messages[originalGraphMessageId]
            .Reply
            .PostAsync(new ReplyPostRequestBody
            {
                Message = new Message
                {
                    Body = new ItemBody { ContentType = BodyType.Html, Content = htmlBody }
                }
            }, cancellationToken: ct);
    }
}
