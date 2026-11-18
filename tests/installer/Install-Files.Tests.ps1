<#
.SYNOPSIS
    Regression test for the installer's Install-Files step, run the way an update runs it.

    Found by a real update: 0.1.1 copied its own running script onto itself when "update" was started from the
    installed copy, so the update failed and rolled back. This builds fake install and package folders, writes a
    "running installer" script that contains the real Install-Files function (so $PSCommandPath is a real file, as it
    is in the real installer), runs it, and checks the cases that matter. Windows PowerShell 5.1 or later.

    Usage:  powershell -File tests\installer\Install-Files.Tests.ps1      (exit code 0 = pass)
#>
param(
    # Test a specific installer file instead of the one in this repo (used to prove the test fails on a buggy one).
    [string]$Installer
)
$ErrorActionPreference = 'Stop'

$installerPath = if ($Installer) { (Resolve-Path $Installer).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '..\..\installer\FormRelay-Setup.ps1')).Path }

$tokens = $null; $errs = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installerPath, [ref]$tokens, [ref]$errs)
$fn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Install-Files' }, $true) | Select-Object -First 1
if (-not $fn) { throw "Install-Files not found in $installerPath" }
$functionText = $fn.Extent.Text

$work = Join-Path ([IO.Path]::GetTempPath()) ('fr-installfiles-' + [guid]::NewGuid().ToString('N'))
$script:failures = 0

# The script that plays "the running installer". $InstallDir is passed in because in the real installer it is a parameter.
function New-Runner([string]$Path, [string]$InstallDir) {
    $text = @"
param([string]`$PackageRoot)
`$ErrorActionPreference = 'Stop'
function Step(`$t) { }
`$InstallDir = '$InstallDir'
$functionText
try { [void](Install-Files `$PackageRoot '9.9.9'); Write-Output 'RESULT: ok' }
catch { Write-Output "RESULT: FAILED `$(`$_.Exception.Message)" }
"@
    Set-Content -Path $Path -Value $text
}

function Run-Case([string]$Name, [bool]$PackageHasScript, [bool]$RunFromInstalled) {
    $base = Join-Path $work $Name
    $inst = Join-Path $base 'inst'; $pkg = Join-Path $base 'pkg'
    New-Item -ItemType Directory -Force (Join-Path $pkg 'app'), $inst | Out-Null
    Set-Content (Join-Path $pkg 'app\a.txt') 'x'
    Set-Content (Join-Path $pkg 'VERSION') '9.9.9'
    Set-Content (Join-Path $pkg 'FormRelay-Setup.cmd') '@echo new launcher'
    if ($PackageHasScript) { Set-Content (Join-Path $pkg 'FormRelay-Setup.ps1') '# NEW script from the package' }

    # An update runs the OLD script that is already installed; a first install runs the script from the unzipped package.
    $runner = if ($RunFromInstalled) { Join-Path $inst 'FormRelay-Setup.ps1' } elseif ($PackageHasScript) { Join-Path $pkg 'FormRelay-Setup.ps1' } else { Join-Path $base 'runner.ps1' }
    New-Runner $runner $inst
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -PackageRoot $pkg 2>&1 | Out-String

    $installed = Join-Path $inst 'FormRelay-Setup.ps1'
    return @{
        Ok        = [bool]($out -match 'RESULT: ok')
        Out       = $out.Trim()
        Installed = $(if (Test-Path $installed) { (Get-Content $installed -TotalCount 1) } else { $null })
        Launcher  = (Test-Path (Join-Path $inst 'FormRelay-Setup.cmd'))
    }
}

function Check([string]$Label, [bool]$Pass, [string]$Detail) {
    if ($Pass) { Write-Host "  PASS  $Label" -ForegroundColor Green }
    else { Write-Host "  FAIL  $Label  $Detail" -ForegroundColor Red; $script:failures++ }
}

try {
    Write-Host "Install-Files regression tests ($installerPath)"

    # The new package carries its own script; the OLD installed script is the one running (what an update does).
    $r = Run-Case 'update-from-installed' $true $true
    Check 'an update started from the installed copy succeeds' $r.Ok $r.Out
    Check '  and the installer is replaced by the new package script' ($r.Installed -ceq '# NEW script from the package') "installed script now starts with: $($r.Installed)"
    Check '  and the launcher is copied' $r.Launcher ''

    # First install: the script running is the package's own, in the package folder.
    $r = Run-Case 'install-from-package' $true $false
    Check 'a first install run from the package folder succeeds' $r.Ok $r.Out
    Check '  and the installer is copied to the install folder' ($null -ne $r.Installed) 'no installer in the install folder'

    # An older or odd package without a script, updated from the installed copy.
    $r = Run-Case 'package-without-script' $false $true
    Check 'a package without a script, run from the installed copy, succeeds' $r.Ok $r.Out
    Check '  and the launcher is still copied' $r.Launcher ''
}
finally { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }

if ($script:failures) { Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All passed.' -ForegroundColor Green
exit 0
