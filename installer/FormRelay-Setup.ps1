<#
.SYNOPSIS
    Installs, updates, checks, rolls back and removes FormRelay (a Kestrel-hosted Windows service).

.DESCRIPTION
    One script for the whole life of an install. Run it from an elevated Windows PowerShell prompt.

      install    First install: files, Windows service, database, settings, firewall, health check.
      update     Newer version over an existing install. Backs up, swaps files, runs new migrations,
                 health-checks, and rolls back by itself if the new version does not come up.
      check      Read-only. Reports what is wrong and changes nothing. Exit code 0 = ok, 1 = warnings, 2 = failures.
      rollback   Go back to the previous version's files (the database is not rolled back).
      uninstall  Remove the service, firewall rule and program files. Keeps settings and database unless -RemoveData.
      graph-cert Create a Microsoft Graph sign-in certificate on this server (private key never leaves it).
      test-email Send a test message with the SMTP settings in config.json, and say plainly what went wrong if it fails.
      set-email  Set up (or change, or turn off) outgoing email: asks the questions, saves them, restarts the service, sends a test.

    Where the files come from: a release zip unpacked next to this script (app\, db\, VERSION), a -PackagePath,
    or -Latest to download the newest GitHub release.

.EXAMPLE
    .\FormRelay-Setup.ps1 install -SqlServer ".\SQLEXPRESS" -AdminAccount "CONTOSO\jsmith" -WithSamples

.EXAMPLE
    .\FormRelay-Setup.ps1 update -Latest

.EXAMPLE
    .\FormRelay-Setup.ps1 check
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('install', 'update', 'check', 'rollback', 'uninstall', 'graph-cert', 'test-email', 'set-email')]
    [string]$Mode = 'check',

    # Where the new version comes from
    [string]$PackagePath,
    [switch]$Latest,
    [string]$Repo = 'cmcclea117-gif/FormRelay',
    [string]$GitHubToken,

    # Install settings (prompted for if missing, unless -NonInteractive). -ConfigFile takes the same names as JSON.
    [string]$ConfigFile,
    [string]$SqlServer,
    [string]$Database = 'FormRelay',
    [string]$AdminAccount,
    [string]$ServiceAccount = 'NT SERVICE\FormRelay',
    [int]$HttpPort = 8080,
    [int]$HttpsPort = 443,
    [string[]]$AllowFrom = @('LocalSubnet'),   # who may connect: LocalSubnet, or addresses/ranges like 10.0.0.0/8
    [switch]$AllowAnyNetwork,                  # open to any address on any network (not recommended)
    [switch]$LocalOnly,                        # listen on this computer only (127.0.0.1); no firewall rule at all
    [string]$CertThumbprint,
    [switch]$CreateSelfSignedCert,
    [string]$HostName = [System.Net.Dns]::GetHostEntry('').HostName,
    [ValidateSet('none', 'microsoft365', 'gmail', 'custom')][string]$SmtpPreset,
    [string]$SmtpHost,
    [int]$SmtpPort = 25,
    [switch]$SmtpUseTls,
    [string]$SmtpUser,
    [string]$SmtpPassword,
    [string]$SmtpFrom,
    [switch]$WithSamples,
    [string]$To,                       # test-email: where to send it

    # Where things live
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'FormRelay'),
    [string]$DataDir = (Join-Path $env:ProgramData 'FormRelay'),

    [switch]$NonInteractive,
    [switch]$Yes,                      # skip the "Continue?" question (the plan is still printed)
    [switch]$Force,
    [switch]$RemoveData,
    [switch]$SkipDatabaseBackup
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'FormRelay'
$ScriptBound = $PSBoundParameters   # functions have their own $PSBoundParameters; this is the script's

# SqlClient ships with Windows PowerShell 5.1 only, so hop over if we were started in PowerShell 7.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $fwd = @($Mode)
    foreach ($k in $PSBoundParameters.Keys) {
        if ($k -eq 'Mode') { continue }
        $v = $PSBoundParameters[$k]
        if ($v -is [switch]) { if ($v.IsPresent) { $fwd += "-$k" } } else { $fwd += "-$k"; $fwd += "$v" }
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @fwd
    exit $LASTEXITCODE
}

# ------------------------------------------------------------------------------------------ helpers

$script:Checks = New-Object System.Collections.ArrayList
function Add-Check([string]$Name, [ValidateSet('ok', 'warn', 'fail')][string]$Status, [string]$Detail) {
    [void]$script:Checks.Add([pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail })
}
function Write-Checks {
    foreach ($c in $script:Checks) {
        $colour = @{ ok = 'Green'; warn = 'Yellow'; fail = 'Red' }[$c.Status]
        Write-Host ('  [{0,-4}] ' -f $c.Status.ToUpper()) -ForegroundColor $colour -NoNewline
        Write-Host ('{0,-26} {1}' -f $c.Name, $c.Detail)
    }
    $fails = @($script:Checks | Where-Object Status -eq 'fail').Count
    $warns = @($script:Checks | Where-Object Status -eq 'warn').Count
    Write-Host ''
    Write-Host ("  {0} failed, {1} warning(s), {2} ok" -f $fails, $warns, (@($script:Checks | Where-Object Status -eq 'ok').Count))
    if ($fails) { return 2 } elseif ($warns) { return 1 } else { return 0 }
}
function Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Test-Admin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Ask([string]$Prompt, [string]$Default, [switch]$Required) {
    if ($NonInteractive) {
        if ($Required -and -not $Default) { throw "Missing required setting: $Prompt (pass it as a parameter or in -ConfigFile)" }
        return $Default
    }
    $suffix = if ($Default) { " [$Default]" } else { '' }
    do { $a = Read-Host "$Prompt$suffix"; if (-not $a) { $a = $Default } } while ($Required -and -not $a)
    return $a
}
function Get-Sid([string]$Account) { (New-Object Security.Principal.NTAccount($Account)).Translate([Security.Principal.SecurityIdentifier]).Value }

# ----- SQL (System.Data.SqlClient, built into Windows PowerShell)
function New-SqlConn([string]$ConnStr) { $c = New-Object System.Data.SqlClient.SqlConnection($ConnStr); $c.Open(); $c }
function Invoke-SqlScript([string]$ConnStr, [string]$Sql) {
    $conn = New-SqlConn $ConnStr
    try {
        foreach ($batch in [regex]::Split($Sql, '(?im)^\s*GO\s*$')) {
            if (-not $batch.Trim()) { continue }
            $cmd = $conn.CreateCommand(); $cmd.CommandText = $batch; $cmd.CommandTimeout = 120
            [void]$cmd.ExecuteNonQuery()
        }
    } finally { $conn.Dispose() }
}
function Get-SqlScalar([string]$ConnStr, [string]$Sql) {
    $conn = New-SqlConn $ConnStr
    try { $cmd = $conn.CreateCommand(); $cmd.CommandText = $Sql; $cmd.CommandTimeout = 30; $cmd.ExecuteScalar() } finally { $conn.Dispose() }
}
function Quote-Id([string]$Name) { '[' + $Name.Replace(']', ']]') + ']' }

# ----- install state
function Get-ConfigPath { Join-Path $DataDir 'config.json' }
function Read-Config { if (Test-Path (Get-ConfigPath)) { Get-Content (Get-ConfigPath) -Raw | ConvertFrom-Json } else { $null } }
function Get-CurrentVersion {
    $f = Join-Path $InstallDir 'current\VERSION'
    if (Test-Path $f) { (Get-Content $f -Raw).Trim() } else { $null }
}
function Get-BaseUrl {
    $cfg = Read-Config
    $url = $null
    if ($cfg -and $cfg.Kestrel -and $cfg.Kestrel.Endpoints) {
        $e = $cfg.Kestrel.Endpoints
        if ($e.Http -and $e.Http.Url) { $url = $e.Http.Url }
    }
    if (-not $url) { $url = 'http://0.0.0.0:8080' }
    $url -replace '://(0\.0\.0\.0|\*|\+)', '://localhost'
}
function Test-Health([int]$TimeoutSeconds = 60) {
    $url = (Get-BaseUrl) + '/api/health'
    $until = (Get-Date).AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        try { return Invoke-RestMethod -Uri $url -TimeoutSec 5 -UseBasicParsing } catch { $last = $_.Exception.Message; Start-Sleep -Seconds 2 }
    } while ((Get-Date) -lt $until)
    throw "Health check failed ($url): $last"
}

# ----- the package (where the new version comes from)
function Resolve-Package {
    if ($Latest) {
        Step "Looking up the latest release of $Repo"
        $h = @{ 'User-Agent' = 'FormRelay-Setup'; Accept = 'application/vnd.github+json' }
        if ($GitHubToken) { $h.Authorization = "Bearer $GitHubToken" }
        $rel = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -Headers $h
        $zipAsset = $rel.assets | Where-Object { $_.name -like 'FormRelay-*-win-x64.zip' } | Select-Object -First 1
        if (-not $zipAsset) { throw "Release $($rel.tag_name) has no FormRelay-*-win-x64.zip asset" }
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ("formrelay-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $tmp | Out-Null
        $zip = Join-Path $tmp $zipAsset.name
        $dl = @{ 'User-Agent' = 'FormRelay-Setup'; Accept = 'application/octet-stream' }
        if ($GitHubToken) { $dl.Authorization = "Bearer $GitHubToken" }
        Invoke-WebRequest -Uri $zipAsset.url -Headers $dl -OutFile $zip -UseBasicParsing
        $sumAsset = $rel.assets | Where-Object { $_.name -eq ($zipAsset.name + '.sha256') } | Select-Object -First 1
        if ($sumAsset) {
            $sumFile = Join-Path $tmp $sumAsset.name
            Invoke-WebRequest -Uri $sumAsset.url -Headers $dl -OutFile $sumFile -UseBasicParsing
            $want = ((Get-Content $sumFile -Raw).Trim() -split '\s+')[0]
            $sha = [Security.Cryptography.SHA256]::Create(); $fs = [IO.File]::OpenRead($zip)
            try { $got = ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '') } finally { $fs.Dispose() }
            if ($want -ne $got.ToLower() -and $want.ToLower() -ne $got.ToLower()) { throw "Checksum mismatch for $($zipAsset.name). Expected $want, got $got. Not installing." }
            Write-Host "    checksum ok"
        } else { Write-Warning 'The release has no .sha256 file; the download could not be verified.' }
        $PackagePath = $zip
        $script:CleanupDir = $tmp
    }
    if (-not $PackagePath) { $PackagePath = $PSScriptRoot }
    if ($PackagePath -like '*.zip') {
        $dest = Join-Path ([IO.Path]::GetTempPath()) ("formrelay-x-" + [guid]::NewGuid().ToString('N'))
        Expand-Archive -Path $PackagePath -DestinationPath $dest
        $script:CleanupDir2 = $dest
        $PackagePath = $dest
    }
    foreach ($need in 'app\FormRelay.Api.exe', 'db', 'VERSION') {
        if (-not (Test-Path (Join-Path $PackagePath $need))) {
            throw "Not a FormRelay package: '$need' not found under $PackagePath. Run from an unpacked release zip, or pass -PackagePath or -Latest."
        }
    }
    return $PackagePath
}
function Remove-Temp { foreach ($d in $script:CleanupDir, $script:CleanupDir2) { if ($d -and (Test-Path $d)) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue } } }

# ----- database
function Get-SqlConnStr([string]$Db = 'master') { "Server=$SqlServer;Database=$Db;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=15" }
function Get-DbPrincipal {
    # A virtual service account authenticates to a remote SQL Server as this machine's account (DOMAIN\MACHINE$).
    if ($ServiceAccount -like 'NT SERVICE\*') {
        $local = @('.', 'localhost', '(local)', $env:COMPUTERNAME)
        $server = ($SqlServer -split '\\')[0]
        if ($local -contains $server -or $server -like '127.*') { return $ServiceAccount }
        return "$env:USERDOMAIN\$env:COMPUTERNAME$"
    }
    return $ServiceAccount
}
function Initialize-Database([string]$PackageRoot) {
    if ($Database -notmatch '^[A-Za-z0-9_]+$') { throw "Database name '$Database' may use only letters, numbers and underscores" }
    Step "Database $Database on $SqlServer"
    Invoke-SqlScript (Get-SqlConnStr) "IF DB_ID(N'$Database') IS NULL CREATE DATABASE $(Quote-Id $Database);"
    $p = Get-DbPrincipal
    $q = Quote-Id $p
    $qdb = Quote-Id $Database
    Invoke-SqlScript (Get-SqlConnStr) @"
IF SUSER_ID(N'$($p.Replace("'", "''"))') IS NULL CREATE LOGIN $q FROM WINDOWS;
"@
    Invoke-SqlScript (Get-SqlConnStr $Database) @"
IF DATABASE_PRINCIPAL_ID(N'$($p.Replace("'", "''"))') IS NULL CREATE USER $q FOR LOGIN $q;
ALTER ROLE db_datareader ADD MEMBER $q;
ALTER ROLE db_datawriter ADD MEMBER $q;
"@
    Write-Host "    $p can read and write $Database (and nothing else)"
}
function Invoke-Migrations([string]$PackageRoot) {
    Step 'Database migrations'
    $cs = Get-SqlConnStr $Database
    Invoke-SqlScript $cs "IF OBJECT_ID('dbo.SchemaVersion') IS NULL CREATE TABLE dbo.SchemaVersion (Version INT NOT NULL PRIMARY KEY, Name NVARCHAR(200) NOT NULL, AppliedUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());"
    $applied = @{}
    $conn = New-SqlConn $cs
    try {
        $cmd = $conn.CreateCommand(); $cmd.CommandText = 'SELECT Version FROM dbo.SchemaVersion'
        $r = $cmd.ExecuteReader(); while ($r.Read()) { $applied[[int]$r[0]] = $true }; $r.Close()
    } finally { $conn.Dispose() }
    $files = Get-ChildItem (Join-Path $PackageRoot 'db') -Filter '*.sql' -File | Where-Object Name -match '^\d{3}_' | Sort-Object Name
    $ran = 0
    foreach ($f in $files) {
        $ver = [int]($f.Name.Substring(0, 3))
        if ($applied.ContainsKey($ver)) { continue }
        Write-Host "    applying $($f.Name)"
        Invoke-SqlScript $cs (Get-Content $f.FullName -Raw)
        $name = $f.BaseName.Replace("'", "''")
        Invoke-SqlScript $cs "IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersion WHERE Version = $ver) INSERT dbo.SchemaVersion (Version, Name) VALUES ($ver, N'$name');"
        $ran++
    }
    if (-not $ran) { Write-Host '    already up to date' }
    if ($WithSamples) {
        $samples = Join-Path $PackageRoot 'db\samples'
        if (Test-Path $samples) {
            foreach ($f in Get-ChildItem $samples -Filter '*.sql' | Sort-Object Name) {
                Write-Host "    sample data: $($f.Name)"
                Invoke-SqlScript $cs (Get-Content $f.FullName -Raw)
            }
        }
    }
    return ($files | ForEach-Object { [int]$_.Name.Substring(0, 3) } | Measure-Object -Maximum).Maximum
}
function Backup-Database {
    if ($SkipDatabaseBackup) { return }
    Step 'Database backup (copy-only)'
    try {
        $dir = Get-SqlScalar (Get-SqlConnStr) "SELECT CONVERT(NVARCHAR(500), SERVERPROPERTY('InstanceDefaultBackupPath'))"
        if (-not $dir) { throw 'no default backup path' }
        $file = Join-Path $dir ("$Database-before-update-{0:yyyyMMdd-HHmmss}.bak" -f (Get-Date))
        Invoke-SqlScript (Get-SqlConnStr) "BACKUP DATABASE $(Quote-Id $Database) TO DISK = N'$($file.Replace("'", "''"))' WITH COPY_ONLY, INIT;"
        Write-Host "    $file"
    } catch { Write-Warning "Could not back up the database ($($_.Exception.Message)). Continuing; use -SkipDatabaseBackup to silence this." }
}

# ----- files, service, firewall
function Set-Junction([string]$Link, [string]$Target) {
    if (Test-Path $Link) { cmd /c rmdir "`"$Link`"" | Out-Null }
    New-Item -ItemType Junction -Path $Link -Target $Target | Out-Null
}
function Install-Files([string]$PackageRoot, [string]$Version) {
    Step "Program files ($Version)"
    $rel = Join-Path $InstallDir "releases\$Version"
    if (Test-Path $rel) { Remove-Item $rel -Recurse -Force }
    New-Item -ItemType Directory -Path $rel -Force | Out-Null
    Copy-Item (Join-Path $PackageRoot 'app\*') $rel -Recurse -Force
    Copy-Item (Join-Path $PackageRoot 'VERSION') $rel -Force
    # Keep the installer next to the program so "update" is always at hand, and take it from the NEW package so
    # the installer itself updates too. (Copying the running script onto itself is an error, so skip that case.)
    $self = Join-Path $PackageRoot 'FormRelay-Setup.ps1'
    if (-not (Test-Path $self)) { $self = $PSCommandPath }
    try { Copy-Item $self (Join-Path $InstallDir 'FormRelay-Setup.ps1') -Force }
    catch { if ($_.Exception.Message -notmatch 'with itself') { throw } }   # same file under another spelling: nothing to do
    $launcher = Join-Path $PackageRoot 'FormRelay-Setup.cmd'
    if (Test-Path $launcher) {
        try { Copy-Item $launcher (Join-Path $InstallDir 'FormRelay-Setup.cmd') -Force }
        catch { if ($_.Exception.Message -notmatch 'with itself') { throw } }
    }
    return $rel
}
function Set-DataDirAcl {
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    foreach ($sub in 'logs', 'backups') { New-Item -ItemType Directory -Path (Join-Path $DataDir $sub) -Force | Out-Null }
    & icacls $DataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null   # SYSTEM, Administrators
    & icacls $DataDir /grant "${ServiceAccount}:(OI)(CI)M" | Out-Null
}
function Install-Service([string]$ReleaseDir) {
    Step 'Windows service'
    $exe = Join-Path $InstallDir 'current\FormRelay.Api.exe'
    $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'FormRelay' `
            -Description 'FormRelay forms and email notifications' -StartupType Automatic | Out-Null
    }
    $obj = if ($ServiceAccount -like 'NT SERVICE\*') { $ServiceAccount } else { $ServiceAccount }
    & sc.exe config $ServiceName obj= $obj | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
    & sc.exe config $ServiceName start= delayed-auto | Out-Null
}
function Get-FirewallScope {
    if ($LocalOnly) { return @{ Profile = 'Any'; Remote = @('Any'); Text = 'no firewall port is opened; FormRelay listens on this computer only' } }
    if ($AllowAnyNetwork) { return @{ Profile = 'Any'; Remote = @('Any'); Text = 'ANY address on ANY network (not recommended)' } }
    return @{ Profile = 'Domain,Private'; Remote = $AllowFrom; Text = "only $($AllowFrom -join ', ') on Domain and Private networks (never Public networks)" }
}
function Set-Firewall {
    Step 'Firewall'
    $ports = @($HttpPort)
    if ($CertThumbprint) { $ports += $HttpsPort }
    $scope = Get-FirewallScope
    Get-NetFirewallRule -DisplayName 'FormRelay' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if ($LocalOnly) { Write-Host "    $($scope.Text)"; return }
    New-NetFirewallRule -DisplayName 'FormRelay' -Direction Inbound -Protocol TCP -LocalPort $ports -Action Allow `
        -Profile $scope.Profile -RemoteAddress $scope.Remote | Out-Null
    Write-Host "    inbound TCP $($ports -join ', '): $($scope.Text)"
    if ($AllowAnyNetwork) { Write-Warning 'FormRelay is now reachable from any address. Put it behind HTTPS and a firewall or reverse proxy before anyone outside your network can reach this PC.' }
    $pub = Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object { $_.NetworkCategory -eq 'Public' -and $_.IPv4Connectivity -ne 'Disconnected' }
    if ($pub -and -not $AllowAnyNetwork) {
        Write-Host "    Note: this PC's network '$(@($pub)[0].Name)' is classed Public, so other computers cannot reach FormRelay yet." -ForegroundColor Yellow
        Write-Host '    If it is a trusted network, change it to Private (Settings > Network & internet), then it will work.' -ForegroundColor Yellow
    }
}
function Confirm-Plan([string]$Title, [string[]]$Lines) {
    Write-Host ''
    Write-Host $Title -ForegroundColor Cyan
    foreach ($l in $Lines) { Write-Host "  - $l" }
    Write-Host ''
    if ($Yes -or $NonInteractive) { return }
    if ((Read-Host 'Continue? [y/N]') -notlike 'y*') { Write-Host 'Cancelled. Nothing was changed.'; exit 0 }
}
function Grant-CertKeyAccess([System.Security.Cryptography.X509Certificates.X509Certificate2]$Cert, [string]$Account) {
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Cert)
    if (-not $rsa) { throw 'The certificate has no RSA private key on this machine.' }
    $name = if ($rsa -is [System.Security.Cryptography.RSACng]) { $rsa.Key.UniqueName } else { $rsa.CspKeyContainerInfo.UniqueKeyContainerName }
    $roots = @("$env:ProgramData\Microsoft\Crypto\Keys", "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys")
    $file = foreach ($r in $roots) { Get-ChildItem $r -Filter $name -ErrorAction SilentlyContinue | Select-Object -First 1 }
    $file = $file | Select-Object -First 1
    if (-not $file) { throw "Could not find the private key file for the certificate (looked for $name)." }
    & icacls $file.FullName /grant "${Account}:R" | Out-Null
    Write-Host "    $Account can read the private key"
}

# ----- outgoing email (SMTP). Presets fill in the server details for common providers.
function Read-Secret([string]$Prompt) {
    if ($NonInteractive) { return $null }
    $s = Read-Host $Prompt -AsSecureString
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}
function Get-SmtpAnswers([switch]$Change) {
    if ($SmtpPreset -eq 'none') { return }
    if (-not $SmtpPreset -and -not $SmtpHost) {
        if ($NonInteractive) { return }
        Write-Host ''
        Write-Host 'Outgoing email (how FormRelay sends the notifications):'
        if ($Change) { Write-Host '  1. Turn email off (notifications are held until a mail server is set)' }
        else { Write-Host '  1. Skip for now (forms work fine; add email later)' }
        Write-Host '  2. Microsoft 365 / Office 365'
        Write-Host '  3. Gmail / Google Workspace'
        Write-Host '  4. My own mail server'
        $script:SmtpPreset = switch (Ask 'Choose 1-4' '1') { '2' { 'microsoft365' } '3' { 'gmail' } '4' { 'custom' } default { 'none' } }
        if ($SmtpPreset -eq 'none') { return }
    }
    switch ($SmtpPreset) {
        'microsoft365' {
            $script:SmtpHost = 'smtp.office365.com'; $script:SmtpPort = 587; $script:SmtpUseTls = $true
            Write-Host '    Needs a mailbox with "Authenticated SMTP" enabled. Microsoft is phasing out password-based SMTP in some'
            Write-Host '    tenants; if sending is refused, use the Graph option instead (docs/install.md).'
        }
        'gmail' {
            $script:SmtpHost = 'smtp.gmail.com'; $script:SmtpPort = 587; $script:SmtpUseTls = $true
            Write-Host '    Needs 2-step verification on the account and an "app password" (not your normal password).'
        }
        default {
            if (-not $SmtpHost) { $script:SmtpHost = Ask 'Mail server name' '' -Required }
            if (-not $ScriptBound.ContainsKey('SmtpPort')) { $script:SmtpPort = [int](Ask 'Port' "$SmtpPort") }
            if (-not $SmtpUseTls -and -not $NonInteractive) { $script:SmtpUseTls = ((Ask 'Encrypt with TLS (STARTTLS)? y/n' 'n') -like 'y*') }
        }
    }
    if (-not $SmtpUser -and -not $NonInteractive) {
        $script:SmtpUser = Ask 'Mail account user name (blank if the server needs none)' ''
        if ($SmtpUser -and -not $SmtpPassword) { $script:SmtpPassword = Read-Secret 'Password (typing is hidden)' }
    }
    if (-not $SmtpFrom) { $script:SmtpFrom = Ask 'Send email from (address)' $(if ($SmtpUser -like '*@*') { $SmtpUser } else { "formrelay@$env:USERDNSDOMAIN".ToLower() }) }
    Write-Host "    Email will go through $SmtpHost`:$SmtpPort. After install, send yourself a test: FormRelay-Setup.ps1 test-email -To you@example.com"
}
function Invoke-TestEmail {
    $cfg = Read-Config
    if (-not $cfg) { throw "No settings file at $(Get-ConfigPath). Install first." }
    $s = $cfg.Smtp
    if (-not $s -or -not $s.Host) { throw 'No mail server is set. Add one under "Smtp" in config.json (see docs/install.md), then try again.' }
    $dest = $To
    if (-not $dest) { $dest = Ask 'Send the test email to' '' -Required }
    $from = if ($s.From) { $s.From } else { "formrelay@$env:COMPUTERNAME" }
    $port = if ($s.Port) { [int]$s.Port } else { 25 }
    Write-Host "Sending from $from to $dest through $($s.Host):$port (TLS: $([bool]$s.UseTls))..."
    $client = New-Object System.Net.Mail.SmtpClient($s.Host, $port)
    $client.EnableSsl = [bool]$s.UseTls
    $client.Timeout = 20000
    if ($s.User) { $client.Credentials = New-Object System.Net.NetworkCredential($s.User, $s.Password) }
    try {
        $client.Send($from, $dest, 'FormRelay test email', "This is a test from FormRelay on $env:COMPUTERNAME. If you can read it, email is set up correctly.")
        Write-Host "Sent. Check the inbox of $dest (and its spam folder)." -ForegroundColor Green
    } catch {
        $why = $_.Exception.GetBaseException().Message
        $hint = switch -Regex ($why) {
            'authentication|5\.7\.|Authentication Required|credentials' { 'The server wants a user name and password it accepts. Check Smtp:User and Smtp:Password. Gmail needs an app password.' }
            'connect|actively refused|timed out|No such host|resolve' { 'Could not reach the server. Check Smtp:Host and Smtp:Port, and that this computer may connect out on that port.' }
            'STARTTLS|SSL|TLS|secure' { 'The server and TLS setting disagree. Port 587 normally needs "UseTls": true; port 25 often needs it false.' }
            default { 'See the message above.' }
        }
        throw "Could not send: $why`n$hint"
    } finally { $client.Dispose() }
}

# ----- change the email settings of an existing install
# Returns $Config with only its Smtp (and Email:Provider) parts changed; $Smtp = $null removes the email settings.
# Kept free of side effects so it can be tested on its own (tests\installer\Smtp-Config.Tests.ps1).
function Update-SmtpConfig($Config, $Smtp) {
    $has = { param($name) $null -ne $Config.PSObject.Properties[$name] }
    if ($null -eq $Smtp) {
        if (& $has 'Smtp') { [void]$Config.PSObject.Properties.Remove('Smtp') }
        return $Config
    }
    $obj = [ordered]@{ Host = $Smtp.Host; Port = [int]$Smtp.Port; UseTls = [bool]$Smtp.UseTls }
    if ($Smtp.User) { $obj.User = $Smtp.User; $obj.Password = $Smtp.Password }
    if ($Smtp.From) { $obj.From = $Smtp.From }
    $value = [pscustomobject]$obj
    if (& $has 'Smtp') { $Config.Smtp = $value } else { $Config | Add-Member -NotePropertyName Smtp -NotePropertyValue $value }
    if (& $has 'Email') {
        if ($null -ne $Config.Email.PSObject.Properties['Provider']) { $Config.Email.Provider = 'Smtp' }
        else { $Config.Email | Add-Member -NotePropertyName Provider -NotePropertyValue 'Smtp' }
    } else { $Config | Add-Member -NotePropertyName Email -NotePropertyValue ([pscustomobject]@{ Provider = 'Smtp' }) }
    return $Config
}
function Invoke-SetEmail {
    if (-not (Test-Admin)) { throw 'Run this from an elevated (Run as administrator) prompt.' }
    $cfgPath = Get-ConfigPath
    $cfg = Read-Config
    if (-not $cfg) { throw "No settings file at $cfgPath. Install FormRelay first." }
    if ($NonInteractive -and -not $SmtpPreset -and -not $SmtpHost) {
        throw 'With -NonInteractive, pass -SmtpPreset (none, microsoft365, gmail or custom) and, for custom, -SmtpHost and the other details.'
    }

    Write-Host ''
    if ($cfg.Smtp -and $cfg.Smtp.Host) { Write-Host "Email is currently sent through $($cfg.Smtp.Host):$($cfg.Smtp.Port)." }
    else { Write-Host 'Email is not set up yet.' }
    if ("$($cfg.Email.Provider)" -eq 'Graph') { Write-Host 'This install is set to send through Microsoft Graph. Choosing an SMTP server will switch it to SMTP.' -ForegroundColor Yellow }

    Get-SmtpAnswers -Change
    $off = ($SmtpPreset -eq 'none') -or -not $SmtpHost
    $lines = @()
    if ($off) {
        $lines += 'Turn outgoing email off. Notifications are held in the outbox (nothing is lost) until a mail server is set'
    } else {
        $lines += "Send email through ${SmtpHost}:${SmtpPort} (TLS: $([bool]$SmtpUseTls)), from $SmtpFrom"
        if ($SmtpUser) { $lines += "Sign in to the mail server as $SmtpUser (the password is saved in the settings file)" }
        $lines += 'Notifications that were waiting will go out'
    }
    $lines += "Change only the email part of $cfgPath, after keeping a backup copy"
    $lines += 'Restart the FormRelay service (a few seconds)'
    Confirm-Plan 'Email settings will be changed:' $lines

    New-Item -ItemType Directory -Path (Join-Path $DataDir 'backups') -Force | Out-Null
    Copy-Item $cfgPath (Join-Path (Join-Path $DataDir 'backups') ("config-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))) -Force
    $smtp = if ($off) { $null } else { @{ Host = $SmtpHost; Port = $SmtpPort; UseTls = [bool]$SmtpUseTls; User = $SmtpUser; Password = $SmtpPassword; From = $SmtpFrom } }
    $cfg = Update-SmtpConfig $cfg $smtp
    $cfg | ConvertTo-Json -Depth 10 | Set-Content -Path $cfgPath -Encoding UTF8
    if (-not (Read-Config)) { throw "The settings file could not be read back after saving. Your backup is in $(Join-Path $DataDir 'backups')." }
    Write-Host "    saved $cfgPath"

    Step 'Restarting the service'
    Restart-Service $ServiceName
    [void](Test-Health 60)
    Write-Host '    healthy' -ForegroundColor Green
    if ($off) { Write-Host 'Email is off. Run set-email again when you have a mail server.'; return }

    $dest = $To
    if (-not $dest -and -not $NonInteractive) { $dest = Ask 'Send a test email to (blank to skip)' '' }
    if ($dest) {
        $script:To = $dest
        try { Invoke-TestEmail }
        catch {
            Write-Host "Your settings are saved, but the test email failed:`n$($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host 'Fix the details with set-email again, or look at them in config.json.'
            exit 1
        }
    } else { Write-Host 'Done. To check it later: FormRelay-Setup.ps1 test-email -To you@example.com' }
}

# ----- settings file
function New-RandomKey { $b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', 'a').Replace('/', 'b') }
function Write-Config {
    $cfgPath = Get-ConfigPath
    if ((Test-Path $cfgPath) -and -not $Force) {
        Write-Host "    keeping existing settings ($cfgPath). Use -Force to rewrite."
        return
    }
    if (Test-Path $cfgPath) { Copy-Item $cfgPath (Join-Path $DataDir ("backups\config-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))) }
    $conn = "Server=$SqlServer;Database=$Database;Integrated Security=true;TrustServerCertificate=true"
    $bind = if ($LocalOnly) { '127.0.0.1' } else { '0.0.0.0' }
    $kestrel = @{ Http = @{ Url = "http://${bind}:$HttpPort" } }
    if ($CertThumbprint) {
        $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Thumbprint -eq ($CertThumbprint -replace '\s', '').ToUpper() | Select-Object -First 1
        if (-not $cert) { throw "Certificate $CertThumbprint is not in LocalMachine\My" }
        $subject = $cert.Subject
        $kestrel.Https = @{ Url = "https://${bind}:$HttpsPort"; Certificate = @{ Subject = $subject; Store = 'My'; Location = 'LocalMachine'; AllowInvalid = $true } }
        Grant-CertKeyAccess $cert $ServiceAccount
    }
    $cfg = [ordered]@{
        ConnectionStrings = @{ Default = $conn }
        Auth              = @{ BootstrapAdmins = @($AdminAccount) }
        Api               = @{ ReadKey = (New-RandomKey) }
        Email             = @{ Provider = 'Smtp' }
        Kestrel           = @{ Endpoints = $kestrel }
    }
    # Skipped email = no Smtp section at all (an empty host would count as a real, broken server).
    if ($SmtpHost) {
        $smtp = [ordered]@{ Host = $SmtpHost; Port = $SmtpPort; UseTls = [bool]$SmtpUseTls }
        if ($SmtpUser) { $smtp.User = $SmtpUser; $smtp.Password = $SmtpPassword }
        if ($SmtpFrom) { $smtp.From = $SmtpFrom }
        $cfg.Smtp = $smtp
    }
    $cfg | ConvertTo-Json -Depth 8 | Set-Content -Path $cfgPath -Encoding UTF8
    Write-Host "    wrote $cfgPath"
}
function Import-InstallAnswers {
    if (-not $ConfigFile) { return }
    $a = Get-Content $ConfigFile -Raw | ConvertFrom-Json
    foreach ($p in $a.PSObject.Properties) {
        if (-not $ScriptBound.ContainsKey($p.Name) -and (Get-Variable -Name $p.Name -Scope Script -ErrorAction SilentlyContinue)) {
            Set-Variable -Name $p.Name -Value $p.Value -Scope Script
        }
    }
}
function Get-InstallAnswers {
    Import-InstallAnswers
    $script:SqlServer = Ask 'SQL Server (name or name\instance)' $(if ($SqlServer) { $SqlServer } else { '.\SQLEXPRESS' }) -Required
    $script:Database = Ask 'Database name' $Database -Required
    $me = "$env:USERDOMAIN\$env:USERNAME"
    $script:AdminAccount = Ask 'First administrator (DOMAIN\user, becomes admin on first sign-in)' $(if ($AdminAccount) { $AdminAccount } else { $me }) -Required
    Get-SmtpAnswers
    if ($CreateSelfSignedCert -and -not $CertThumbprint) {
        $c = New-SelfSignedCertificate -DnsName $HostName -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(2)
        $script:CertThumbprint = $c.Thumbprint
        Write-Host "    created a self-signed certificate for $HostName ($($c.Thumbprint)). Browsers will warn about it; use a real one for production."
    }
    if ($CertThumbprint -and -not $ScriptBound.ContainsKey('HttpPort')) { $script:HttpPort = 80 }
}

# ------------------------------------------------------------------------------------------ check
function Invoke-Check {
    Write-Host "FormRelay check ($env:COMPUTERNAME)" -ForegroundColor Cyan
    Add-Check 'Windows' ($(if ([Environment]::OSVersion.Version.Major -ge 10) { 'ok' } else { 'warn' })) ([Environment]::OSVersion.VersionString)
    Add-Check 'Elevated' ($(if (Test-Admin) { 'ok' } else { 'warn' })) ($(if (Test-Admin) { 'running as administrator' } else { 'not elevated; some checks (service ACLs, certificates) may be incomplete' }))

    $ver = Get-CurrentVersion
    if (-not $ver) { Add-Check 'Installed version' 'fail' "no install found under $InstallDir"; return (Write-Checks) }
    Add-Check 'Installed version' 'ok' $ver
    $exe = Join-Path $InstallDir 'current\FormRelay.Api.exe'
    Add-Check 'Program files' ($(if (Test-Path $exe) { 'ok' } else { 'fail' })) $exe

    $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { Add-Check 'Service' 'fail' "service '$ServiceName' does not exist" }
    else {
        Add-Check 'Service' ($(if ($svc.Status -eq 'Running') { 'ok' } else { 'fail' })) "$($svc.Status), start type $($svc.StartType)"
        if ($svc.StartType -eq 'Disabled') { Add-Check 'Service start type' 'fail' 'disabled' }
    }

    $cfg = Read-Config
    if (-not $cfg) { Add-Check 'Settings file' 'fail' "$(Get-ConfigPath) is missing or not valid JSON"; return (Write-Checks) }
    Add-Check 'Settings file' 'ok' (Get-ConfigPath)
    $conn = $cfg.ConnectionStrings.Default
    if (-not $conn) { Add-Check 'Connection string' 'fail' 'ConnectionStrings:Default is not set (the app would fall back to .\SQLEXPRESS)' }
    else {
        try {
            $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($conn)
            $ver2 = Get-SqlScalar "$conn;Connect Timeout=10" 'SELECT MAX(Version) FROM dbo.SchemaVersion'
            Add-Check 'Database (as installer)' 'ok' "$($b.DataSource) / $($b.InitialCatalog), schema version $ver2"
            $pkg = Join-Path $PSScriptRoot 'db'
            if (Test-Path $pkg) {
                $newest = (Get-ChildItem $pkg -Filter '*.sql' | Where-Object Name -match '^\d{3}_' | ForEach-Object { [int]$_.Name.Substring(0, 3) } | Measure-Object -Maximum).Maximum
                if ($newest -and $ver2 -is [int] -and $ver2 -lt $newest) { Add-Check 'Schema' 'warn' "database is at $ver2, this package carries $newest. Run update." }
            }
        } catch { Add-Check 'Database (as installer)' 'fail' $_.Exception.Message.Split("`n")[0] }
    }

    if (-not ($cfg.Auth -and @($cfg.Auth.BootstrapAdmins).Count)) { Add-Check 'Administrators' 'warn' 'Auth:BootstrapAdmins is empty (fine once real admins exist)' }
    if ($env:ASPNETCORE_ENVIRONMENT) { Add-Check 'Environment variable' 'warn' "ASPNETCORE_ENVIRONMENT=$($env:ASPNETCORE_ENVIRONMENT) is set machine-wide; remove it on a server" }

    $http = Get-BaseUrl
    $port = ([Uri]$http).Port
    # Ask the port itself (the Windows network-status API can come back empty even when the app is answering).
    $tcp = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $tcp.BeginConnect('127.0.0.1', $port, $null, $null)
        $open = $iar.AsyncWaitHandle.WaitOne(3000) -and $tcp.Connected
    } catch { $open = $false } finally { $tcp.Close() }
    Add-Check "Port $port" ($(if ($open) { 'ok' } else { 'fail' })) ($(if ($open) { 'accepting connections' } else { 'nothing answers (service stopped, or another program owns the port)' }))
    $fw = Get-NetFirewallRule -DisplayName 'FormRelay' -ErrorAction SilentlyContinue
    if ("$($cfg.Kestrel.Endpoints.Http.Url)" -like 'http://127.*') { Add-Check 'Firewall rule' 'ok' 'FormRelay listens on this computer only (-LocalOnly), so no firewall port is open' }
    elseif (-not $fw) { Add-Check 'Firewall rule' 'warn' 'no inbound rule; other computers cannot reach the site (fine if only this PC uses it)' }
    else {
        $remote = @(($fw | Get-NetFirewallAddressFilter).RemoteAddress)
        $profiles = "$($fw.Profile)"
        $wide = ($remote -contains 'Any') -or ($remote -contains '*')
        $pub = ($profiles -match 'Any|Public')
        if ($wide -or $pub) {
            Add-Check 'Firewall rule' 'warn' ('open to ' + $(if ($wide) { 'any address' } else { 'the local network' }) + $(if ($pub) { ', including Public networks (cafe or guest Wi-Fi)' } else { '' }) + '. Internal-only is the safe default: re-run install -Force, or see docs/security.md.')
        } else { Add-Check 'Firewall rule' 'ok' ("allows only $($remote -join ', ') on $profiles networks") }
    }
    if (-not $cfg.Kestrel.Endpoints.Https) { Add-Check 'Encryption' 'ok' 'plain HTTP: fine on a trusted internal network. Add HTTPS (docs/install.md) before anyone outside it uses this.' }

    try {
        $h = Test-Health 5
        Add-Check 'Health endpoint' 'ok' "status $($h.status)"
        try {
            $d = Invoke-RestMethod -Uri ((Get-BaseUrl) + '/api/diagnostics') -UseDefaultCredentials -TimeoutSec 20 -UseBasicParsing
            foreach ($c in $d) { if ($c.name -ne 'Version') { Add-Check "app: $($c.name)" $c.status $c.detail } }
        } catch { Add-Check 'App self-check' 'warn' "could not read /api/diagnostics as $env:USERDOMAIN\$env:USERNAME (needs an admin account); health only" }
    } catch { Add-Check 'Health endpoint' 'fail' $_.Exception.Message }

    $cert = $cfg.Kestrel.Endpoints.Https
    if ($cert) {
        $subject = $cert.Certificate.Subject
        $c = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq $subject } | Sort-Object NotAfter -Descending | Select-Object -First 1
        if (-not $c) { Add-Check 'HTTPS certificate' 'fail' "no certificate with subject '$subject' in LocalMachine\My" }
        else {
            $days = [int]($c.NotAfter - (Get-Date)).TotalDays
            Add-Check 'HTTPS certificate' ($(if ($days -lt 0) { 'fail' } elseif ($days -lt 30) { 'warn' } else { 'ok' })) "expires $($c.NotAfter.ToString('yyyy-MM-dd')) ($days days)"
        }
    }
    $drive = Get-PSDrive -Name ($InstallDir.Substring(0, 1)) -ErrorAction SilentlyContinue
    if ($drive) { Add-Check 'Disk space' ($(if ($drive.Free -lt 1GB) { 'warn' } else { 'ok' })) ("{0:N1} GB free on {1}:" -f ($drive.Free / 1GB), $drive.Name) }
    return (Write-Checks)
}

# ------------------------------------------------------------------------------------------ modes
function Invoke-Install {
    if (-not (Test-Admin)) { throw 'Run this from an elevated (Run as administrator) prompt.' }
    if ((Get-CurrentVersion) -and -not $Force) { throw "FormRelay $(Get-CurrentVersion) is already installed. Use 'update' to upgrade, or -Force to reinstall." }
    $pkg = Resolve-Package
    $ver = (Get-Content (Join-Path $pkg 'VERSION') -Raw).Trim()
    Get-InstallAnswers

    $ports = @($HttpPort); if ($CertThumbprint) { $ports += $HttpsPort }
    Confirm-Plan "FormRelay $ver will make these changes on $env:COMPUTERNAME:" @(
        "Copy the program to $InstallDir",
        "Create a Windows service 'FormRelay' that runs as $ServiceAccount. That account has no administrator rights: it can only read the program and its own settings.",
        "Create the database '$Database' on $SqlServer, and give $(Get-DbPrincipal) read and write access to that database only",
        "Save settings (including any passwords) in $DataDir, readable only by Administrators, SYSTEM and the service",
        $(if ($LocalOnly) { 'Open no firewall port: FormRelay will listen on this computer only (127.0.0.1)' } else { "Open Windows Firewall for TCP $($ports -join ', '): $((Get-FirewallScope).Text)" }),
        "It sends nothing anywhere. It only reaches the internet if you later choose 'update from GitHub', or set up outgoing email"
    )

    Step "Installing FormRelay $ver"
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $rel = Install-Files $pkg $ver
    Set-Junction (Join-Path $InstallDir 'current') $rel
    Install-Service $rel                       # the service exists before the database login that refers to it
    Initialize-Database $pkg
    [void](Invoke-Migrations $pkg)
    Step 'Settings'
    Set-DataDirAcl
    Write-Config
    Set-Firewall

    Step 'Starting the service'
    Start-Service $ServiceName
    $h = Test-Health 90
    Write-Host '    healthy' -ForegroundColor Green
    Write-Host ''
    $code = Invoke-Check
    Write-Host ''
    if ($LocalOnly) { $HostName = 'localhost' }
    $base = if ($CertThumbprint) { "https://$HostName" + $(if ($HttpsPort -ne 443) { ":$HttpsPort" }) } else { "http://$HostName" + $(if ($HttpPort -ne 80) { ":$HttpPort" }) }
    Write-Host "FormRelay is running at $base" -ForegroundColor Green
    Write-Host "Sign in as $AdminAccount to become the first administrator, then remove Auth:BootstrapAdmins from $(Get-ConfigPath)."
    Write-Host "Settings: $(Get-ConfigPath)    Logs: Event Viewer > Application > FormRelay    Update later: $InstallDir\FormRelay-Setup.cmd (choose 2)"
    if ($code -ge 2) { exit 2 }
}

function Invoke-Update {
    if (-not (Test-Admin)) { throw 'Run this from an elevated (Run as administrator) prompt.' }
    $old = Get-CurrentVersion
    if (-not $old) { throw "No install found under $InstallDir. Use 'install' first." }
    $cfg = Read-Config
    if (-not $cfg) { throw "Settings file $(Get-ConfigPath) is missing; refusing to update blind." }
    $pkg = Resolve-Package
    $new = (Get-Content (Join-Path $pkg 'VERSION') -Raw).Trim()
    if ($new -eq $old -and -not $Force) { Write-Host "Already on $old. Nothing to do (use -Force to reinstall it)."; return }
    if ([version]($new -replace '[-+].*$', '') -lt [version]($old -replace '[-+].*$', '') -and -not $Force) { throw "Package $new is older than installed $old. Use -Force to downgrade." }

    # Settings come from the saved config, not from prompts.
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($cfg.ConnectionStrings.Default)
    $script:SqlServer = $b.DataSource; $script:Database = $b.InitialCatalog

    Confirm-Plan "FormRelay will be updated $old -> ${new}:" @(
        'Back up the database (copy-only) and your settings first',
        'Stop the service for a moment, swap in the new program files, apply any new database migrations, and start it again',
        'If the new version does not come up healthy, switch back to the old files automatically',
        'Your settings and data are kept'
    )
    Step "Updating FormRelay $old -> $new"
    Backup-Database
    Copy-Item (Get-ConfigPath) (Join-Path $DataDir ("backups\config-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))) -Force
    $oldRel = (Get-Item (Join-Path $InstallDir 'current')).Target
    if ($oldRel -is [array]) { $oldRel = $oldRel[0] }

    Step 'Stopping the service'
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    (Get-Service $ServiceName -ErrorAction SilentlyContinue).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    try {
        $rel = Install-Files $pkg $new
        Set-Junction (Join-Path $InstallDir 'current') $rel
        [void](Invoke-Migrations $pkg)
        Step 'Starting the service'
        Start-Service $ServiceName
        $h = Test-Health 90
        Write-Host "    healthy (now running $(Get-CurrentVersion))" -ForegroundColor Green
    } catch {
        Write-Host "Update failed: $($_.Exception.Message)" -ForegroundColor Red
        Step "Rolling back to $old"
        Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
        if ($oldRel -and (Test-Path $oldRel)) { Set-Junction (Join-Path $InstallDir 'current') $oldRel }
        Start-Service $ServiceName -ErrorAction SilentlyContinue
        try { [void](Test-Health 60); Write-Host "    $old is running again. The database keeps any migrations that already ran (they are backward compatible)." -ForegroundColor Yellow }
        catch { Write-Host "    $old did not come back either. Run '$PSCommandPath check' and see Event Viewer." -ForegroundColor Red }
        exit 2
    }
    # keep the last three releases
    Get-ChildItem (Join-Path $InstallDir 'releases') -Directory | Sort-Object CreationTime -Descending | Select-Object -Skip 3 |
        Where-Object { $_.FullName -ne $rel -and $_.FullName -ne $oldRel } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host ''
    $code = Invoke-Check
    Write-Host "`nFormRelay is now on $new." -ForegroundColor Green
    if ($code -ge 2) { exit 2 }
}

function Invoke-Rollback {
    if (-not (Test-Admin)) { throw 'Run this from an elevated (Run as administrator) prompt.' }
    $cur = Get-CurrentVersion
    $others = Get-ChildItem (Join-Path $InstallDir 'releases') -Directory | Where-Object Name -ne $cur | Sort-Object CreationTime -Descending
    if (-not $others) { throw 'There is no earlier version on disk to go back to.' }
    $target = $others | Select-Object -First 1
    Step "Rolling back $cur -> $($target.Name)"
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    Set-Junction (Join-Path $InstallDir 'current') $target.FullName
    Start-Service $ServiceName
    $h = Test-Health 90
    Write-Host "Now running $(Get-CurrentVersion). The database was not changed." -ForegroundColor Green
}

function Invoke-Uninstall {
    if (-not (Test-Admin)) { throw 'Run this from an elevated (Run as administrator) prompt.' }
    Confirm-Plan 'FormRelay will be removed from this computer:' @(
        'Stop and delete the Windows service and its firewall rule',
        "Delete the program files in $InstallDir",
        $(if ($RemoveData) { "ALSO DELETE your settings in $DataDir (the database is kept)" } else { "Keep your settings in $DataDir and the database" })
    )
    Step 'Removing FormRelay'
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
    }
    Get-NetFirewallRule -DisplayName 'FormRelay' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    $cur = Join-Path $InstallDir 'current'
    if (Test-Path $cur) { cmd /c rmdir "`"$cur`"" | Out-Null }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    if ($RemoveData -and (Test-Path $DataDir)) { Remove-Item $DataDir -Recurse -Force; Write-Host '    settings removed' }
    else { Write-Host "    kept settings in $DataDir and the database. Delete them yourself, or re-run with -RemoveData (settings only)." }
}

function Invoke-GraphCert {
    if (-not (Test-Admin)) { throw 'Run this from an elevated (Run as administrator) prompt.' }
    Step 'Creating a Microsoft Graph sign-in certificate on this server'
    $subject = "CN=FormRelay Graph Mailer ($env:COMPUTERNAME)"
    $cert = New-SelfSignedCertificate -Subject $subject -CertStoreLocation Cert:\LocalMachine\My -KeyExportPolicy NonExportable `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeySpec Signature -NotAfter (Get-Date).AddYears(2)
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    $cer = Join-Path $DataDir "formrelay-graph-$($cert.Thumbprint.Substring(0, 8)).cer"
    [void](Export-Certificate -Cert $cert -FilePath $cer)
    Grant-CertKeyAccess $cert $ServiceAccount
    Write-Host ''
    Write-Host "Thumbprint : $($cert.Thumbprint)"
    Write-Host "Expires    : $($cert.NotAfter.ToString('yyyy-MM-dd'))  (put a reminder in your calendar; mail stops silently after this)"
    Write-Host "Public file: $cer   <- send THIS to your Entra admin (docs/admin-request-graph-mailer.md)"
    Write-Host ''
    Write-Host 'Then add this to config.json (Settings file) and restart the service:'
    Write-Host '  "Email": { "Provider": "Graph" },'
    Write-Host "  `"Graph`": { `"TenantId`": `"...`", `"ClientId`": `"...`", `"Sender`": `"mailbox@yourdomain`", `"CertificateThumbprint`": `"$($cert.Thumbprint)`" }"
}

# ------------------------------------------------------------------------------------------ main
try {
    if ($Mode -ne 'check') {
        New-Item -ItemType Directory -Path (Join-Path $DataDir 'logs') -Force -ErrorAction SilentlyContinue | Out-Null
        try { Start-Transcript -Path (Join-Path $DataDir ("logs\setup-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))) | Out-Null } catch { }
    }
    switch ($Mode) {
        'install'    { Invoke-Install }
        'update'     { Invoke-Update }
        'check'      { $rc = Invoke-Check; exit $rc }
        'rollback'   { Invoke-Rollback }
        'uninstall'  { Invoke-Uninstall }
        'graph-cert' { Invoke-GraphCert }
        'test-email' { Invoke-TestEmail }
        'set-email'  { Invoke-SetEmail }
    }
} catch {
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
} finally {
    Remove-Temp
    try { Stop-Transcript | Out-Null } catch { }
}
