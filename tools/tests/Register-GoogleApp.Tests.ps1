#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Pester tests for Register-GoogleApp.ps1 (G1): gcloud is faked.

BeforeAll {
    $script:Script = Join-Path $PSScriptRoot '..\Register-GoogleApp.ps1'
}

Describe 'Register-GoogleApp' {
    BeforeEach {
        $global:GcloudAanroepen = [System.Collections.Generic.List[string]]::new()
        $global:GcloudBestaat = $false
        function global:gcloud {
            $global:GcloudAanroepen.Add(($args -join ' '))
            if ($args[0] -eq 'projects' -and $args[1] -eq 'describe') { $global:LASTEXITCODE = $(if ($global:GcloudBestaat) { 0 } else { 1 }); return }
            if ($args[0] -eq 'iam' -and $args[1] -eq 'service-accounts' -and $args[2] -eq 'describe' -and $args -notcontains '--format=value(oauth2ClientId)') { $global:LASTEXITCODE = $(if ($global:GcloudBestaat) { 0 } else { 1 }); return }
            $global:LASTEXITCODE = 0
            if ($args -contains '--format=value(oauth2ClientId)') { return '1234567890' }
        }
    }

    AfterEach {
        Remove-Item function:global:gcloud -ErrorAction SilentlyContinue
    }

    It 'creates the project and turns on the APIs' {
        & $script:Script -ProjectId 'moss365-contoso' 6>$null
        $global:GcloudAanroepen | Should -Contain 'projects create moss365-contoso --name=Moss365'
        $global:GcloudAanroepen | Should -Contain 'services enable admin.googleapis.com licensing.googleapis.com groupssettings.googleapis.com alertcenter.googleapis.com gmail.googleapis.com --project=moss365-contoso'
    }

    It 'does not create a project that exists already' {
        $global:GcloudBestaat = $true
        & $script:Script -ProjectId 'moss365-contoso' 6>$null
        @($global:GcloudAanroepen | Where-Object { $_ -like 'projects create*' }).Count | Should -Be 0
        @($global:GcloudAanroepen | Where-Object { $_ -like 'services enable admin.googleapis.com*' }).Count | Should -Be 1
    }

    It 'makes no service account without -Gmail' {
        & $script:Script -ProjectId 'moss365-contoso' 6>$null
        @($global:GcloudAanroepen | Where-Object { $_ -like 'iam *' }).Count | Should -Be 0
    }

    It 'creates the service account and its key with -Gmail and shows the client id' {
        $uit = & $script:Script -ProjectId 'moss365-contoso' -Gmail -Sleutelbestand 'sleutel.json' 6>&1 | Out-String
        $global:GcloudAanroepen | Should -Contain 'iam service-accounts create moss365-gmail --display-name=Moss365 Gmail settings --project=moss365-contoso'
        $global:GcloudAanroepen | Should -Contain 'iam service-accounts keys create sleutel.json --iam-account=moss365-gmail@moss365-contoso.iam.gserviceaccount.com --project=moss365-contoso'
        $uit | Should -Match '1234567890'
        $uit | Should -Match 'gmail.settings.sharing'
    }

    It 'uses a service account that exists already' {
        $global:GcloudBestaat = $true
        & $script:Script -ProjectId 'moss365-contoso' -Gmail 6>$null
        @($global:GcloudAanroepen | Where-Object { $_ -like 'iam service-accounts create*' }).Count | Should -Be 0
        @($global:GcloudAanroepen | Where-Object { $_ -like 'iam service-accounts keys create*' }).Count | Should -Be 1
    }

    It 'refuses a project id Google does not accept' {
        { & $script:Script -ProjectId 'Moss_365' 6>$null } | Should -Throw
    }

    It 'is ASCII only' {
        $bytes = [System.IO.File]::ReadAllBytes($script:Script)
        @($bytes | Where-Object { $_ -gt 127 }).Count | Should -Be 0
    }
}
