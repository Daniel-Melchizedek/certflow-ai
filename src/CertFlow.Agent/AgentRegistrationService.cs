using Microsoft.Extensions.Logging;

namespace CertFlow.Agent;

/// <summary>
/// Creates the three CertFlow agents in Azure AI Foundry so they appear in the portal.
/// Called from POST /admin/agents/register (deploy.ps1 Step 10).
/// Uses AgentOrchestrator.EnsureAllAgentsRegisteredAsync which is the single source of
/// truth for all agent definitions (system prompts + tool schemas).
/// </summary>
public class AgentRegistrationService(
    AgentOrchestrator orchestrator,
    ILogger<AgentRegistrationService> logger)
{
    public async Task RegisterAllAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Registering CertFlow agents in Azure AI Foundry...");
        await orchestrator.EnsureAllAgentsRegisteredAsync(ct);
        logger.LogInformation(
            "Agents registered: {Intent}, {Policy}, {Confirmation}",
            AgentOrchestrator.IntentAgentName,
            AgentOrchestrator.PolicyAgentName,
            AgentOrchestrator.ConfirmationAgentName);
    }
}
