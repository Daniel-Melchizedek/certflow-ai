using Azure.AI.ContentSafety;
using Microsoft.Extensions.Logging;

namespace CertFlow.Agent;

/// <summary>
/// Wraps Azure AI Content Safety Prompt Shield to block prompt injection attacks
/// before untrusted input reaches any agent. Guards against:
///   - User prompt attacks (candidate replies, chat messages attempting to override instructions)
///   - Indirect document attacks (inbound email bodies containing embedded instructions)
/// </summary>
public class ContentSafetyGuard(ContentSafetyClient client, ILogger<ContentSafetyGuard> logger)
{
    /// <summary>
    /// Returns true when the input is safe to pass to an agent.
    /// </summary>
    /// <param name="userPrompt">The user-controlled instruction text (user prompt attack surface).</param>
    /// <param name="documents">Untrusted documents embedded in the request (indirect attack surface).</param>
    public async Task<bool> IsInputSafeAsync(
        string userPrompt,
        IEnumerable<string>? documents = null,
        CancellationToken ct = default)
    {
        var options = new ShieldPromptOptions { UserPrompt = userPrompt };
        foreach (var doc in documents ?? [])
            options.Documents.Add(doc);

        ShieldPromptResult result = (await client.ShieldPromptAsync(options, ct)).Value;

        if (result.UserPromptAnalysis?.AttackDetected == true)
        {
            logger.LogWarning("Prompt Shield detected a user prompt attack.");
            return false;
        }

        if (result.DocumentsAnalysis?.Any(d => d.AttackDetected) == true)
        {
            logger.LogWarning("Prompt Shield detected an indirect document attack.");
            return false;
        }

        return true;
    }
}
