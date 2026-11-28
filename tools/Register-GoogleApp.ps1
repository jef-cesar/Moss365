<#
.SYNOPSIS
    Creates the own Google Cloud project of the organisation for Moss365 (G1, U35) and turns on the Admin SDK API.

.DESCRIPTION
    Moss365 reads Google Workspace with an OAuth client of the organisation itself, never with a client of the makers
    (U35). This script, with the Google Cloud CLI (gcloud), signed in as someone who may create projects:
      1. creates the project (or uses it when it exists already);
      2. turns on the APIs Moss365 reads with: the Admin SDK (admin.googleapis.com: users, groups, organisational
         units, resources, admin roles, the logs), and for the parts of Google (U40) the Enterprise License Manager
         (licensing.googleapis.com), the Groups Settings API (groupssettings.googleapis.com) and the Alert Center
         (alertcenter.googleapis.com). Turning an API on gives no right by itself: a part only asks its scope when it is
         on in Moss365.
    Two steps stay by hand in the console, because Google has no command for them; the script shows the links:
      3. the OAuth consent screen, of the type Internal (only accounts of the organisation);
      4. an OAuth client of the type Desktop app. Fill in its client ID and secret in Moss365, on Connections, with the
         primary domain of Google Workspace.
    Moss365 only asks for reading scopes (users, groups, organisational units) and accepts only an account of that
    domain. Running the script again changes nothing that is already there.

.PARAMETER ProjectId
    The id of the project: lowercase letters, digits and hyphens, 6 to 30 characters, starting with a letter.

.PARAMETER Naam
    The name of the project as the console shows it. Default: Moss365.

.PARAMETER Organisatie
    The id of the Google Cloud organisation to create the project in (optional).

.EXAMPLE
    .\Register-GoogleApp.ps1 -ProjectId moss365-contoso
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z][a-z0-9-]{4,28}[a-z0-9]$')]
    [string]$ProjectId,
    [string]$Naam = 'Moss365',
    [ValidatePattern('^[0-9]*$')]
    [string]$Organisatie = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Gcloud {
    # gcloud with the given arguments; stops on an error with the text of gcloud
    param([string[]]$Argumenten)
    $uit = & gcloud @Argumenten 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gcloud $($Argumenten -join ' ') failed: $($uit -join ' ')" }
    return $uit
}

if (-not (Get-Command gcloud -ErrorAction SilentlyContinue)) {
    throw 'The Google Cloud CLI (gcloud) is not installed: https://cloud.google.com/sdk/docs/install'
}

# 1. The project
& gcloud projects describe $ProjectId '--format=value(projectId)' 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Project $ProjectId exists already."
}
else {
    $maken = @('projects', 'create', $ProjectId, "--name=$Naam")
    if ($Organisatie) { $maken += "--organization=$Organisatie" }
    Invoke-Gcloud $maken | Out-Null
    Write-Host "Project $ProjectId created."
}

# 2. The APIs
$apis = @('admin.googleapis.com', 'licensing.googleapis.com', 'groupssettings.googleapis.com', 'alertcenter.googleapis.com')
Invoke-Gcloud (@('services', 'enable') + $apis + @("--project=$ProjectId")) | Out-Null
Write-Host "APIs turned on: $($apis -join ', ')."

# 3 and 4. By hand in the console
Write-Host ''
Write-Host 'Now in the console, as the script cannot do this:'
Write-Host "  3. Consent screen, type Internal: https://console.cloud.google.com/apis/credentials/consent?project=$ProjectId"
Write-Host "  4. OAuth client, type Desktop app: https://console.cloud.google.com/apis/credentials/oauthclient?project=$ProjectId"
Write-Host 'Then fill in the client ID, the secret and the primary domain in Moss365, on Connections.'
