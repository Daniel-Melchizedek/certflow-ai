param location string
param accountName string
param projectName string
param managedIdentityPrincipalId string

// Azure AI Foundry resource (new model). Azure.AI.Projects >= 1.0.0-beta.9 talks only to
// this resource type via a https://<account>.services.ai.azure.com/api/projects/<project>
// endpoint. The older MachineLearningServices Hub/Project pair has no project endpoint
// and is not supported by the SDK version this solution uses.
resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: accountName
  location: location
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    // Both are load-bearing: allowProjectManagement enables the projects child resource,
    // customSubDomainName produces the services.ai.azure.com hostname the SDK requires.
    allowProjectManagement: true
    customSubDomainName: accountName
    publicNetworkAccess: 'Enabled'
  }
}

// Model deployments live on the Foundry account itself — no separate Azure OpenAI
// account or workspace connection is needed.
resource gpt4oDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: account
  name: 'gpt-4o'
  // 50k TPM. Measured usage is far below this — a three-exam bulk proposal peaked at 8,565
  // tokens in a minute, plus 2,769 for the confirmation reply — but the headroom is deliberate
  // and free: GlobalStandard bills per token consumed, not per unit reserved, so a higher ceiling
  // costs nothing when idle.
  //
  // Do not trim this to fit the measured peak. 10k TPM failed even though 8,565 fits inside it,
  // because the limit is enforced over a sliding window of seconds rather than a per-minute
  // bucket: the burst lands in roughly 35s, i.e. ~14.7k tokens/min while in flight. Retries make
  // it worse — a 429 mid-run makes Service Bus redeliver, and each redelivery re-runs the intent
  // agent, so throttling sustains itself until the message dead-letters.
  sku: { name: 'GlobalStandard', capacity: 50 }
  properties: {
    model: { format: 'OpenAI', name: 'gpt-4o', version: '2024-11-20' }
  }
}

// Separate low-cost judge deployment for evaluation runs — not used by agents.
// gpt-4o-mini 2024-07-18 is deprecated for new deployments in australiaeast (as of Sep 2026);
// gpt-5-nano is the cheapest GA model available. Must be declared after gpt4oDeployment:
// ARM enforces serial model deployment.
resource judgeDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: account
  name: 'gpt-5-nano'
  sku: { name: 'GlobalStandard', capacity: 30 }
  properties: {
    model: { format: 'OpenAI', name: 'gpt-5-nano', version: '2025-08-07' }
  }
  dependsOn: [gpt4oDeployment]
}

// Agent-level guardrail. Distinct from the model deployments' filter (Microsoft.DefaultV2, applied
// by default): an agent with no policy of its own inherits only the model's, which cannot see the
// agent-specific attack surface. The three filters that justify this policy existing are
// Indirect Attack and Indirect Attack Spotlighting — inbound candidate email is untrusted text
// interpolated into the prompt, the textbook indirect-injection vector — and Task Adherence, which
// evaluates PreToolCall and so guards confirm_reschedule_slot against an injected booking.
//
// Harm categories sit at Medium rather than Low deliberately. Low is the most aggressive tier and
// these four are already covered at the model layer, so Low buys little and risks rejecting
// legitimate mail: a candidate writing that they have been unwell and depressed can trip Selfharm
// at Low, which would silently kill a valid reschedule request.
//
// PII filters (Email Protection, Name Protection) are deliberately NOT enabled — the agents pass
// candidate names and email addresses to get_candidate_context as a matter of course.
//
// The deployed policy reads back with 15 filters rather than the 13 below: the service injects a
// Purview Prompt/Completion pair from the base policy. That is expected, not drift.
resource agentGuardrail 'Microsoft.CognitiveServices/accounts/raiPolicies@2025-06-01' = {
  parent: account
  name: 'ExamOpsAgentGuardrail'
  properties: {
    mode: 'Blocking'
    basePolicyName: 'Microsoft.DefaultV2'
    contentFilters: [
      { name: 'Hate',     source: 'Prompt',     severityThreshold: 'Medium', blocking: true, enabled: true }
      { name: 'Hate',     source: 'Completion', severityThreshold: 'Medium', blocking: true, enabled: true }
      { name: 'Sexual',   source: 'Prompt',     severityThreshold: 'Medium', blocking: true, enabled: true }
      { name: 'Sexual',   source: 'Completion', severityThreshold: 'Medium', blocking: true, enabled: true }
      { name: 'Violence', source: 'Prompt',     severityThreshold: 'Medium', blocking: true, enabled: true }
      { name: 'Violence', source: 'Completion', severityThreshold: 'Medium', blocking: true, enabled: true }
      { name: 'Selfharm', source: 'Prompt',     severityThreshold: 'Medium', blocking: true, enabled: true }
      { name: 'Selfharm', source: 'Completion', severityThreshold: 'Medium', blocking: true, enabled: true }
      { name: 'Jailbreak',                    source: 'Prompt',      blocking: true, enabled: true }
      { name: 'Indirect Attack',              source: 'Prompt',      blocking: true, enabled: true }
      { name: 'Indirect Attack Spotlighting', source: 'Prompt',      blocking: true, enabled: true }
      { name: 'Task Adherence',               source: 'PreToolCall', blocking: true, enabled: true }
      { name: 'Protected Material Text',      source: 'Completion',  blocking: true, enabled: true }
    ]
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: account
  name: projectName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    displayName: 'CertFlow AI Project'
    description: 'Intent & Identity + Policy & Scheduling agents for CertFlow AI'
  }
  dependsOn: [gpt4oDeployment, judgeDeployment]
}

// Azure AI User — lets the Managed Identity call agents and model deployments.
resource foundryUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, managedIdentityPrincipalId, '53ca6127-db72-4b80-b1b0-d745d6d5456d')
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Cognitive Services OpenAI User — required for the chat completions data plane.
resource openAIUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, managedIdentityPrincipalId, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output projectEndpoint string = 'https://${accountName}.services.ai.azure.com/api/projects/${projectName}'
// Data-plane host for chat completions against the account's own deployments.
output openAiEndpoint string = 'https://${accountName}.openai.azure.com/'
output accountEndpoint string = account.properties.endpoint
// Surfaced so the deploy script can address the project's connections ARM path, which needs the
// account and project names separately rather than the composed endpoint URL.
output accountName string = accountName
output projectName string = projectName
output accountId string = account.id
output judgeModelDeploymentName string = judgeDeployment.name
// Full ARM resource id, not the bare name. Foundry accepts a bare policy name on an agent without
// error and then applies no filtering at all, so the id is what must reach the agent definition.
output agentRaiPolicyId string = agentGuardrail.id
