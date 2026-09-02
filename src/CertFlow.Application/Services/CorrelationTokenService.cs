using System.Security.Cryptography;
using System.Text;

namespace CertFlow.Application.Services;

public class CorrelationTokenService
{
    public string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(9))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');   // URL-safe 12 chars

    public string BuildIdempotencyKey(string candidateId, Guid appointmentId) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{candidateId}:{appointmentId}")));
}
