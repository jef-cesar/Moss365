<#
.SYNOPSIS
    Opstart voor scripts uit de bibliotheek van Moss365 (ingebed in de app; ASCII-only).

.DESCRIPTION
    Verbindt met wat het script nodig heeft (Exchange Online, Microsoft Graph, AD), met de aanmelding van de app, en
    roept het script aan met de waarden uit het formulier. Alles wat het script schrijft of teruggeeft, gaat als een
    regel JSON naar de app (soort s: tekst, info, waarschuwing, fout, voortgang, rij).

    Geheimen: de tokens komen enkel via omgevingsvariabelen van dit proces (MOSS365_EXO_TOKEN, MOSS365_GRAPH_TOKEN) en
    het AD-wachtwoord via de invoer (stdin). Ze worden meteen uit de omgeving gehaald en nooit geschreven.

    Uitgangscodes: 0 gelukt, 1 gestopt met een fout, 2 klaar met fouten, 3 voorbeeld niet mogelijk, 4 verbinding mislukt.
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
    # Uitvoer van Format-Table/Format-List: als tekst, in een keer
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

# ---- Enkel controleren: syntaxfouten, zonder iets uit te voeren of te verbinden ----
if ($Controleer) {
    $fouten = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$fouten)
    foreach ($f in @($fouten)) {
        Send-Moss 'fout' @{ t = $f.Message; regel = $f.Extent.StartLineNumber; kolom = $f.Extent.StartColumnNumber }
    }
    exit 0
}

# ---- Geheimen uit de omgeving halen (nooit doorgeven of schrijven) ----
$exoToken = $env:MOSS365_EXO_TOKEN
$env:MOSS365_EXO_TOKEN = $null
$graphToken = $env:MOSS365_GRAPH_TOKEN
$env:MOSS365_GRAPH_TOKEN = $null
$adWachtwoordTekst = if ($AdWachtwoord) { [Console]::In.ReadLine() } else { $null }

$diensten = @($Verbinding -split '[,;+]' | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })

# ---- Hulp voor scripts: Microsoft Graph zonder de Graph-module (geen botsing met Exchange) ----
$global:MossGraphToken = $graphToken
function global:Invoke-MossGraph {
    <#
    .SYNOPSIS
        Microsoft Graph aanspreken met de aanmelding van Moss365, zonder de Graph-module.
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
    if (-not $global:MossGraphToken) { throw 'Geen Graph-verbinding: zet Graph bij de verbindingen van dit script.' }
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

# ---- Verbinden ----
try {
    # Eerst Graph, dan Exchange: elk laadt een eigen versie van MSAL, en in de omgekeerde volgorde botsen ze
    if ($diensten -contains 'graph') {
        if (-not $graphToken) { throw 'Geen aanmelding voor Microsoft Graph: meld aan in de app (Verbindingen).' }
        if (Get-Module -ListAvailable Microsoft.Graph.Authentication) {
            try {
                Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
                Connect-MgGraph -AccessToken (ConvertTo-SecureString $graphToken -AsPlainText -Force) -NoWelcome -ErrorAction Stop
                Send-Moss 'info' @{ t = 'Verbonden met Microsoft Graph.' }
            }
            catch {
                Send-Moss 'waarschuwing' @{ t = "De Graph-module kon niet verbinden ($($_.Exception.Message)); Invoke-MossGraph werkt wel." }
            }
        }
        else {
            Send-Moss 'info' @{ t = 'Microsoft Graph via Invoke-MossGraph (de Graph-module is niet geinstalleerd).' }
        }
        $global:MossUpn = $Upn
        function global:Get-MgContext { [pscustomobject]@{ Account = $global:MossUpn; AuthType = 'AccessToken'; ContextScope = 'Process' } }
    }

    if ($diensten -contains 'exchange') {
        if (-not $exoToken) { throw 'Geen aanmelding voor Exchange Online: meld aan in de app (Verbindingen).' }
        Import-Module ExchangeOnlineManagement -ErrorAction Stop
        $exo = @{ AccessToken = $exoToken; ShowBanner = $false }
        if ($Upn) { $exo['UserPrincipalName'] = $Upn }
        Connect-ExchangeOnline @exo
        Send-Moss 'info' @{ t = 'Verbonden met Exchange Online.' }
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
        Send-Moss 'info' @{ t = "Active Directory: $(if ($AdServer) { $AdServer } else { 'domeincontroller van deze pc' })." }
    }
}
catch {
    Send-Moss 'fout' @{ t = "Verbinden mislukt: $($_.Exception.Message)" }
    exit 4
}
finally {
    $adWachtwoordTekst = $null
    $exoToken = $null
}

# ---- Wat in de app niet kan of niet hoeft ----
function global:Connect-ExchangeOnline { Write-Verbose 'Connect-ExchangeOnline overgeslagen: Moss365 is al verbonden.' }
function global:Disconnect-ExchangeOnline { }
function global:Connect-MgGraph { Write-Verbose 'Connect-MgGraph overgeslagen: Moss365 is al verbonden.' }
function global:Disconnect-MgGraph { }
function global:Read-Host { throw 'Read-Host werkt niet in de app: maak er een variabele van (Bewerken > Variabelen).' }
function global:Write-Progress {
    param([Parameter(Position = 0)][string]$Activity, [Parameter(Position = 1)][string]$Status, [int]$PercentComplete = -1,
        [switch]$Completed, [int]$Id, [int]$ParentId, [string]$CurrentOperation, [int]$SecondsRemaining)
    Send-Moss 'voortgang' @{ t = (@($Activity, $Status, $CurrentOperation) | Where-Object { $_ }) -join ' - '; p = $PercentComplete; klaar = [bool]$Completed }
}

# ---- Waarden ----
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
            Send-Moss 'fout' @{ t = 'Dit script ondersteunt -WhatIf niet: er is geen voorbeeld mogelijk. Er werd niets uitgevoerd.' }
            exit 3
        }
        $parameters['WhatIf'] = $true
    }
}
catch {
    Send-Moss 'fout' @{ t = "Het script kon niet gestart worden: $($_.Exception.Message)" }
    exit 1
}
finally {
    Remove-Item -LiteralPath $Invoer -Force -ErrorAction SilentlyContinue
}

# ---- Uitvoeren ----
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
    $plaats = if ($_.InvocationInfo -and $_.InvocationInfo.ScriptLineNumber) { " (regel $($_.InvocationInfo.ScriptLineNumber))" } else { '' }
    Send-Moss 'fout' @{ t = "$($_.Exception.Message)$plaats" }
    $code = 1
}

if ($code -eq 0 -and $script:Fouten -gt 0) { $code = 2 }
try { if ($diensten -contains 'exchange') { ExchangeOnlineManagement\Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue } } catch { }
exit $code
