<#
.SYNOPSIS
    Builds the release zip: FormRelay-<version>-win-x64.zip (+ .sha256). Used by CI/CD and runnable locally.

    The zip is self-contained (the .NET runtime is inside), so the target machine needs no Hosting Bundle or SDK.
    Layout:  app\  db\  FormRelay-Setup.ps1  VERSION  README.txt
#>
param(
    [Parameter(Mandatory)][string]$Version,          # e.g. 0.1.0 or 0.2.0-beta.1 (no leading "v")
    [string]$Output,
    [switch]$SkipWeb                                   # reuse an existing web build
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path (Join-Path $here '..')).Path
if (-not $Output) { $Output = Join-Path $root 'artifacts' }
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$') { throw "Version '$Version' is not like 1.2.3 or 1.2.3-beta.1" }

New-Item -ItemType Directory -Force $Output | Out-Null
$Output = (Resolve-Path $Output).Path
$stage = Join-Path $Output "stage-$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

if (-not $SkipWeb) {
    Write-Host '==> web' -ForegroundColor Cyan
    Push-Location (Join-Path $root 'web')
    try { npm ci; if ($LASTEXITCODE) { throw 'npm ci failed' }; npm run build; if ($LASTEXITCODE) { throw 'npm run build failed' } } finally { Pop-Location }
}

Write-Host '==> publish' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src\FormRelay.Api\FormRelay.Api.csproj') -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:InformationalVersion=$Version -o (Join-Path $stage 'app')
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

Remove-Item (Join-Path $stage 'app\web.config') -ErrorAction SilentlyContinue   # an IIS file; Kestrel does not use it
Write-Host '==> assemble' -ForegroundColor Cyan
Copy-Item (Join-Path $root 'db') (Join-Path $stage 'db') -Recurse
Copy-Item (Join-Path $root 'installer\FormRelay-Setup.ps1') $stage
Set-Content -Path (Join-Path $stage 'VERSION') -Value $Version -NoNewline
@"
FormRelay $Version

Install (from an elevated Windows PowerShell prompt, in this folder):
  .\FormRelay-Setup.ps1 install -SqlServer ".\SQLEXPRESS" -AdminAccount "DOMAIN\you"

Check an existing install (changes nothing):   .\FormRelay-Setup.ps1 check
Update later:                                  C:\Program Files\FormRelay\FormRelay-Setup.ps1 update -Latest

Docs: https://github.com/cmcclea117-gif/FormRelay
"@ | Set-Content (Join-Path $stage 'README.txt')

$zip = Join-Path $Output "FormRelay-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$sha = [Security.Cryptography.SHA256]::Create()
$fs = [IO.File]::OpenRead($zip)
try { $hash = ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '').ToLower() } finally { $fs.Dispose() }
"$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -NoNewline
Remove-Item $stage -Recurse -Force
Write-Host "==> $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)`n    sha256 $hash" -ForegroundColor Green
