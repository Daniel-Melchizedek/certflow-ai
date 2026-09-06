using System.Security.Cryptography;
using System.Text;

namespace CertFlow.Application.Services;

public class CorrelationTokenService
{
    public string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(9))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');   // URL-safe 12 chars

    /// <summary>
    /// Guards against the same inbound email being handled twice (Service Bus redelivery or a
    /// duplicate Graph notification). <paramref name="sourceMessageId"/> is what makes that
    /// specific — without it the key is constant per candidate+appointment, so a candidate can
    /// only ever reschedule a given exam once and every later request is dropped as a duplicate.
    /// </summary>
    public string BuildIdempotencyKey(string candidateId, Guid appointmentId, string sourceMessageId) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{candidateId}:{appointmentId}:{sourceMessageId}")));

    /// <summary>
    /// Key for the inbound message itself, claimed before any agent runs. Keyed on sender and
    /// message id only — at that point nothing has been parsed yet, so no appointment is known,
    /// and every outcome (proposal, bulk proposal, or a request for more detail) must dedupe.
    /// </summary>
    public string BuildMessageKey(string senderEmail, string sourceMessageId) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"msg:{senderEmail}:{sourceMessageId}")));
}
