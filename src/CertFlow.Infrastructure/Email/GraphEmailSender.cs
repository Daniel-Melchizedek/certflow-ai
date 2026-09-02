using CertFlow.Application.Interfaces;
using Microsoft.Graph;
using Microsoft.Graph.Models;
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
            SaveToSentItems = false
        };

        await graphClient.Users[sharedMailboxEmail].SendMail.PostAsync(body, cancellationToken: ct);
    }
}
