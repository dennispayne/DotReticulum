#Requires -Version 7.0
[CmdletBinding()]
param([string] $Version = '5.7.1', [int] $Attempts = 4)

$ErrorActionPreference = 'Stop'
if (Get-Module -ListAvailable Pester | Where-Object { $_.Version -eq [version] $Version }) { return }
for ($attempt = 1; ; $attempt++) {
    try {
        if (!(Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue)) { Register-PSRepository -Default }
        Install-Module Pester -RequiredVersion $Version -Scope CurrentUser -Force -SkipPublisherCheck -Repository PSGallery -ErrorAction Stop
        return
    }
    catch {
        if ($attempt -ge $Attempts) { throw }
        Write-Warning "Pester install attempt $attempt failed: $($_.Exception.Message)"
        Start-Sleep -Seconds (10 * $attempt)
    }
}
