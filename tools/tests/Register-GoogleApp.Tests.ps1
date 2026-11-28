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
            $global:LASTEXITCODE = 0
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

    It 'refuses a project id Google does not accept' {
        { & $script:Script -ProjectId 'Moss_365' 6>$null } | Should -Throw
    }

    It 'is ASCII only' {
        $bytes = [System.IO.File]::ReadAllBytes($script:Script)
        @($bytes | Where-Object { $_ -gt 127 }).Count | Should -Be 0
    }
}
