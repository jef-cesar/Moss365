<#MOSS365
Name: Mailgroepen vergelijken
Description: Vergelijkt de kenmerken en de leden van twee mailgroepen
Version: 1.0
Connection: Exchange
Writes: no
Variable: Group1 | Group | required | Eerste groep |  |
Variable: Group2 | Group | required | Tweede groep |  |
#>
<#
.SYNOPSIS
    Vergelijkt alle attributen van twee groepen (mail-enabled security groups)
    via Get-DistributionGroup, en toont ook het ledenaantal + leden verschil.

.EXAMPLE
    .\Compare-GroupAttributes.ps1 -Group1 "SG-SM-info2-Full" -Group2 "SG-SM-info-Full"
#>

param(
    [Parameter(Mandatory = $true)][string]$Group1,
    [Parameter(Mandatory = $true)][string]$Group2,
    [string]$ExportCsv
)

function Get-FlatGroupData {
    param([string]$Identity)

    Write-Host "Ophalen van gegevens voor $Identity ..." -ForegroundColor Cyan
    $result = [ordered]@{}

    $grp = Get-DistributionGroup -Identity $Identity -ErrorAction Stop
    foreach ($prop in $grp.PSObject.Properties) {
        $result["Group.$($prop.Name)"] = $prop.Value
    }

    $members = Get-DistributionGroupMember -Identity $Identity -ResultSize Unlimited
    $result["Members.Count"] = $members.Count
    $result["Members.List"] = ($members | Sort-Object Name | ForEach-Object { $_.PrimarySmtpAddress }) -join ", "

    return $result
}

$data1 = Get-FlatGroupData -Identity $Group1
$data2 = Get-FlatGroupData -Identity $Group2

$allKeys = @($data1.Keys) + @($data2.Keys) | Select-Object -Unique | Sort-Object

$comparison = foreach ($key in $allKeys) {
    $val1 = if ($data1.Contains($key)) { ($data1[$key] | Out-String).Trim() } else { "<niet aanwezig>" }
    $val2 = if ($data2.Contains($key)) { ($data2[$key] | Out-String).Trim() } else { "<niet aanwezig>" }

    [PSCustomObject]@{
        Attribute    = $key
        ($Group1)    = $val1
        ($Group2)    = $val2
        Verschillend = ($val1 -ne $val2)
    }
}

$differences = $comparison | Where-Object { $_.Verschillend }

Write-Host "`n=== Attributen die verschillen tussen $Group1 en $Group2 ===" -ForegroundColor Yellow
if ($differences.Count -eq 0) {
    Write-Host "Geen verschillen gevonden." -ForegroundColor Green
} else {
    $differences | Format-Table Attribute, $Group1, $Group2 -Wrap -AutoSize
    Write-Host "`nAantal verschillende attributen: $($differences.Count) van totaal $($comparison.Count)" -ForegroundColor Yellow
}

if ($ExportCsv) {
    $comparison | Export-Csv -Path $ExportCsv -NoTypeInformation -Encoding UTF8 -Delimiter ";"
    Write-Host "`nVolledige vergelijking weggeschreven naar: $ExportCsv" -ForegroundColor Cyan
}