<#
.SYNOPSIS
    Tests for Update-SmtpConfig, the part of "set-email" that rewrites config.json.

    That file holds the database connection string, passwords and the first-admin setting, so the rewrite must change
    only the email part and must round-trip every other value exactly. Run on Windows PowerShell 5.1, which is what the
    installer uses (its ConvertTo-Json has quirks with arrays and special characters).

    Usage:  powershell -File tests\installer\Smtp-Config.Tests.ps1      (exit code 0 = pass)
#>
param([string]$Installer)
$ErrorActionPreference = 'Stop'

$installerPath = if ($Installer) { (Resolve-Path $Installer).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '..\..\installer\FormRelay-Setup.ps1')).Path }
$tokens = $null; $errs = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installerPath, [ref]$tokens, [ref]$errs)
$fn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Update-SmtpConfig' }, $true) | Select-Object -First 1
if (-not $fn) { throw "Update-SmtpConfig not found in $installerPath" }
Invoke-Expression $fn.Extent.Text

$script:failures = 0
function Check([string]$Label, [bool]$Pass, [string]$Detail = '') {
    if ($Pass) { Write-Host "  PASS  $Label" -ForegroundColor Green }
    else { Write-Host "  FAIL  $Label  $Detail" -ForegroundColor Red; $script:failures++ }
}
# Load, change, serialize the way set-email does, and load again: the whole round trip.
function Apply([string]$Json, $Smtp) {
    $cfg = $Json | ConvertFrom-Json
    $cfg = Update-SmtpConfig $cfg $Smtp
    $out = $cfg | ConvertTo-Json -Depth 10
    return @{ Text = $out; Obj = ($out | ConvertFrom-Json) }
}

$base = @'
{
  "ConnectionStrings": { "Default": "Server=.\\SQLEXPRESS;Database=FormRelay;Integrated Security=true;TrustServerCertificate=true" },
  "Auth": { "BootstrapAdmins": [] },
  "Api": { "ReadKey": "k3y-with-dashes_and.dots" },
  "Email": { "Provider": "Smtp" },
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:8080" }, "Https": { "Url": "https://0.0.0.0:443", "Certificate": { "Subject": "CN=host", "Store": "My", "Location": "LocalMachine", "AllowInvalid": true } } } },
  "Connectors": { "Secrets": { "erp": "Server=erp01;User Id=ro;Password=p@ss;Encrypt=True" } }
}
'@
$oneAdmin = $base.Replace('"BootstrapAdmins": []', '"BootstrapAdmins": [ "OFFICE-PC\\mccle" ]')
$trickyPassword = 'p"a\ss''w<o>r&d é/{x}'

Write-Host "Update-SmtpConfig tests ($installerPath)"

# ---- adding email to a config that has none
$r = Apply $base @{ Host = 'smtp.office365.com'; Port = 587; UseTls = $true; User = 'me@example.com'; Password = 'secret'; From = 'me@example.com' }
Check 'adds the Smtp section' ($r.Obj.Smtp.Host -eq 'smtp.office365.com' -and $r.Obj.Smtp.User -eq 'me@example.com' -and $r.Obj.Smtp.From -eq 'me@example.com')
Check 'Port is a number and UseTls is a boolean in the JSON' ($r.Text -match '"Port":\s*587' -and $r.Text -match '"UseTls":\s*true') $r.Text
Check 'keeps the connection string exactly' ($r.Obj.ConnectionStrings.Default -ceq 'Server=.\SQLEXPRESS;Database=FormRelay;Integrated Security=true;TrustServerCertificate=true') $r.Obj.ConnectionStrings.Default
Check 'keeps an empty BootstrapAdmins list as a list' ($r.Text -match '"BootstrapAdmins":\s*\[\s*\]') $r.Text
Check 'keeps the nested Kestrel and certificate settings' ($r.Obj.Kestrel.Endpoints.Https.Certificate.Subject -eq 'CN=host' -and $r.Obj.Kestrel.Endpoints.Https.Certificate.AllowInvalid -eq $true -and $r.Obj.Kestrel.Endpoints.Http.Url -eq 'http://0.0.0.0:8080')
Check 'keeps the API key and connector secrets' ($r.Obj.Api.ReadKey -eq 'k3y-with-dashes_and.dots' -and $r.Obj.Connectors.Secrets.erp -eq 'Server=erp01;User Id=ro;Password=p@ss;Encrypt=True')

# ---- a single-element list must stay a list
$r = Apply $oneAdmin @{ Host = 'h'; Port = 25; UseTls = $false; From = 'a@b.c' }
Check 'keeps a one-item BootstrapAdmins list as a list' ($r.Text -match '"BootstrapAdmins":\s*\[\s*"OFFICE-PC\\\\mccle"\s*\]') $r.Text

# ---- passwords with awkward characters survive exactly
$r = Apply $base @{ Host = 'h'; Port = 587; UseTls = $true; User = 'u'; Password = $trickyPassword; From = 'a@b.c' }
Check 'a password with quotes, backslash, <, >, &, accents and braces round-trips exactly' ($r.Obj.Smtp.Password -ceq $trickyPassword) "got: $($r.Obj.Smtp.Password)"

# ---- replacing existing settings
$withSmtp = (Apply $base @{ Host = 'old.example.com'; Port = 25; UseTls = $false; User = 'olduser'; Password = 'oldpw'; From = 'old@example.com' }).Text
$r = Apply $withSmtp @{ Host = 'new.example.com'; Port = 587; UseTls = $true; From = 'new@example.com' }
Check 'replaces the Smtp section' ($r.Obj.Smtp.Host -eq 'new.example.com' -and $r.Obj.Smtp.Port -eq 587 -and $r.Obj.Smtp.From -eq 'new@example.com')
Check 'drops the old user name and password when the new setup has none' ($null -eq $r.Obj.Smtp.User -and $null -eq $r.Obj.Smtp.Password)

# ---- no empty values are written
$r = Apply $base @{ Host = 'h'; Port = 25; UseTls = $false; User = ''; Password = ''; From = '' }
Check 'writes no User, Password or From when they are blank' ($null -eq $r.Obj.Smtp.User -and $null -eq $r.Obj.Smtp.Password -and $null -eq $r.Obj.Smtp.From)

# ---- turning email off
$r = Apply $withSmtp $null
Check 'turning email off removes the Smtp section' ($null -eq $r.Obj.PSObject.Properties['Smtp']) $r.Text
Check '  and leaves the rest alone' ($r.Obj.ConnectionStrings.Default -ceq 'Server=.\SQLEXPRESS;Database=FormRelay;Integrated Security=true;TrustServerCertificate=true' -and $r.Obj.Api.ReadKey -eq 'k3y-with-dashes_and.dots')
$r = Apply $base $null
Check 'turning email off when it was never on is harmless' ($null -eq $r.Obj.PSObject.Properties['Smtp'])

# ---- provider handling
$graph = $base.Replace('"Provider": "Smtp"', '"Provider": "Graph"').Replace('"Api":', '"Graph": { "TenantId": "t", "ClientId": "c", "Sender": "s@x.y" }, "Api":')
$r = Apply $graph @{ Host = 'h'; Port = 25; UseTls = $false; From = 'a@b.c' }
Check 'switches the provider from Graph to Smtp' ($r.Obj.Email.Provider -eq 'Smtp')
Check '  and keeps the Graph settings in the file' ($r.Obj.Graph.TenantId -eq 't' -and $r.Obj.Graph.Sender -eq 's@x.y')
$noEmail = $base.Replace('"Email": { "Provider": "Smtp" },', '')
$r = Apply $noEmail @{ Host = 'h'; Port = 25; UseTls = $false; From = 'a@b.c' }
Check 'creates Email:Provider when the file has no Email section' ($r.Obj.Email.Provider -eq 'Smtp')

# ---- running it twice changes nothing more
$once = Apply $base @{ Host = 'h'; Port = 587; UseTls = $true; User = 'u'; Password = 'p'; From = 'a@b.c' }
$twice = Apply $once.Text @{ Host = 'h'; Port = 587; UseTls = $true; User = 'u'; Password = 'p'; From = 'a@b.c' }
Check 'applying the same settings twice gives identical JSON' ($once.Text -ceq $twice.Text)

if ($script:failures) { Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All passed.' -ForegroundColor Green
exit 0
