#Requires -Version 7.0
<#
.SYNOPSIS
  CertFlow AI — idempotent end-to-end deploy/redeploy script.
  Run this after every teardown to fully recreate Azure infrastructure and redeploy.

.PARAMETER SqlAdminPassword
  SQL server admin password (prompted if not supplied).

.PARAMETER ResourceGroupName
  Azure resource group name. Default: certflow-rg

.PARAMETER Location
  Azure region. Default: eastus

.PARAMETER SkipBuild
  Skip Docker image build (use existing images in ACR).
#>
param(
    [string]$ResourceGroupName = "certflow-rg",
    [string]$Location = "eastus",
    [switch]$SkipBuild,
    [SecureString]$SqlAdminPassword
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([int]$n, [string]$msg) {
    Write-Host "`n=== Step $n: $msg ===" -ForegroundColor Cyan
}

# ─── Step 1: Validate prerequisites ────────────────────────────────────────────
Write-Step 1 "Validate prerequisites"

foreach ($cmd in "az", "dotnet", "docker") {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        throw "Required tool not found: $cmd"
    }
}

$account = az account show | ConvertFrom-Json
Write-Host "Signed in as: $($account.user.name) | Tenant: $($account.tenantId)"

if (-not $SqlAdminPassword) {
    $SqlAdminPassword = Read-Host "Enter SQL admin password" -AsSecureString
}
$sqlPasswordPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SqlAdminPassword))

# ─── Step 2: Create resource group ─────────────────────────────────────────────
Write-Step 2 "Create resource group (idempotent)"
az group create --name $ResourceGroupName --location $Location --output none
Write-Host "Resource group: $ResourceGroupName"

# ─── Step 3: Deploy Bicep ──────────────────────────────────────────────────────
Write-Step 3 "Deploy Bicep infrastructure"
$deployOutput = az deployment group create `
    --resource-group $ResourceGroupName `
    --template-file "$PSScriptRoot/infra/main.bicep" `
    --parameters "@$PSScriptRoot/infra/main.parameters.json" `
    --parameters sqlAdminPassword="$sqlPasswordPlain" `
    --query properties.outputs `
    --output json | ConvertFrom-Json

$acrName         = $deployOutput.acrName.value
$acrLoginServer  = $deployOutput.acrLoginServer.value
$apiUrl          = $deployOutput.apiUrl.value
$portalUrl       = $deployOutput.portalUrl.value
$projectEndpoint = $deployOutput.aiFoundryProjectEndpoint.value
$aiConnStr       = $deployOutput.appInsightsConnectionString.value

Write-Host "ACR: $acrLoginServer"
Write-Host "API: $apiUrl"
Write-Host "Portal: $portalUrl"

# ─── Step 4: Grant Graph API permissions to Managed Identity ───────────────────
Write-Step 4 "Grant Microsoft Graph permissions to Managed Identity"

# Get the managed identity principal ID
$miPrincipalId = az identity show `
    --resource-group $ResourceGroupName `
    --name "certflow-identity" `
    --query principalId -o tsv

# Microsoft Graph app ID (constant across all tenants)
$graphAppId = "00000003-0000-0000-c000-000000000000"
$graphSpId = az ad sp show --id $graphAppId --query id -o tsv

$permissions = @{
    "User.Read.All"    = "df021288-bdef-4463-88db-98f22de89214"
    "Mail.ReadWrite"   = "e2a3a72e-5f79-4c64-b1b1-878b674786c9"
    "Mail.Send"        = "b633e1c5-b582-4048-a93e-9f11b44c7e96"
}

foreach ($perm in $permissions.GetEnumerator()) {
    $existing = az rest --method GET `
        --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$miPrincipalId/appRoleAssignments" `
        --query "value[?appRoleId=='$($perm.Value)']" -o json | ConvertFrom-Json
    if ($existing.Count -eq 0) {
        az rest --method POST `
            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$miPrincipalId/appRoleAssignments" `
            --body "{`"principalId`":`"$miPrincipalId`",`"resourceId`":`"$graphSpId`",`"appRoleId`":`"$($perm.Value)`"}" `
            --output none
        Write-Host "Granted: $($perm.Key)"
    } else {
        Write-Host "Already granted: $($perm.Key)"
    }
}

# ─── Step 5: Create M365 shared mailbox (if not exists) ────────────────────────
Write-Step 5 "Ensure M365 shared mailbox exists"

$mailboxEmail = (Get-Content "$PSScriptRoot/infra/main.parameters.json" | ConvertFrom-Json).parameters.mailboxEmail.value

Write-Host "Checking mailbox: $mailboxEmail"
Write-Host "If ExchangeOnlineManagement is installed, uncomment the block below."
Write-Host "(Mailbox creation requires Connect-ExchangeOnline — run manually if needed)"

# Uncomment to run when ExchangeOnlineManagement module is available:
# Import-Module ExchangeOnlineManagement -ErrorAction Stop
# Connect-ExchangeOnline
# if (-not (Get-Mailbox -Identity $mailboxEmail -ErrorAction SilentlyContinue)) {
#     New-Mailbox -Shared -Name "CertFlow Reschedule" -Alias "certflow-reschedule" -PrimarySmtpAddress $mailboxEmail
#     Write-Host "Mailbox created: $mailboxEmail"
# } else {
#     Write-Host "Mailbox already exists."
# }

# ─── Step 6: Build and push Docker images ──────────────────────────────────────
Write-Step 6 "Build and push Docker images to ACR"

if ($SkipBuild) {
    Write-Host "Skipping build (--SkipBuild specified)"
} else {
    $images = @(
        @{ Name = "certflow-api";       Path = "./src/CertFlow.Api" },
        @{ Name = "certflow-mcpserver"; Path = "./src/CertFlow.McpServer" },
        @{ Name = "certflow-worker";    Path = "./src/CertFlow.Worker" },
        @{ Name = "certflow-portal";    Path = "./src/CertFlow.Portal.Blazor" }
    )
    foreach ($img in $images) {
        Write-Host "Building $($img.Name)..."
        az acr build `
            --registry $acrName `
            --image "$($img.Name):latest" `
            $img.Path `
            --output none
        Write-Host "Pushed: $acrLoginServer/$($img.Name):latest"
    }
}

# ─── Step 7: Update Container Apps with latest images ──────────────────────────
Write-Step 7 "Update Container Apps"

$apps = @("certflow-api", "certflow-mcp", "certflow-worker", "certflow-portal")
$imageMap = @{
    "certflow-api"    = "certflow-api"
    "certflow-mcp"    = "certflow-mcpserver"
    "certflow-worker" = "certflow-worker"
    "certflow-portal" = "certflow-portal"
}
foreach ($app in $apps) {
    az containerapp update `
        --name $app `
        --resource-group $ResourceGroupName `
        --image "$acrLoginServer/$($imageMap[$app]):latest" `
        --output none
    Write-Host "Updated: $app"
}

# ─── Step 8: Run EF Core migrations + seed data ────────────────────────────────
Write-Step 8 "Run database migrations and seed"

$jobName = "certflow-migrate-$(Get-Random)"
az containerapp job create `
    --name $jobName `
    --resource-group $ResourceGroupName `
    --environment "certflow-env" `
    --image "$acrLoginServer/certflow-api:latest" `
    --replica-completion-count 1 `
    --replica-retry-limit 1 `
    --command "dotnet" "CertFlow.Api.dll" "--migrate-and-seed" `
    --output none

Write-Host "Waiting for migration job to complete..."
$timeout = 300  # 5 minutes
$elapsed = 0
do {
    Start-Sleep 10
    $elapsed += 10
    $status = az containerapp job execution list --name $jobName --resource-group $ResourceGroupName --query "[0].properties.status" -o tsv
    Write-Host "Migration status: $status (${elapsed}s)"
} while ($status -notin @("Succeeded", "Failed") -and $elapsed -lt $timeout)

if ($status -ne "Succeeded") { throw "Migration job failed with status: $status" }

az containerapp job delete --name $jobName --resource-group $ResourceGroupName --yes --output none
Write-Host "Migration complete."

# ─── Step 9: Register Graph subscription ────────────────────────────────────────
Write-Step 9 "Register Graph change notification subscription"

# Update webhookBaseUrl now that we know the API URL
az deployment group create `
    --resource-group $ResourceGroupName `
    --template-file "$PSScriptRoot/infra/main.bicep" `
    --parameters "@$PSScriptRoot/infra/main.parameters.json" `
    --parameters sqlAdminPassword="$sqlPasswordPlain" webhookBaseUrl="$apiUrl" `
    --output none

$response = Invoke-RestMethod -Method POST -Uri "$apiUrl/admin/graph-subscription/create"
Write-Host "Graph subscription: $($response.subscriptionId)"

# ─── Step 10: Register AI Foundry agents ────────────────────────────────────────
Write-Step 10 "Register Azure AI Foundry agents"

$response = Invoke-RestMethod -Method POST -Uri "$apiUrl/admin/agents/register"
Write-Host "Agents registered: $($response.message)"

# ─── Step 11: Summary ──────────────────────────────────────────────────────────
Write-Step 11 "Deployment complete"

Write-Host ""
Write-Host "┌────────────────────────────────────────────────────────────┐" -ForegroundColor Green
Write-Host "│  CertFlow AI — Deployment Summary                          │" -ForegroundColor Green
Write-Host "├────────────────────────────────────────────────────────────┤" -ForegroundColor Green
Write-Host "│  Portal   : $portalUrl" -ForegroundColor Green
Write-Host "│  API      : $apiUrl" -ForegroundColor Green
Write-Host "│  Mailbox  : $mailboxEmail" -ForegroundColor Green
Write-Host "│  Webhook  : $apiUrl/api/emails/graph-webhook" -ForegroundColor Green
Write-Host "│  AI Hub   : Azure AI Foundry Portal → certflow-project" -ForegroundColor Green
Write-Host "└────────────────────────────────────────────────────────────┘" -ForegroundColor Green
