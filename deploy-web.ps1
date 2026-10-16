# Deploy the static web app only (payyet.drivetodev.online).
# FTP credentials come from deploy.credentials.ps1 (git-ignored).
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\deploy.credentials.ps1"
if ([string]::IsNullOrWhiteSpace([string]$D2D_DEPLOY_FTP_SERVER) -or [string]::IsNullOrWhiteSpace([string]$D2D_DEPLOY_FTP_USERNAME) -or [string]::IsNullOrWhiteSpace([string]$D2D_DEPLOY_FTP_PASSWORD)) {
    throw "FTP credentials are incomplete in deploy.credentials.ps1."
}
$deployParameters = @{
    Server     = $D2D_DEPLOY_FTP_SERVER
    Username   = $D2D_DEPLOY_FTP_USERNAME
    Password   = $D2D_DEPLOY_FTP_PASSWORD
    Component  = "web"
    StdoutLog  = $true      # keep ANCM stdout logs; remove once stable
}
& "$PSScriptRoot\deploy-single-host.core.ps1" @deployParameters @args
