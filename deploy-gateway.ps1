# Deploy the API to gateway.drivetodev.online/payyet (the API only; the web app stays at drivetodev.online/payyet).
# FTP credentials come from deploy.credentials.ps1 (git-ignored); secrets from deploy\appsettings.Gateway.json (git-ignored).
# Set $env:PAYYET_DEPLOY_DRY_RUN = "1" to see what would be uploaded without changing anything.
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\deploy.credentials.ps1"
if ([string]::IsNullOrWhiteSpace([string]$D2D_DEPLOY_FTP_SERVER) -or [string]::IsNullOrWhiteSpace([string]$D2D_DEPLOY_FTP_USERNAME) -or [string]::IsNullOrWhiteSpace([string]$D2D_DEPLOY_FTP_PASSWORD)) {
    throw "FTP credentials are incomplete in deploy.credentials.ps1."
}
$deployParameters = @{
    Server     = $D2D_DEPLOY_FTP_SERVER
    Username   = $D2D_DEPLOY_FTP_USERNAME
    Password   = $D2D_DEPLOY_FTP_PASSWORD
    ApiTarget  = "gateway"
    Component  = "api"
    StdoutLog  = $true      # keep ANCM stdout logs; remove once stable
}
& "$PSScriptRoot\deploy-single-host.core.ps1" @deployParameters @args
