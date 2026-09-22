namespace CertFlow.Portal.Blazor;

/// <summary>
/// The scope the portal requests so the Slot Advisor can reach Work IQ Calendar as the signed-in
/// candidate. Held in one place because it is needed both where token acquisition is configured and
/// where the token is acquired — when those two drifted apart, every chat turn failed with a 401
/// that pointed at the audience rather than at the mismatch.
/// </summary>
internal static class FoundryAuth
{
    internal const string Scope = "https://ai.azure.com/.default";
}
