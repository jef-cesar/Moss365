<#MOSS365
Name: Postvakken vergelijken
Description: Vergelijkt alle kenmerken en rechten van twee postvakken en toont enkel de verschillen
Version: 1.0
Connection: Exchange
Writes: no
Variable: Mailbox1 | Mailbox | required | Eerste postvak (het goede) |  |
Variable: Mailbox2 | Mailbox | required | Tweede postvak (met het probleem) |  |
#>
<#
.SYNOPSIS
    Vergelijkt alle attributen van twee mailboxen (Get-Mailbox, Get-CASMailbox,
    Get-MailboxPermission en Get-RecipientPermission) en toont enkel de verschillen.

.DESCRIPTION
    Nuttig om te achterhalen waarom twee ogenschijnlijk identiek geconfigureerde
    gedeelde postvakken zich anders gedragen (bv. automapping, add-in gedrag, ...).

.PARAMETER Mailbox1
    Identity (e-mailadres of alias) van de eerste (referentie/werkende) mailbox.

.PARAMETER Mailbox2
    Identity (e-mailadres of alias) van de tweede (probleem) mailbox.

.PARAMETER ExportCsv
    Optioneel pad om de volledige vergelijking (incl. gelijke waarden) als CSV weg te schrijven.

.EXAMPLE
    .\Compare-MailboxAttributes.ps1 -Mailbox1 "info2@contoso.com" -Mailbox2 "info@contoso.com"

.EXAMPLE
    .\Compare-MailboxAttributes.ps1 -Mailbox1 "info2@contoso.com" -Mailbox2 "info@contoso.com" -ExportCsv "C:\Temp\vergelijking.csv"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Mailbox1,

    [Parameter(Mandatory = $true)]
    [string]$Mailbox2,

    [string]$ExportCsv
)

function Get-FlatMailboxData {
    param([string]$Identity)

    Write-Host "Ophalen van gegevens voor $Identity ..." -ForegroundColor Cyan

    $result = [ordered]@{}

    # --- Get-Mailbox: alle properties ---
    $mbx = Get-Mailbox -Identity $Identity -ErrorAction Stop
    foreach ($prop in $mbx.PSObject.Properties) {
        $result["Mailbox.$($prop.Name)"] = $prop.Value
    }

    # --- Get-CASMailbox: client access / protocol instellingen ---
    try {
        $cas = Get-CASMailbox -Identity $Identity -ErrorAction Stop
        foreach ($prop in $cas.PSObject.Properties) {
            $result["CASMailbox.$($prop.Name)"] = $prop.Value
        }
    } catch {
        Write-Warning "Get-CASMailbox mislukt voor $Identity : $_"
    }

    # --- Get-MailboxPermission: FullAccess/SendOnBehalf permissies ---
    try {
        $perms = Get-MailboxPermission -Identity $Identity |
            Where-Object { $_.User -notlike "NT AUTHORITY\SELF" } |
            ForEach-Object { "$($_.User) [$($_.AccessRights -join ',')] AutoMapping=$($_.AutoMapping)" }
        $result["MailboxPermission.List"] = ($perms -join " | ")
    } catch {
        Write-Warning "Get-MailboxPermission mislukt voor $Identity : $_"
    }

    # --- Get-RecipientPermission: Send As ---
    try {
        $sendAs = Get-RecipientPermission -Identity $Identity |
            Where-Object { $_.Trustee -notlike "NT AUTHORITY\SELF" } |
            ForEach-Object { "$($_.Trustee) [$($_.AccessRights -join ',')]" }
        $result["RecipientPermission.SendAs"] = ($sendAs -join " | ")
    } catch {
        Write-Warning "Get-RecipientPermission mislukt voor $Identity : $_"
    }

    return $result
}

# --- Data ophalen ---
$data1 = Get-FlatMailboxData -Identity $Mailbox1
$data2 = Get-FlatMailboxData -Identity $Mailbox2

# --- Alle unieke property-namen verzamelen (union van beide) ---
$allKeys = @($data1.Keys) + @($data2.Keys) | Select-Object -Unique | Sort-Object

# --- Vergelijken ---
$comparison = foreach ($key in $allKeys) {
    $val1 = if ($data1.Contains($key)) { $data1[$key] } else { "<niet aanwezig>" }
    $val2 = if ($data2.Contains($key)) { $data2[$key] } else { "<niet aanwezig>" }

    # Arrays/collecties leesbaar maken als tekst voor vergelijking
    $val1Str = ($val1 | Out-String).Trim()
    $val2Str = ($val2 | Out-String).Trim()

    [PSCustomObject]@{
        Attribute        = $key
        ($Mailbox1)      = $val1Str
        ($Mailbox2)      = $val2Str
        Verschillend     = ($val1Str -ne $val2Str)
    }
}

# --- Enkel verschillen tonen in de console ---
$differences = $comparison | Where-Object { $_.Verschillend -eq $true }

Write-Host "`n=== Attributen die verschillen tussen $Mailbox1 en $Mailbox2 ===" -ForegroundColor Yellow
if ($differences.Count -eq 0) {
    Write-Host "Geen verschillen gevonden." -ForegroundColor Green
} else {
    $differences | Format-Table Attribute, $Mailbox1, $Mailbox2 -Wrap -AutoSize
    Write-Host "`nAantal verschillende attributen: $($differences.Count) van totaal $($comparison.Count)" -ForegroundColor Yellow
}

# --- Optioneel: volledige vergelijking (incl. gelijke waarden) naar CSV ---
if ($ExportCsv) {
    $comparison | Export-Csv -Path $ExportCsv -NoTypeInformation -Encoding UTF8 -Delimiter ";"
    Write-Host "`nVolledige vergelijking weggeschreven naar: $ExportCsv" -ForegroundColor Cyan
}