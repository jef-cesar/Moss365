<#MOSS365
Name: Verzonden items bewaren in gedeelde postvakken
Description: Zet voor alle gedeelde postvakken dat mail verzonden als of namens het postvak ook in het postvak bewaard blijft
Version: 1.0
Connection: Exchange
Writes: yes
#>
<#
.SYNOPSIS
    Zet voor alle gedeelde postvakken MessageCopyForSentAsEnabled en MessageCopyForSendOnBehalfEnabled aan.
    Met -WhatIf zie je wat er zou gebeuren.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param()

# Install module if needed (run once)
# Install-Module ExchangeOnlineManagement -Scope CurrentUser

# Moss365 connects to Exchange Online before the script runs; outside the app, connect first yourself
if (-not (Get-Command Get-Mailbox -ErrorAction SilentlyContinue)) { throw 'Geen verbinding met Exchange Online: start dit script vanuit Moss365, of verbind eerst zelf.' }

# Get all shared mailboxes
$sharedMailboxes = Get-Mailbox -RecipientTypeDetails SharedMailbox

# Loop through each shared mailbox and apply settings
foreach ($mailbox in $sharedMailboxes) {
    Write-Host "Processing:" $mailbox.DisplayName

    Set-Mailbox $mailbox.Identity `
        -MessageCopyForSentAsEnabled $true `
        -MessageCopyForSendOnBehalfEnabled $true
}

Write-Host "Completed updating all shared mailboxes."
