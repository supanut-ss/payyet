<#
    Single-host deployment for Pay_Yet (ASP.NET Core app served at https://api.drivetodev.online, its own subdomain site).

    Flow (adapted from the proven P2S / EA single-host workflow on the same Plesk host):
      0. validate target + production config   1. unit tests   2. dotnet publish (win-x86 self-contained)
      3. stage config into the publish folder  4. app_offline.htm  5. upload files  6. bring online
      7. health checks (and restore maintenance mode if they fail)

    This file is tracked and contains no credentials. Credentials are passed in by deploy-single-host.ps1
    (git-ignored). Secrets for the app live in deploy\appsettings.Production.json (git-ignored).

    Why win-x86 self-contained: the app pool of this subscription runs 32-bit and has no x86 runtime
    installed; a self-contained publish needs nothing from the host. Use -FrameworkDependent only after the
    app pool is switched to 64-bit and the host runtime is confirmed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$Server,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$Username,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$Password,

    # Where the API runs: "api" = its own subdomain site (current), "gateway" = the /payyet folder of gateway.drivetodev.online.
    # The paths below are pinned on purpose (the FTP login root is the whole subscription).
    [ValidateSet("api", "gateway")] [string]$ApiTarget = "api",
    [string]$RemotePath = "",
    [string]$PublicBaseUrl = "",

    # Static web app (landing page + LIFF pay page) lives in the /payyet folder of the drivetodev.online site.
    [string]$WebRemotePath = "drivetodev.online/payyet",
    [string]$WebPublicUrl = "https://drivetodev.online/payyet",
    [ValidateSet("all", "api", "web")] [string]$Component = "all",   # api = backend only, web = static web app only
    [string]$ConfigPath = "",

    [string]$Runtime = "win-x86",
    [switch]$FrameworkDependent,
    [switch]$StdoutLog,            # keep ANCM stdout logs on (use for the first deploy / debugging)
    [switch]$SkipTests,

    [ValidateRange(0, 600)] [int]$OfflineDrainSeconds = 20,
    [ValidateRange(1, 100)] [int]$HealthCheckAttempts = 12,
    [ValidateRange(1, 300)] [int]$HealthCheckDelaySeconds = 5
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
if ($ApiTarget -eq "gateway") {
    if (-not $RemotePath) { $RemotePath = "gateway.drivetodev.online/payyet" }
    if (-not $PublicBaseUrl) { $PublicBaseUrl = "https://gateway.drivetodev.online/payyet" }
    if (-not $ConfigPath) { $ConfigPath = Join-Path $repoRoot "deploy\appsettings.Gateway.json" }   # same secrets, App:PublicBaseUrl / App:PathBase for the gateway
    $targetKey = "gateway"
    if ($Component -eq "all") { $Component = "api" }     # the static web app stays at drivetodev.online/payyet
} else {
    if (-not $RemotePath) { $RemotePath = "api.drivetodev.online" }
    if (-not $PublicBaseUrl) { $PublicBaseUrl = "https://api.drivetodev.online" }
    if (-not $ConfigPath) { $ConfigPath = Join-Path $repoRoot "deploy\appsettings.Production.json" }
    $targetKey = "api"
}
$dryRun = $env:PAYYET_DEPLOY_DRY_RUN -eq "1"

# ---- 0. Guard rails -------------------------------------------------------------------------------------
# The FTP login root is the whole subscription (thaipesleague.com, other domains and subdomains). Refuse anything but our folder,
# because -PruneRemoteRootFiles below deletes files in the target folder that are not part of this deployment.
$target = $RemotePath.Trim('/').ToLowerInvariant()
$allowedTarget = if ($ApiTarget -eq "gateway") { "gateway.drivetodev.online/payyet" } else { "api.drivetodev.online" }
if ($target -ne $allowedTarget) {
    throw "Refusing to deploy: RemotePath must be '$allowedTarget' for -ApiTarget $ApiTarget (got '$RemotePath')."
}
if ($WebRemotePath.Trim('/').ToLowerInvariant() -ne "drivetodev.online/payyet") {
    throw "Refusing to deploy: WebRemotePath must be 'drivetodev.online/payyet' (got '$WebRemotePath')."
}
$PublicBaseUrl = $PublicBaseUrl.TrimEnd('/')
$WebPublicUrl = $WebPublicUrl.TrimEnd('/')

$pwsh = if (Get-Command powershell -ErrorAction SilentlyContinue) { "powershell" } else { "pwsh" }
$uploader = Join-Path $repoRoot "upload-ftp.ps1"

# ---- Static web app (separate Plesk site; independent of the API deploy) ---------------------------------
function Deploy-Web {
    Write-Host "Uploading web app to ftp://$Server/$($WebRemotePath.Trim('/')) ..." -ForegroundColor Yellow
    $webDir = Join-Path $repoRoot "web"
    $webManifest = Join-Path $repoRoot ".deploy-cache\payyet-web-apex-manifest.json"
    $dryArgs = @(); if ($dryRun) { $dryArgs = @("-DryRun") }
    & $pwsh -NoProfile -ExecutionPolicy Bypass -File $uploader -Server $Server -Username $Username -Password $Password `
        -LocalPath $webDir -RemotePath $WebRemotePath.Trim('/').ToLowerInvariant() -ManifestPath $webManifest -PruneRemoteRootFiles @dryArgs
    if ($LASTEXITCODE -ne 0) { throw "Uploading the web app failed (exit code $LASTEXITCODE). The API is unaffected; fix and rerun." }
    if ($dryRun) { Write-Host "Dry run complete. No remote files were changed." -ForegroundColor Cyan; return }

    $webProblems = @()
    foreach ($path in @("/", "/liff/pay.html", "/manifest.webmanifest")) {
        try { Invoke-WebRequest -Uri "$WebPublicUrl$path" -UseBasicParsing -TimeoutSec 30 | Out-Null }
        catch { $webProblems += "$WebPublicUrl$path -> $($_.Exception.Message)" }
    }
    try {   # browser calls from the web app need CORS on the API
        $webOrigin = ([uri]$WebPublicUrl).GetLeftPart([System.UriPartial]::Authority)   # a CORS origin has no path
        $pre = Invoke-WebRequest -Uri "$PublicBaseUrl/api/pay/config" -UseBasicParsing -TimeoutSec 30 -Headers @{ Origin = $webOrigin }
        if ($pre.Headers["Access-Control-Allow-Origin"] -ne $webOrigin) { $webProblems += "API does not allow origin $webOrigin (check App:WebBaseUrl)" }
    } catch { $webProblems += "CORS check: $($_.Exception.Message)" }
    if ($webProblems.Count -gt 0) {
        Write-Warning ("Web checks reported problems:`n - " + ($webProblems -join "`n - "))
        throw "Web app uploaded but not healthy. Is the drivetodev.online/payyet folder served with a valid certificate?"
    }
    Write-Host "Web app verified: $WebPublicUrl" -ForegroundColor Green
}

if ($Component -eq "web") {
    Deploy-Web
    Write-Host "--- Web deployment complete ---" -ForegroundColor Cyan
    return
}

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw "Production config not found: $ConfigPath" }
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json

function Get-ConfigValue([string]$Path) {
    $node = $config
    foreach ($part in $Path.Split('.')) {
        if ($null -eq $node -or -not ($node.PSObject.Properties.Name -contains $part)) { return $null }
        $node = $node.$part
    }
    return [string]$node
}
$required = @("Line.LoginChannelId", "Liff.Id", "Jobs.Key")
if ([string]::IsNullOrWhiteSpace((Get-ConfigValue "Gateway.ApiKey"))) {
    $required += @("ConnectionStrings.Default", "Line.ChannelSecret", "Line.ChannelAccessToken")   # no gateway: the keys must be in this file
} else {
    $required += @("Gateway.BaseUrl", "Gateway.AppId")                                            # the DB string and LINE keys come from the gateway
}
$problems = @()
foreach ($key in $required) {
    $value = Get-ConfigValue $key
    if ([string]::IsNullOrWhiteSpace($value) -or $value -match "PASTE_|CHANGE_ME") { $problems += $key }
}
if ($problems.Count -gt 0) {
    throw "Fill these values in $ConfigPath before deploying: $($problems -join ', ')"
}

# ---- 1. Tests ---------------------------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Host "1. Running tests..." -ForegroundColor Yellow
    & dotnet test "$repoRoot\PayYet.sln" -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed - not deploying." }
}

# ---- 2. Publish -------------------------------------------------------------------------------------------
$stage = Join-Path $repoRoot "deploy"
$appDir = Join-Path $stage "app"
if (Test-Path -LiteralPath $appDir) { Remove-Item -LiteralPath $appDir -Recurse -Force }
New-Item -ItemType Directory -Path $appDir | Out-Null

$mode = if ($FrameworkDependent) { "framework-dependent" } else { "$Runtime self-contained" }
Write-Host "2. Publishing ($mode)..." -ForegroundColor Yellow
$publishArgs = @("publish", "$repoRoot\src\PayYet.Api", "-c", "Release", "-o", $appDir, "--nologo", "-v", "q")
if (-not $FrameworkDependent) { $publishArgs += @("-r", $Runtime, "--self-contained", "true") }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# web.config: out-of-process (the pool is shared with other sites), stdout logging only when asked.
$webConfigPath = Join-Path $appDir "web.config"
if (-not (Test-Path -LiteralPath $webConfigPath)) { throw "web.config missing from the publish output." }
[xml]$webConfig = Get-Content -LiteralPath $webConfigPath
$aspNetCore = $webConfig.configuration.location.'system.webServer'.aspNetCore
if (-not $aspNetCore) { throw "aspNetCore element not found in web.config." }
$aspNetCore.SetAttribute("hostingModel", "outofprocess")
$aspNetCore.SetAttribute("stdoutLogEnabled", $(if ($StdoutLog) { "true" } else { "false" }))

# Plesk replaces 4xx/5xx responses with its own HTML error pages. The LIFF page reads our JSON error bodies
# (e.g. "slip already used"), so let the app's own responses through unchanged. The post-deploy check below
# verifies this; if the host locks the httpErrors section, drop this block.
$webServer = $webConfig.configuration.location.'system.webServer'
$httpErrors = $webServer.httpErrors
if (-not $httpErrors) {
    $httpErrors = $webConfig.CreateElement("httpErrors")
    [void]$webServer.AppendChild($httpErrors)
}
$httpErrors.SetAttribute("existingResponse", "PassThrough")
$webConfig.Save($webConfigPath)

# logs folder must exist for ANCM stdout logging; the uploader only sends files, so ship a placeholder file.
New-Item -ItemType Directory -Path (Join-Path $appDir "logs") -Force | Out-Null
Set-Content -LiteralPath (Join-Path $appDir "logs\README.txt") -Value "ANCM stdout logs are written here when stdoutLogEnabled=true."

# ---- 3. Stage production config (removed again in 'finally') ---------------------------------------------
Copy-Item -LiteralPath $ConfigPath -Destination (Join-Path $appDir "appsettings.Production.json") -Force

# ---- 4. Maintenance page ----------------------------------------------------------------------------------
# The Thai text lives in a UTF-8 template file (not inside this script): Windows PowerShell 5.1 reads a BOM-less
# script as ANSI, which turned Thai literals into mojibake. The page is self-contained (no external files) because the app is
# down while it is shown.
$offlinePath = Join-Path $stage "app_offline.htm"
$templatesDir = Join-Path (Join-Path $repoRoot "deploy") "templates"
$templatePath = Join-Path $templatesDir "app_offline.template.htm"
foreach ($f in @($templatePath)) { if (-not (Test-Path -LiteralPath $f)) { throw "Missing maintenance page asset: $f" } }
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$offlineHtml = [IO.File]::ReadAllText($templatePath, [System.Text.Encoding]::UTF8)
[IO.File]::WriteAllText($offlinePath, $offlineHtml, $utf8NoBom)
# read the bytes back and make sure the Thai text survived (guards against another encoding mix-up)
$check = [System.Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($offlinePath))
if ($check.Contains([string][char]0xFFFD) -or $check -notmatch '\p{IsThai}') {
    throw "Maintenance page was not written as valid UTF-8 Thai text."
}

$manifestPath = Join-Path $repoRoot $(if ($targetKey -eq "gateway") { ".deploy-cache\payyet-gateway-manifest.json" } else { ".deploy-cache\payyet-manifest.json" })
$dryArgs = @(); if ($dryRun) { $dryArgs = @("-DryRun") }

function Assert-UploadSucceeded([string]$Stage, [int]$ExitCode) {
    if ($ExitCode -ne 0) {
        throw "$Stage failed (upload-ftp.ps1 exit code $ExitCode). The site stays in maintenance mode; fix the error and rerun."
    }
}
function Send-Offline {
    & $pwsh -NoProfile -ExecutionPolicy Bypass -File $uploader -Server $Server -Username $Username -Password $Password `
        -LocalPath $stage -RemotePath $target -ExcludePaths "app,appsettings.Production.json,appsettings.Gateway.json" @dryArgs   # config files are sent once, inside the app upload
}

$deployed = $false
try {
    Write-Host "4. Taking the app offline..." -ForegroundColor Yellow
    Send-Offline
    Assert-UploadSucceeded "Taking the application offline" $LASTEXITCODE
    if (-not $dryRun) {
        Write-Host "Waiting $OfflineDrainSeconds s for IIS to release files..."
        Start-Sleep -Seconds $OfflineDrainSeconds
    }

    Write-Host "5. Uploading application files to ftp://$Server/$target ..." -ForegroundColor Yellow
    & $pwsh -NoProfile -ExecutionPolicy Bypass -File $uploader -Server $Server -Username $Username -Password $Password `
        -LocalPath $appDir -RemotePath $target -ManifestPath $manifestPath -PruneRemoteRootFiles @dryArgs
    Assert-UploadSucceeded "Uploading application files" $LASTEXITCODE

    if ($dryRun) {
        Write-Host "Dry run complete. No remote files were changed." -ForegroundColor Cyan
        return
    }

    # ---- 6. Bring online --------------------------------------------------------------------------------
    Write-Host "6. Bringing the app back online..." -ForegroundColor Yellow
    $del = [System.Net.FtpWebRequest]::Create("ftp://$Server/$target/app_offline.htm")
    $del.Method = [System.Net.WebRequestMethods+Ftp]::DeleteFile
    $del.Credentials = New-Object System.Net.NetworkCredential($Username, $Password)
    $del.UsePassive = $true
    $del.GetResponse().Close()

    # ---- 7. Health checks ---------------------------------------------------------------------------------
    $healthUrl = "$PublicBaseUrl/health"
    $ok = $false; $last = "no response"
    for ($i = 1; $i -le $HealthCheckAttempts; $i++) {
        try {
            $r = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 30
            $last = "HTTP $([int]$r.StatusCode)"
            if ([int]$r.StatusCode -eq 200) { $ok = $true; break }
        }
        catch {
            $last = if ($_.Exception.Response) { "HTTP $([int]$_.Exception.Response.StatusCode)" } else { $_.Exception.Message }
        }
        if ($i -lt $HealthCheckAttempts) {
            Write-Host "Health check not ready ($last), attempt $i/$HealthCheckAttempts..." -ForegroundColor Gray
            Start-Sleep -Seconds $HealthCheckDelaySeconds
        }
    }
    if (-not $ok) {
        Write-Warning "Health check failed ($last). Restoring maintenance mode."
        Send-Offline
        throw "$healthUrl did not return HTTP 200 after $HealthCheckAttempts attempts (last: $last). With -StdoutLog the reason is in $target/logs/ on the server."
    }

    # Extra checks: API status page served, config reached the app, webhook rejects unsigned requests.
    $problems = @()
    try {
        $root = Invoke-WebRequest -Uri "$PublicBaseUrl/" -UseBasicParsing -TimeoutSec 30
        if ([int]$root.StatusCode -ne 200 -or $root.Content -notmatch "API Status") { $problems += "API status page missing at /" }
    } catch { $problems += "API status page: $($_.Exception.Message)" }

    try {
        $cfg = Invoke-RestMethod -Uri "$PublicBaseUrl/api/pay/config" -TimeoutSec 30
        if ($cfg.liffId -ne (Get-ConfigValue "Liff.Id")) { $problems += "Liff:Id on the server does not match the local config" }
    } catch { $problems += "pay config: $($_.Exception.Message)" }

    try {
        Invoke-WebRequest -Uri "$PublicBaseUrl/api/line/webhook" -Method Post -Body "{}" -ContentType "application/json" -UseBasicParsing -TimeoutSec 30 | Out-Null
        $problems += "webhook accepted an unsigned request"
    } catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($code -ne 401) { $problems += "webhook (unsigned) returned HTTP $code instead of 401" }
    }

    try {
        Invoke-WebRequest -Uri "$PublicBaseUrl/api/pay/deploycheck" -UseBasicParsing -TimeoutSec 30 | Out-Null
        $problems += "pay API accepted a request without a LINE token"
    } catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        $body = ""
        if ($_.Exception.Response) { $body = (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() }
        if ($code -ne 401) { $problems += "pay API (no token) returned HTTP $code instead of 401" }
        elseif ($body -notmatch '"error"') { $problems += "pay API error body was replaced by a host error page (httpErrors PassThrough not applied)" }
    }

    if ($problems.Count -gt 0) {
        Write-Warning ("Post-deploy checks reported problems:`n - " + ($problems -join "`n - "))
        throw "Deployment is up but not healthy. Fix the items above."
    }

    $deployed = $true
    Write-Host "Deployment verified: $PublicBaseUrl (health, status page, config, webhook guard, JSON errors)." -ForegroundColor Green
}
catch {
    Write-Host "Deployment stopped: $($_.Exception.Message)" -ForegroundColor Red
    throw
}
finally {
    # never leave secrets or the offline page lying around in the local staging folder
    Remove-Item -LiteralPath (Join-Path $appDir "appsettings.Production.json") -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $offlinePath -Force -ErrorAction SilentlyContinue
    Set-Location $repoRoot
}

if ($deployed -and $Component -eq "all") { Deploy-Web }

if ($deployed) {
    Write-Host "--- Deployment complete ---" -ForegroundColor Cyan
    Write-Host "Next: LINE Developers -> Webhook URL -> Verify; set the LIFF endpoint URL to $WebPublicUrl/liff/pay.html; add the Plesk scheduled task ($PublicBaseUrl/jobs/reminders?key=<Jobs:Key>)."
}
