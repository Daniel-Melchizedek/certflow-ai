using Azure.AI.Projects;
using Microsoft.Extensions.Logging;

namespace CertFlow.Agent;

/// <summary>
/// Verifies the Azure AI Foundry project connection is live and the gpt-4o deployment is reachable.
/// Called from deploy.ps1 Step 10 via POST /admin/agents/register.
/// Agent logic (system prompts + tools) is defined in AgentOrchestrator — no separate Foundry
/// agent objects are created; the Chat Completions API provides the tool-calling capability directly.
/// </summary>
public class AgentRegistrationService(AIProjectClient projectClient, ILogger<AgentRegistrationService> logger)
{
    public async Task RegisterAllAsync(CancellationToken ct = default)
    {
        // Verify the project has a gpt-4o deployment available
        var deployments = projectClient.Deployments;
        bool found = false;
        await foreach (var deployment in deployments.GetDeploymentsAsync(cancellationToken: ct))
        {
            if (deployment.Name.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                logger.LogInformation("Found gpt-4o deployment: {Name}", deployment.Name);
                break;
            }
        }

        if (!found)
            logger.LogWarning("No gpt-4o deployment found. Ensure gpt-4o is deployed in the AI Foundry project.");
        else
            logger.LogInformation("CertFlow agents are configured and ready (Intent + Policy via Chat Completions API).");
    }
}
