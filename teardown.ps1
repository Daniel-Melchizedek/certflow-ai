#Requires -Version 7.0
<#
.SYNOPSIS
  CertFlow AI — tear down all Azure resources.
  M365 shared mailbox and Entra app registrations are NOT deleted.
  Run deploy.ps1 to recreate everything.

.PARAMETER ResourceGroupName
  Resource group to delete. Default: certflow-rg

.PARAMETER NoWait
  Queue deletion without waiting for completion.
#>
param(
    [string]$ResourceGroupName = "certflow-rg",
    [switch]$NoWait
)

Write-Host "Deleting resource group '$ResourceGroupName'..." -ForegroundColor Yellow
Write-Host "The M365 shared mailbox and Graph app permissions are NOT affected." -ForegroundColor Yellow
Write-Host ""

$confirm = Read-Host "Type 'yes' to confirm deletion"
if ($confirm -ne 'yes') {
    Write-Host "Aborted." -ForegroundColor Red
    exit 0
}

$noWaitFlag = if ($NoWait) { "--no-wait" } else { "" }
az group delete --name $ResourceGroupName --yes $noWaitFlag

Write-Host ""
Write-Host "Resource group deletion queued." -ForegroundColor Green
Write-Host "Run .\deploy.ps1 to recreate the full stack." -ForegroundColor Green
