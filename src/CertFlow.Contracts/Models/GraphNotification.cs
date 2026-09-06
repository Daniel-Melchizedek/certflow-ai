namespace CertFlow.Contracts.Models;

/// <summary>
/// A Graph change notification parked by the webhook for the Worker to process. Carries only
/// the message id — the webhook must return within three seconds, so the Graph lookup that
/// turns this into an <see cref="EmailMessage"/> happens off the request path.
/// </summary>
public record GraphNotification(
    string MessageId,
    string? SubscriptionId);
