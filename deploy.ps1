#Requires -Version 7.0
<#
.SYNOPSIS
  CertFlow AI — idempotent end-to-end deploy/redeploy script.
  Safe to re-run after teardown.ps1 to fully recreate the environment.

.PARAMETER SqlAdminPassword
  SQL server admin password (prompted if not supplied).

.PARAMETER ResourceGroupName
  Azure resource group name. Default: certflow-rg

.PARAMETER Location
  Azure region. Default: australiaeast
  NOTE: many subscriptions (including Visual Studio subscriptions) refuse new Azure SQL
  servers in US/EU regions with "RegionDoesNotAllowProvisioning". australiaeast is known
  to work. If you change this, verify SQL first:
      az sql server create --name probe$(Get-Random) --resource-group <rg> --location <region> ...

.PARAMETER SkipBuild
  Skip the container image build and reuse whatever is already in ACR.
#>
param(
    [string]$ResourceGroupName = "certflow-rg",
    [string]$Location          = "australiaeast",
    [switch]$SkipBuild,
    [SecureString]$SqlAdminPassword,
    [SecureString]$McpApiKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([int]$n, [string]$msg) {
    Write-Host "`n=== Step ${n}: ${msg} ===" -ForegroundColor Cyan
}

# ─── Step 1: Prerequisites ─────────────────────────────────────────────────────
Write-Step 1 "Validate prerequisites"

foreach ($cmd in "az", "dotnet") {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) { throw "Required tool not found: $cmd" }
}
# Container images are built with `az acr build` (server-side), so no local Docker needed.

az extension add --name containerapp --yes --only-show-errors 2>&1 | Out-Null

$account = az account show | ConvertFrom-Json
Write-Host "Signed in as : $($account.user.name)"
Write-Host "Tenant       : $($account.tenantId)"
Write-Host "Subscription : $($account.name)"

$tenantDomain = $account.tenantDefaultDomain
$mailboxEmail = "exam-support@$tenantDomain"

if (-not $SqlAdminPassword) {
    $SqlAdminPassword = Read-Host "Enter SQL admin password" -AsSecureString
}
$sqlPasswordPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SqlAdminPassword))

# The MCP server's API key has to survive redeploys untouched: the Foundry MCP tool connection
# stores this value, so minting a new one silently breaks every agent tool call until that
# connection is updated by hand.
#
# The existing container-app secret is the system of record, read back via the ARM listSecrets
# action. Key Vault would be the more natural home, but its data plane is RBAC-gated and only the
# managed identity holds Secrets User — the human running this script cannot read or write vault
# secrets without a new role assignment, whereas listSecrets is control-plane and already
# available to anyone who can deploy the group.
if ($McpApiKey) {
    $mcpKeyPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($McpApiKey))
    $mcpKeyOrigin = "supplied on the command line"
} else {
    $subId = $account.id
    $mcpKeyPlain = az rest --method POST `
        --uri "https://management.azure.com/subscriptions/$subId/resourceGroups/$ResourceGroupName/providers/Microsoft.App/containerApps/certflow-mcp/listSecrets?api-version=2024-03-01" `
        --query "value[?name=='mcp-api-key'].value | [0]" -o tsv 2>$null

    if ($mcpKeyPlain) {
        $mcpKeyOrigin = "reused from the existing certflow-mcp container app"
    } else {
        $mcpKeyPlain  = [Convert]::ToBase64String(
            [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $mcpKeyOrigin = "newly generated (fresh environment)"
    }
}
Write-Host "MCP API key  : $mcpKeyOrigin"

# ─── Step 2: Resource group ────────────────────────────────────────────────────
Write-Step 2 "Create resource group"
az group create --name $ResourceGroupName --location $Location --output none
Write-Host "Resource group: $ResourceGroupName ($Location)"

# ─── Step 3: Bicep — pass 1 (placeholder images) ───────────────────────────────
# ACR is created empty by this pass, so the four container apps must start on a public
# placeholder image. Deploying them straight onto ACR tags that do not exist yet fails
# with "unable to pull image using Managed identity".
Write-Step 3 "Deploy infrastructure (pass 1 — placeholder images)"

az deployment group create `
    --resource-group $ResourceGroupName `
    --template-file "$PSScriptRoot/infra/main.bicep" `
    --parameters "@$PSScriptRoot/infra/main.parameters.json" `
    --parameters sqlAdminPassword="$sqlPasswordPlain" mcpApiKey="$mcpKeyPlain" imagesPublished=false `
    --name certflow-pass1 `
    --output none

$out = az deployment group show --resource-group $ResourceGroupName --name certflow-pass1 `
    --query properties.outputs --output json | ConvertFrom-Json

$acrName        = $out.acrName.value
$acrLoginServer = $out.acrLoginServer.value
$apiUrl         = $out.apiUrl.value
$portalUrl      = $out.portalUrl.value

Write-Host "ACR    : $acrLoginServer"
Write-Host "API    : $apiUrl"
Write-Host "Portal : $portalUrl"
Write-Host "MCP    : $($out.mcpUrl.value)"
Write-Host ""
Write-Host "Register $($out.mcpUrl.value) as the remote MCP server endpoint in Foundry," -ForegroundColor Yellow
Write-Host "with header X-Api-Key set to this app's mcp-api-key secret." -ForegroundColor Yellow

# ─── Step 4: Graph API permissions for the Managed Identity ────────────────────
Write-Step 4 "Grant Microsoft Graph permissions to the Managed Identity"

$miPrincipalId = az identity show --resource-group $ResourceGroupName --name "certflow-identity" --query principalId -o tsv
$graphSpId     = az ad sp show --id "00000003-0000-0000-c000-000000000000" --query id -o tsv

$permissions = @{
    "User.Read.All"  = "df021288-bdef-4463-88db-98f22de89214"
    "Mail.ReadWrite" = "e2a3a72e-5f79-4c64-b1b1-878b674786c9"
    "Mail.Send"      = "b633e1c5-b582-4048-a93e-9f11b44c7e96"
}
foreach ($perm in $permissions.GetEnumerator()) {
    $body = "{`"principalId`":`"$miPrincipalId`",`"resourceId`":`"$graphSpId`",`"appRoleId`":`"$($perm.Value)`"}"
    $r = az rest --method POST `
        --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$miPrincipalId/appRoleAssignments" `
        --body $body --output none 2>&1
    if ("$r" -match "already exists") { Write-Host "Already granted : $($perm.Key)" }
    elseif ("$r" -match "ERROR")      { Write-Host "FAILED $($perm.Key) : $r" }
    else                              { Write-Host "Granted         : $($perm.Key)" }
}

# ─── Step 5: Mailbox ───────────────────────────────────────────────────────────
# A true Exchange shared mailbox needs Connect-ExchangeOnline (interactive), so this
# script provisions a licensed mailbox user via Graph instead — fully non-interactive.
Write-Step 5 "Ensure the CertFlow mailbox exists"

$existing = az rest --method GET --uri "https://graph.microsoft.com/v1.0/users/$mailboxEmail" 2>&1
if ("$existing" -match "Request_ResourceNotFound") {
    $mailboxPassword = "Cf!" + [System.Guid]::NewGuid().ToString("N").Substring(0,12) + "9Z"
    $userBody = @{
        accountEnabled    = $true
        displayName       = "Exam Support"
        mailNickname      = "exam-support"
        userPrincipalName = $mailboxEmail
        usageLocation     = "IN"
        passwordProfile   = @{ password = $mailboxPassword; forceChangePasswordNextSignIn = $false }
    } | ConvertTo-Json -Depth 5
    $userBody | Out-File "$env:TEMP\cf_user.json" -Encoding utf8 -NoNewline

    $created = az rest --method POST --uri "https://graph.microsoft.com/v1.0/users" `
        --headers "Content-Type=application/json" --body "@$env:TEMP\cf_user.json" | ConvertFrom-Json
    Remove-Item "$env:TEMP\cf_user.json" -Force

    # A mailbox only exists once a licence with Exchange Online is applied.
    $skuId = (az rest --method GET --uri "https://graph.microsoft.com/v1.0/subscribedSkus" | ConvertFrom-Json).value |
             Where-Object { $_.skuPartNumber -in @("DEVELOPERPACK_E5","ENTERPRISEPACK","SPE_E5","EXCHANGESTANDARD") } |
             Select-Object -First 1 -ExpandProperty skuId
    if (-not $skuId) { throw "No Exchange Online licence SKU available in this tenant." }

    $licBody = @{ addLicenses = @(@{ skuId = $skuId; disabledPlans = @() }); removeLicenses = @() } | ConvertTo-Json -Depth 5
    $licBody | Out-File "$env:TEMP\cf_lic.json" -Encoding utf8 -NoNewline
    az rest --method POST --uri "https://graph.microsoft.com/v1.0/users/$($created.id)/assignLicense" `
        --headers "Content-Type=application/json" --body "@$env:TEMP\cf_lic.json" --output none
    Remove-Item "$env:TEMP\cf_lic.json" -Force

    Write-Host "Mailbox created  : $mailboxEmail"
    Write-Host "Mailbox password : $mailboxPassword   <-- record this now"
    Write-Host "Waiting for Exchange to provision the mailbox (can take several minutes)..."
} else {
    Write-Host "Mailbox already exists: $mailboxEmail"
}

# ─── Step 6: Build and push container images ───────────────────────────────────
# The Dockerfiles reference sibling projects (`COPY . .` with src/... paths), so the
# build context must be the repo root and the Dockerfile passed via --file.
Write-Step 6 "Build container images in ACR"

$imageTag = "v" + (Get-Date -Format "MMddHHmm")

if ($SkipBuild) {
    Write-Host "Skipping build (-SkipBuild)"
    $imageTag = "latest"
} else {
    $images = @(
        @{ Name = "certflow-api";       Dockerfile = "src/CertFlow.Api/Dockerfile" },
        @{ Name = "certflow-mcpserver"; Dockerfile = "src/CertFlow.McpServer/Dockerfile" },
        @{ Name = "certflow-worker";    Dockerfile = "src/CertFlow.Worker/Dockerfile" },
        @{ Name = "certflow-portal";    Dockerfile = "src/CertFlow.Portal.Blazor/Dockerfile" }
    )
    foreach ($img in $images) {
        Write-Host "Building $($img.Name):$imageTag ..."
        az acr build `
            --registry $acrName `
            --resource-group $ResourceGroupName `
            --image "$($img.Name):$imageTag" `
            --image "$($img.Name):latest" `
            --file $img.Dockerfile `
            $PSScriptRoot `
            --output none
        Write-Host "  pushed $acrLoginServer/$($img.Name):$imageTag"
    }
}

# ─── Step 7: Bicep — pass 2 (real images + webhook URL) ────────────────────────
# Re-running Bicep is what points the apps at ACR. Doing it via `az containerapp update`
# alone would be undone by the next Bicep run, so the flag lives in the template.
Write-Step 7 "Deploy infrastructure (pass 2 — real images)"

az deployment group create `
    --resource-group $ResourceGroupName `
    --template-file "$PSScriptRoot/infra/main.bicep" `
    --parameters "@$PSScriptRoot/infra/main.parameters.json" `
    --parameters sqlAdminPassword="$sqlPasswordPlain" mcpApiKey="$mcpKeyPlain" imagesPublished=true webhookBaseUrl="$apiUrl" `
    --name certflow-pass2 `
    --output none

# Pin to the immutable tag so a redeploy always rolls a fresh revision
# (pushing a new :latest does not, on its own, create a new revision).
if (-not $SkipBuild) {
    $appImages = @{
        "certflow-api"    = "certflow-api"
        "certflow-mcp"    = "certflow-mcpserver"
        "certflow-worker" = "certflow-worker"
        "certflow-portal" = "certflow-portal"
    }
    foreach ($app in $appImages.Keys) {
        az containerapp update --name $app --resource-group $ResourceGroupName `
            --image "$acrLoginServer/$($appImages[$app]):$imageTag" --output none --only-show-errors
        Write-Host "Rolled $app -> $imageTag"
    }
}

# ─── Step 8: Database migration + seed ─────────────────────────────────────────
# Driven by MIGRATE_AND_SEED rather than a CLI arg: `az containerapp job` does not split
# space-separated --args, so "--migrate-and-seed" cannot be passed as its own argument.
Write-Step 8 "Run database migrations and seed"

$miId  = az identity show --resource-group $ResourceGroupName --name certflow-identity --query id -o tsv
$jobEnv = az containerapp show --name certflow-api --resource-group $ResourceGroupName `
    --query "properties.template.containers[0].env" -o json | ConvertFrom-Json
$envArgs = @()
foreach ($e in $jobEnv) { if ($e.value) { $envArgs += "$($e.name)=$($e.value)" } }
$envArgs += "MIGRATE_AND_SEED=true"

$jobExists = az containerapp job show --name certflow-migrate --resource-group $ResourceGroupName 2>&1
if ("$jobExists" -match "ResourceNotFound|ERROR") {
    az containerapp job create `
        --name certflow-migrate `
        --resource-group $ResourceGroupName `
        --environment "certflow-env" `
        --image "$acrLoginServer/certflow-api:$imageTag" `
        --trigger-type Manual `
        --replica-timeout 900 --replica-completion-count 1 --replica-retry-limit 1 `
        --mi-user-assigned $miId `
        --registry-server $acrLoginServer --registry-identity $miId `
        --cpu 0.5 --memory 1Gi `
        --output none
}
az containerapp job update --name certflow-migrate --resource-group $ResourceGroupName `
    --image "$acrLoginServer/certflow-api:$imageTag" --set-env-vars $envArgs --output none --only-show-errors

az containerapp job start --name certflow-migrate --resource-group $ResourceGroupName --output none
Write-Host "Waiting for migration job..."
$elapsed = 0
do {
    Start-Sleep 15; $elapsed += 15
    $status = az containerapp job execution list --name certflow-migrate --resource-group $ResourceGroupName `
        --query "[0].properties.status" -o tsv
    Write-Host "  ${elapsed}s : $status"
} while ($status -notin @("Succeeded","Failed") -and $elapsed -lt 600)
if ($status -ne "Succeeded") { throw "Migration job failed (status: $status)" }
Write-Host "Database migrated and seeded."

# ─── Step 9: Portal Entra app registration ─────────────────────────────────────
Write-Step 9 "Configure Entra sign-in for the portal"

$appName = "CertFlow AI Portal"
$appId = az ad app list --display-name $appName --query "[0].appId" -o tsv
if (-not $appId) {
    $appId = az ad app create --display-name $appName `
        --sign-in-audience AzureADMyOrg `
        --web-redirect-uris "$portalUrl/signin-oidc" `
        --enable-id-token-issuance true `
        --query appId -o tsv

    $roleId = [guid]::NewGuid().ToString()
    $appRoles = @(@{
        allowedMemberTypes = @("User")
        description        = "Exam operations staff — access to admin audit and requests"
        displayName        = "ExamOps"
        id                 = $roleId
        isEnabled          = $true
        value              = "ExamOps"
    }) | ConvertTo-Json -Depth 5 -AsArray
    $appRoles | Out-File "$env:TEMP\cf_roles.json" -Encoding utf8 -NoNewline
    az ad app update --id $appId --app-roles "@$env:TEMP\cf_roles.json" --only-show-errors
    Remove-Item "$env:TEMP\cf_roles.json" -Force

    az ad sp create --id $appId --output none
    Write-Host "Created app registration: $appId"
} else {
    az ad app update --id $appId --web-redirect-uris "$portalUrl/signin-oidc" --only-show-errors
    Write-Host "Reusing app registration: $appId"
}

$clientSecret = az ad app credential reset --id $appId --display-name "certflow-portal" --years 1 --query password -o tsv
az containerapp secret set --name certflow-portal --resource-group $ResourceGroupName `
    --secrets "aad-client-secret=$clientSecret" --output none --only-show-errors
az containerapp update --name certflow-portal --resource-group $ResourceGroupName `
    --set-env-vars `
        "AzureAd__Instance=https://login.microsoftonline.com/" `
        "AzureAd__TenantId=$($account.tenantId)" `
        "AzureAd__ClientId=$appId" `
        "AzureAd__CallbackPath=/signin-oidc" `
        "AzureAd__ClientSecret=secretref:aad-client-secret" `
        "ApiBaseUrl=$apiUrl" `
    --output none --only-show-errors
Write-Host "Portal sign-in configured."

# Grant the deploying user the ExamOps role so the admin pages are reachable immediately.
$spObjId = az ad sp show --id $appId --query id -o tsv
$roleId  = az ad app show --id $appId --query "appRoles[?value=='ExamOps'].id | [0]" -o tsv
$userId  = az ad signed-in-user show --query id -o tsv
$body = "{`"principalId`":`"$userId`",`"resourceId`":`"$spObjId`",`"appRoleId`":`"$roleId`"}"
az rest --method POST --uri "https://graph.microsoft.com/v1.0/users/$userId/appRoleAssignments" `
    --body $body --output none 2>&1 | Out-Null
Write-Host "ExamOps role assigned to $($account.user.name)"

# ─── Step 10: Graph subscription + agent registration ──────────────────────────
Write-Step 10 "Register Graph subscription and AI Foundry agents"

$sub = Invoke-RestMethod -Method POST -Uri "$apiUrl/admin/graph-subscription/create" `
       -ContentType "application/json" -Body "{}" -TimeoutSec 180
Write-Host "Graph subscription: $($sub.subscriptionId)"

# The agents declare a Foundry-hosted MCP tool that resolves its credentials through this project
# connection, so it has to exist before they are registered. Without it the agents register fine
# and then every tool call fails, with nothing in the failure naming the missing connection.
#
# category MUST be 'RemoteTool'. The API also accepts 'CustomKeys', and a CustomKeys connection
# authenticates correctly — but the portal's Tools blade filters on the tool category, so the MCP
# server is invisible there and looks unconfigured. 'MCP', 'ModelContextProtocol', 'McpServer' and
# 'RemoteMcp' are all rejected outright.
$foundryAccount = $out.aiFoundryAccountName.value
$foundryProject = $out.aiFoundryProjectName.value
$connName       = "certflow-mcp"
$mcpUrl         = $out.mcpUrl.value

$connBody = @{
    properties = @{
        category      = 'RemoteTool'
        target        = $mcpUrl
        authType      = 'CustomKeys'
        isSharedToAll = $true
        # Header name must match what the MCP server checks and what McpToolExecutor sends.
        credentials   = @{ keys = @{ 'X-Api-Key' = $mcpKeyPlain } }
    }
} | ConvertTo-Json -Depth 10

$connFile = Join-Path ([IO.Path]::GetTempPath()) "certflow-mcp-connection.json"
try {
    $connBody | Set-Content -Path $connFile -Encoding UTF8
    # PUT is a create-or-replace, so re-running simply refreshes the target and key.
    az rest --method PUT `
        --uri "https://management.azure.com/subscriptions/$($account.id)/resourceGroups/$ResourceGroupName/providers/Microsoft.CognitiveServices/accounts/$foundryAccount/projects/$foundryProject/connections/$connName`?api-version=2025-06-01" `
        --headers "Content-Type=application/json" --body "@$connFile" --output none
    Write-Host "MCP tool connection: $connName -> $mcpUrl"
} finally {
    Remove-Item $connFile -Force -ErrorAction SilentlyContinue
}

$agents = Invoke-RestMethod -Method POST -Uri "$apiUrl/admin/agents/register" `
          -ContentType "application/json" -Body "{}" -TimeoutSec 300
Write-Host "Agents: $($agents.message)"

# ─── Step 11: Summary ──────────────────────────────────────────────────────────
Write-Step 11 "Deployment complete"
Write-Host ""
Write-Host "  Portal   : $portalUrl"        -ForegroundColor Green
Write-Host "  API      : $apiUrl"           -ForegroundColor Green
Write-Host "  Mailbox  : $mailboxEmail"     -ForegroundColor Green
Write-Host "  Webhook  : $apiUrl/api/emails/graph-webhook" -ForegroundColor Green
Write-Host "  Foundry  : Azure AI Foundry portal -> certflow-project" -ForegroundColor Green
Write-Host ""
