<#MOSS365
Name: Rooms koppelen in Microsoft Places
Description: Maakt gebouwen en verdiepingen aan in Microsoft Places en koppelt de rooms met een gebouw eraan
Version: 1.0
Connection: Exchange
Writes: yes
#>
<#
.SYNOPSIS
    Koppelt rooms met gebouwinformatie aan Microsoft Places (gebouwen en verdiepingen aanmaken). Met -WhatIf toont het
    enkel welke rooms het zou koppelen.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param()

# 1. Gather info first
$ExchangeRooms = Get-PlaceV3 -Type Room | Where-Object { $_.Building -ne $null }

if ($ExchangeRooms.Count -eq 0) {
    Write-Warning "No rooms found with Building information. Please set Building info using Set-Place first."
    return
}

# 2. Display what we found
Write-Host "Found $($ExchangeRooms.Count) rooms ready to be linked to Microsoft Places:" -ForegroundColor Cyan
$ExchangeRooms | Select-Object DisplayName, Building, FloorLabel | Out-Host

# 3. Confirmation Question
if ($WhatIfPreference) {
    Write-Host 'Voorbeeld: bovenstaande rooms zouden gekoppeld worden; er wordt niets aangemaakt.'
    return
}
# You confirm in Moss365 after the preview; outside the app, run it with -WhatIf first

# 4. Process with Verbose output
foreach ($Room in $ExchangeRooms) {
    Write-Verbose "Starting processing for $($Room.DisplayName)" -Verbose
    
    # --- BUILDING LOGIC ---
    $BuildingName = $Room.Building
    $ExistingBuilding = Get-PlaceV3 -Type Building | Where-Object { $_.DisplayName -eq $BuildingName }
    
    if (-not $ExistingBuilding) {
        Write-Host "  [NEW] Creating Building: $BuildingName" -ForegroundColor Cyan
        $Building = New-Place -Type Building -Name $BuildingName
        $BuildingId = $Building.PlaceId
    } else {
        Write-Verbose "  [SKIP] Building '$BuildingName' already exists." -Verbose
        $BuildingId = $ExistingBuilding.PlaceId
    }

    # --- FLOOR LOGIC ---
    $FloorName = if ($Room.FloorLabel) { $Room.FloorLabel } else { "0" }
    $ExistingFloor = Get-PlaceV3 -Type Floor | Where-Object { $_.ParentId -eq $BuildingId -and $_.DisplayName -eq $FloorName }
    
    if (-not $ExistingFloor) {
        Write-Host "  [NEW] Creating Floor: $FloorName in $BuildingName" -ForegroundColor Green
        $Floor = New-Place -Type Floor -Name $FloorName -ParentId $BuildingId
        $FloorId = $Floor.PlaceId
    } else {
        Write-Verbose "  [SKIP] Floor '$FloorName' already exists in this building." -Verbose
        $FloorId = $ExistingFloor.PlaceId
    }

    # --- LINKING LOGIC ---
    if ($Room.ParentId -ne $FloorId) {
        Write-Host "  [LINK] Linking $($Room.DisplayName) to Floor $FloorName" -ForegroundColor Blue
        Set-PlaceV3 -Identity $Room.PlaceId -ParentId $FloorId
    } else {
        Write-Verbose "  [OK] $($Room.DisplayName) is already correctly linked." -Verbose
    }
}

Write-Host "`nProcessing complete!" -ForegroundColor Magentat