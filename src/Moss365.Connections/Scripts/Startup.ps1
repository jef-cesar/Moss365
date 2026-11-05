<#
.SYNOPSIS
    Startup for scripts from the Moss365 library (embedded in the app; ASCII only).

.DESCRIPTION
    Connects to what the script needs (Exchange Online, Microsoft Graph, AD), with the sign-in of the app, and calls
    the script with the values from the form. Everything the script writes or returns goes to the app as one line of
    JSON (kind s: text, info, warning, error, progress, row).

    Secrets: the tokens come only via environment variables of this process (MOSS365_EXO_TOKEN, MOSS365_GRAPH_TOKEN)
    and the AD password via the input (stdin). They are removed from the environment right away and never written.

    Exit codes: 0 succeeded, 1 stopped with an error, 2 done with errors, 3 preview not possible, 4 connection failed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Script,
    [string]$Invoer,
    [string]$Uitvoer,
    [switch]$Controleer,
    [string]$Verbinding = 'Lokaal',
    [switch]$Voorbeeld,
    [string]$Upn,
    [string]$AdServer,
    [string]$AdGebruiker,
    [switch]$AdWachtwoord
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Send-Moss {
    param([string]$Soort, [hashtable]$Data = @{})
    $Data['s'] = $Soort
    [Console]::Out.WriteLine(($Data | ConvertTo-Json -Compress -Depth 4))
    [Console]::Out.Flush()
}

function Format-MossWaarde {
    param($Waarde)
    if ($null -eq $Waarde) { return '' }
    if ($Waarde -is [datetime]) { return $Waarde.ToString('yyyy-MM-dd HH:mm') }
    if ($Waarde -is [string]) { return $Waarde }
    if ($Waarde -is [System.Collections.IDictionary]) { return (@($Waarde.Keys | ForEach-Object { "$_=$($Waarde[$_])" }) -join '; ') }
    if ($Waarde -is [System.Collections.IEnumerable]) { return (@($Waarde | ForEach-Object { "$_" }) -join ', ') }
    return "$Waarde"
}

$script:Opmaak = New-Object System.Collections.Generic.List[object]
$script:Fouten = 0

function Send-Opmaak {
    # Output of Format-Table/Format-List: as text, in one go
    if ($script:Opmaak.Count -gt 0) {
        $tekst = ($script:Opmaak | Out-String -Width 220).Trim("`r", "`n")
        $script:Opmaak.Clear()
        foreach ($regel in $tekst -split "`r?`n") { Send-Moss 'tekst' @{ t = $regel } }
    }
}

function Send-Uitvoer {
    param($Item)
    if ($null -eq $Item) { return }
    $type = $Item.GetType().FullName
    if ($type -like 'Microsoft.PowerShell.Commands.Internal.Format.*') { $script:Opmaak.Add($Item); return }
    Send-Opmaak

    if ($Item -is [System.Management.Automation.ErrorRecord]) {
        $script:Fouten++
        Send-Moss 'fout' @{ t = $Item.ToString() }
    }
    elseif ($Item -is [System.Management.Automation.WarningRecord]) { Send-Moss 'waarschuwing' @{ t = $Item.Message } }
    elseif ($Item -is [System.Management.Automation.VerboseRecord]) { Send-Moss 'info' @{ t = $Item.Message } }
    elseif ($Item -is [System.Management.Automation.DebugRecord]) { }
    elseif ($Item -is [System.Management.Automation.InformationRecord]) {
        $m = $Item.MessageData
        if ($m -is [System.Management.Automation.HostInformationMessage]) {
            $kleur = if ($null -ne $m.ForegroundColor) { "$($m.ForegroundColor)" } else { '' }
            Send-Moss 'tekst' @{ t = "$($m.Message)"; kleur = $kleur; doorgaan = [bool]$m.NoNewLine }
        }
        else { Send-Moss 'tekst' @{ t = "$m" } }
    }
    elseif ($Item -is [string] -or $Item -is [ValueType]) { Send-Moss 'tekst' @{ t = "$Item" } }
    else {
        $rij = [ordered]@{}
        if ($Item -is [System.Collections.IDictionary]) {
            foreach ($k in $Item.Keys) { $rij["$k"] = Format-MossWaarde $Item[$k] }
            $tabel = 'Resultaat'
        }
        else {
            foreach ($p in $Item.PSObject.Properties) {
                if ($p.MemberType -in 'NoteProperty', 'Property', 'ScriptProperty', 'AliasProperty') {
                    try { $rij[$p.Name] = Format-MossWaarde $p.Value } catch { $rij[$p.Name] = '' }
                }
            }
            $tabel = ($Item.PSObject.TypeNames | Select-Object -First 1) -replace '^Selected\.', '' -replace '^Deserialized\.', ''
            if ($tabel -match 'PSCustomObject$|Hashtable$') { $tabel = 'Resultaat' }
            $tabel = ($tabel -split '\.')[-1]
        }
        Send-Moss 'rij' @{ tabel = $tabel; r = $rij }
    }
}

# ---- Check only: syntax errors, without running or connecting anything ----
if ($Controleer) {
    $fouten = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$fouten)
    foreach ($f in @($fouten)) {
        Send-Moss 'fout' @{ t = $f.Message; regel = $f.Extent.StartLineNumber; kolom = $f.Extent.StartColumnNumber }
    }
    exit 0
}

# ---- Take the secrets out of the environment (never pass them on or write them) ----
$exoToken = $env:MOSS365_EXO_TOKEN
$env:MOSS365_EXO_TOKEN = $null
$graphToken = $env:MOSS365_GRAPH_TOKEN
$env:MOSS365_GRAPH_TOKEN = $null
$adWachtwoordTekst = if ($AdWachtwoord) { [Console]::In.ReadLine() } else { $null }

$diensten = @($Verbinding -split '[,;+]' | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })

# ---- Help for scripts: Microsoft Graph without the Graph module (no clash with Exchange) ----
$global:MossGraphToken = $graphToken
function global:Invoke-MossGraph {
    <#
    .SYNOPSIS
        Calls Microsoft Graph with the sign-in of Moss365, without the Graph module.
    .EXAMPLE
        Invoke-MossGraph '/users?$select=displayName' -Alles
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Pad,
        [string]$Methode = 'GET',
        $Inhoud,
        [hashtable]$Headers = @{},
        [switch]$Alles
    )
    if (-not $global:MossGraphToken) { throw 'No Graph connection: add Graph to the connections of this script.' }
    $uri = if ($Pad -match '^https://') { $Pad } else { 'https://graph.microsoft.com/v1.0' + $(if ($Pad.StartsWith('/')) { $Pad } else { '/' + $Pad }) }
    $h = @{ Authorization = "Bearer $global:MossGraphToken" }
    foreach ($k in $Headers.Keys) { $h[$k] = $Headers[$k] }
    $param = @{ Method = $Methode; Uri = $uri; Headers = $h }
    if ($null -ne $Inhoud) { $param['Body'] = ($Inhoud | ConvertTo-Json -Depth 10); $param['ContentType'] = 'application/json' }
    $r = Invoke-RestMethod @param
    if (-not $Alles) { return $r }
    $alle = New-Object System.Collections.Generic.List[object]
    while ($true) {
        if ($null -ne $r.value) { foreach ($v in $r.value) { $alle.Add($v) } }
        $volgende = $r.'@odata.nextLink'
        if (-not $volgende) { break }
        $param['Uri'] = $volgende
        $r = Invoke-RestMethod @param
    }
    return $alle
}

# ---- Connect ----
try {
    # Graph first, then Exchange: each loads its own version of MSAL, and in the reverse order they clash
    if ($diensten -contains 'graph') {
        if (-not $graphToken) { throw 'No sign-in for Microsoft Graph: sign in in the app (Connections).' }
        if (Get-Module -ListAvailable Microsoft.Graph.Authentication) {
            try {
                Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
                Connect-MgGraph -AccessToken (ConvertTo-SecureString $graphToken -AsPlainText -Force) -NoWelcome -ErrorAction Stop
                Send-Moss 'info' @{ t = 'Connected to Microsoft Graph.' }
            }
            catch {
                Send-Moss 'waarschuwing' @{ t = "The Graph module could not connect ($($_.Exception.Message)); Invoke-MossGraph does work." }
            }
        }
        else {
            Send-Moss 'info' @{ t = 'Microsoft Graph via Invoke-MossGraph (the Graph module is not installed).' }
        }
        $global:MossUpn = $Upn
        function global:Get-MgContext { [pscustomobject]@{ Account = $global:MossUpn; AuthType = 'AccessToken'; ContextScope = 'Process' } }
    }

    if ($diensten -contains 'exchange') {
        if (-not $exoToken) { throw 'No sign-in for Exchange Online: sign in in the app (Connections).' }
        Import-Module ExchangeOnlineManagement -ErrorAction Stop
        $exo = @{ AccessToken = $exoToken; ShowBanner = $false }
        if ($Upn) { $exo['UserPrincipalName'] = $Upn }
        Connect-ExchangeOnline @exo
        Send-Moss 'info' @{ t = 'Connected to Exchange Online.' }
    }

    if ($diensten -contains 'ad') {
        Import-Module ActiveDirectory -ErrorAction Stop
        $global:MossAd = @{}
        if ($AdServer) {
            $global:PSDefaultParameterValues['*-AD*:Server'] = $AdServer
            $global:MossAd['Server'] = $AdServer
        }
        if ($AdGebruiker -and $adWachtwoordTekst) {
            $cred = New-Object System.Management.Automation.PSCredential($AdGebruiker, (ConvertTo-SecureString $adWachtwoordTekst -AsPlainText -Force))
            $global:PSDefaultParameterValues['*-AD*:Credential'] = $cred
            $global:MossAd['Credential'] = $cred
        }
        Send-Moss 'info' @{ t = "Active Directory: $(if ($AdServer) { $AdServer } else { 'domain controller of this PC' })." }
    }
}
catch {
    Send-Moss 'fout' @{ t = "Connecting failed: $($_.Exception.Message)" }
    exit 4
}
finally {
    $adWachtwoordTekst = $null
    $exoToken = $null
}

# ---- What is not possible or not needed in the app ----
function global:Connect-ExchangeOnline { Write-Verbose 'Connect-ExchangeOnline skipped: Moss365 is already connected.' }
function global:Disconnect-ExchangeOnline { }
function global:Connect-MgGraph { Write-Verbose 'Connect-MgGraph skipped: Moss365 is already connected.' }
function global:Disconnect-MgGraph { }
function global:Read-Host { throw 'Read-Host does not work in the app: make it a variable (Edit > Variables).' }
function global:Write-Progress {
    param([Parameter(Position = 0)][string]$Activity, [Parameter(Position = 1)][string]$Status, [int]$PercentComplete = -1,
        [switch]$Completed, [int]$Id, [int]$ParentId, [string]$CurrentOperation, [int]$SecondsRemaining)
    Send-Moss 'voortgang' @{ t = (@($Activity, $Status, $CurrentOperation) | Where-Object { $_ }) -join ' - '; p = $PercentComplete; klaar = [bool]$Completed }
}

# ---- Values ----
$parameters = @{}
try {
    $opdracht = Get-Command -Name $Script -CommandType ExternalScript -ErrorAction Stop
    $waarden = Get-Content -LiteralPath $Invoer -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($p in $waarden.PSObject.Properties) {
        if (-not $opdracht.Parameters.ContainsKey($p.Name)) { continue }
        $type = $opdracht.Parameters[$p.Name].ParameterType
        $w = $p.Value
        if ($type -eq [System.Management.Automation.SwitchParameter]) {
            if ($w -eq $true -or "$w" -in 'true', 'ja', '1') { $parameters[$p.Name] = $true }
            continue
        }
        if ($null -eq $w -or "$w" -eq '') { continue }
        if ($type -eq [datetime]) { $w = [datetime]::ParseExact("$w", 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) }
        $parameters[$p.Name] = $w
    }
    if ($Voorbeeld) {
        if (-not $opdracht.Parameters.ContainsKey('WhatIf')) {
            Send-Moss 'fout' @{ t = 'This script does not support -WhatIf: no preview is possible. Nothing was run.' }
            exit 3
        }
        $parameters['WhatIf'] = $true
    }
}
catch {
    Send-Moss 'fout' @{ t = "The script could not be started: $($_.Exception.Message)" }
    exit 1
}
finally {
    Remove-Item -LiteralPath $Invoer -Force -ErrorAction SilentlyContinue
}

# ---- Run ----
$global:MossUitvoer = $Uitvoer
Set-Location -LiteralPath $Uitvoer
$ErrorActionPreference = 'Continue'
$code = 0
try {
    & $Script @parameters *>&1 | ForEach-Object { Send-Uitvoer $_ }
    Send-Opmaak
}
catch {
    Send-Opmaak
    $plaats = if ($_.InvocationInfo -and $_.InvocationInfo.ScriptLineNumber) { " (line $($_.InvocationInfo.ScriptLineNumber))" } else { '' }
    Send-Moss 'fout' @{ t = "$($_.Exception.Message)$plaats" }
    $code = 1
}

if ($code -eq 0 -and $script:Fouten -gt 0) { $code = 2 }
try { if ($diensten -contains 'exchange') { ExchangeOnlineManagement\Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue } } catch { }
exit $code
