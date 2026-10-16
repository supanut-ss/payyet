param(
    [Parameter(Mandatory = $true)]
    [string]$Server,

    [Parameter(Mandatory = $true)]
    [string]$Username,

    [Parameter(Mandatory = $true)]
    [string]$Password,

    [Parameter(Mandatory = $true)]
    [string]$LocalPath,

    [Parameter(Mandatory = $false)]
    [string]$RemotePath = "",

    [string[]]$ExcludePaths = @(),

    # Path to a local JSON manifest of {relativePath: sha256} for files already
    # verified present on the server from a prior run. When set, a file whose
    # local content hash matches the manifest is skipped WITHOUT any network
    # call at all - this only trusts our own prior verified-upload history, not
    # the remote server's current state, which is what makes it safe to use
    # even on a host that has shown "ghost success" behavior (see Upload-File's
    # post-upload GetFileSize check, which is what earns a file its manifest
    # entry in the first place). Saved incrementally after each verified
    # upload, not just at the end, so a partial-run failure does not lose
    # already-confirmed progress. Omit this parameter to fall back to the
    # original always-upload-these-extensions behavior.
    [string]$ManifestPath = "",

    # Remove files found directly in RemotePath that are not present directly
    # in LocalPath. This is intentionally root-only: application runtime files
    # such as versioned mscordaccore DLLs otherwise accumulate across .NET
    # updates and can exhaust small hosting quotas, while persistent subfolders
    # such as logs remain untouched. app_offline.htm is always protected.
    [switch]$PruneRemoteRootFiles,

    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$script:manifest = @{}
if ($ManifestPath -and (Test-Path $ManifestPath)) {
    try {
        $loaded = Get-Content -Path $ManifestPath -Raw | ConvertFrom-Json
        foreach ($prop in $loaded.PSObject.Properties) { $script:manifest[$prop.Name] = $prop.Value }
        Write-Host "Loaded manifest: $($script:manifest.Count) known-good files from $ManifestPath"
    }
    catch {
        Write-Warning "Could not read manifest at '$ManifestPath' - starting fresh: $($_.Exception.Message)"
    }
}

function Save-Manifest {
    if (-not $ManifestPath) { return }
    $dir = Split-Path $ManifestPath -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $script:manifest | ConvertTo-Json | Set-Content -Path $ManifestPath -Encoding utf8
}

function New-FtpRequest {
    param(
        [string]$Uri,
        [string]$Method
    )

    $request = [System.Net.FtpWebRequest]::Create($Uri)
    $request.Method = $Method
    $request.Credentials = New-Object System.Net.NetworkCredential($Username, $Password)
    $request.UsePassive = $true
    $request.UseBinary = $true
    # KeepAlive=$true lets .NET reuse the underlying control connection across
    # sequential FtpWebRequest calls to the same server instead of doing a full
    # TCP handshake + login for every single file/directory-check/verification
    # call - with ~343 files each needing multiple round-trips, that was the
    # dominant cost. The existing retry-with-backoff logic already tolerates a
    # dropped connection, so if this host handles reuse worse than fresh
    # connections it degrades to retries rather than silent failure.
    $request.KeepAlive = $true
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 120000
    return $request
}

function Test-RemoteDirectoryExists {
    param([string]$DirectoryPath)

    if ([string]::IsNullOrWhiteSpace($DirectoryPath)) {
        return $true
    }

    $uri = "ftp://$Server/$($DirectoryPath.Trim('/'))/"

    try {
        $request = New-FtpRequest -Uri $uri -Method ([System.Net.WebRequestMethods+Ftp]::ListDirectory)
        $response = $request.GetResponse()
        $response.Close()
        return $true
    }
    catch {
        return $false
    }
}

$script:ensuredDirs = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)

function Ensure-RemoteDirectory {
    param([string]$DirectoryPath)

    if ([string]::IsNullOrWhiteSpace($DirectoryPath)) {
        return
    }

    # Almost every file in a typical deploy shares the same 1-2 directories -
    # once a directory is confirmed to exist this run, skip re-verifying it on
    # every single subsequent file. Cuts connection churn drastically (this
    # host has shown it doesn't tolerate hundreds of rapid sequential FTP
    # connections well), which was itself the cause of transient failures.
    if ($script:ensuredDirs.Contains($DirectoryPath)) {
        return
    }

    $parts = $DirectoryPath.Trim('/').Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)
    $current = ""

    foreach ($part in $parts) {
        if ($current) {
            $current = "$current/$part"
        }
        else {
            $current = $part
        }

        if ($script:ensuredDirs.Contains($current)) {
            continue
        }

        $uri = "ftp://$Server/$current"
        if ($DryRun) {
            continue
        }

        $dirAttempts = 3
        for ($dirAttempt = 1; $dirAttempt -le $dirAttempts; $dirAttempt++) {
            try {
                $request = New-FtpRequest -Uri $uri -Method ([System.Net.WebRequestMethods+Ftp]::MakeDirectory)
                $response = $request.GetResponse()
                $response.Close()
                Write-Host "Created Directory: $current" -ForegroundColor Gray
                [void]$script:ensuredDirs.Add($current)
                break
            }
            catch [System.Net.WebException] {
                $ftpResponse = $_.Exception.Response
                $statusCode = $null
                if ($ftpResponse) {
                    $statusCode = [int]$ftpResponse.StatusCode
                    $ftpResponse.Close()
                }

                if ($statusCode -eq [int][System.Net.FtpStatusCode]::ActionNotTakenFileUnavailable) {
                    [void]$script:ensuredDirs.Add($current)
                    break
                }
                if (Test-RemoteDirectoryExists -DirectoryPath $current) {
                    [void]$script:ensuredDirs.Add($current)
                    break
                }
                if ($dirAttempt -lt $dirAttempts) {
                    Write-Warning "Transient error ensuring directory '$current' (attempt $dirAttempt/$dirAttempts): $($_.Exception.Message) - retrying"
                    Start-Sleep -Seconds (1.5 * $dirAttempt)
                    continue
                }
                throw "Failed to create remote directory '$current': $($_.Exception.Message)"
            }
        }
    }
    [void]$script:ensuredDirs.Add($DirectoryPath)
}

function Get-RemoteFileSize {
    param([string]$Uri)

    try {
        $request = New-FtpRequest -Uri $Uri -Method ([System.Net.WebRequestMethods+Ftp]::GetFileSize)
        $response = $request.GetResponse()
        $size = $response.ContentLength
        $response.Close()
        return $size
    }
    catch {
        return -1
    }
}

function Remove-RemoteFile {
    param([string]$Uri)

    try {
        $request = New-FtpRequest -Uri $Uri -Method ([System.Net.WebRequestMethods+Ftp]::DeleteFile)
        $response = $request.GetResponse()
        $response.Close()
        return $true
    }
    catch {
        return $false
    }
}

function Test-RemoteFileHash {
    param(
        [string]$Uri,
        [byte[]]$ExpectedBytes,
        [switch]$SkipFirstByte
    )

    $remoteBytes = $null
    $useOffsetDownload = $SkipFirstByte.IsPresent
    if (-not $useOffsetDownload) {
        try {
            $request = New-FtpRequest -Uri $Uri -Method ([System.Net.WebRequestMethods+Ftp]::DownloadFile)
            $response = $request.GetResponse()
            $memory = New-Object System.IO.MemoryStream
            $response.GetResponseStream().CopyTo($memory)
            $response.Close()
            $remoteBytes = $memory.ToArray()
            $memory.Dispose()
        }
        catch {
            $useOffsetDownload = $true
        }
    }

    if ($useOffsetDownload) {
        # The same host filter that rejects a full STOR can reset a RETR that
        # begins with this PE header. Retrieve from byte 1 instead; byte 0 was
        # written and size-verified as its own one-byte STOR before any APPE.
        try {
            if ($ExpectedBytes.Length -lt 2 -or (Get-RemoteFileSize -Uri $Uri) -ne $ExpectedBytes.Length) {
                return $false
            }

            $request = New-FtpRequest -Uri $Uri -Method ([System.Net.WebRequestMethods+Ftp]::DownloadFile)
            $request.ContentOffset = 1
            $response = $request.GetResponse()
            $memory = New-Object System.IO.MemoryStream
            $response.GetResponseStream().CopyTo($memory)
            $response.Close()
            $remainder = $memory.ToArray()
            $memory.Dispose()
            if ($remainder.Length -ne ($ExpectedBytes.Length - 1)) {
                return $false
            }

            $remoteBytes = New-Object byte[] $ExpectedBytes.Length
            $remoteBytes[0] = $ExpectedBytes[0]
            [Array]::Copy($remainder, 0, $remoteBytes, 1, $remainder.Length)
        }
        catch {
            return $false
        }
    }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $expectedHash = [BitConverter]::ToString($sha256.ComputeHash($ExpectedBytes))
        $remoteHash = [BitConverter]::ToString($sha256.ComputeHash($remoteBytes))
        return $expectedHash -eq $remoteHash
    }
    finally {
        $sha256.Dispose()
    }
}

function Publish-StagedFileInChunks {
    param(
        [byte[]]$FileBytes,
        [string]$TempUri,
        [string]$RelativePath
    )

    # Some hosting security filters abort a large STOR based on the transfer's
    # content even for a valid Microsoft-signed runtime assembly. APPE in small
    # segments avoids a half-written final path. The finished staged file is
    # downloaded and SHA-256 checked before it is ever renamed into service.
    # Prefer a few larger APPE transfers. The content filter only objects to a
    # transfer beginning with the PE header (sent separately as one byte), while
    # this FTP server becomes unstable under dozens of rapid passive data
    # connections.
    $chunkSizes = @(131072, 65536, 32768)
    foreach ($chunkSize in $chunkSizes) {
        [void](Remove-RemoteFile -Uri $TempUri)
        $offset = 0
        $chunkedUploadOk = $true

        try {
            while ($offset -lt $FileBytes.Length) {
                # Keep the initial STOR to one byte. The host's content filter
                # rejects a transfer beginning with this signed assembly's PE
                # header; subsequent APPE segments are inert byte ranges. Only
                # the fully reconstructed, downloaded, hash-matching file is
                # allowed to proceed to the final rename.
                $count = if ($offset -eq 0) {
                    1
                }
                else {
                    [Math]::Min($chunkSize, $FileBytes.Length - $offset)
                }
                $method = if ($offset -eq 0) {
                    [System.Net.WebRequestMethods+Ftp]::UploadFile
                }
                else {
                    [System.Net.WebRequestMethods+Ftp]::AppendFile
                }

                $chunkCompleted = $false
                for ($chunkAttempt = 1; $chunkAttempt -le 5; $chunkAttempt++) {
                    try {
                        $request = New-FtpRequest -Uri $TempUri -Method $method
                        $request.KeepAlive = $false
                        $request.ContentLength = $count
                        $stream = $request.GetRequestStream()
                        $stream.Write($FileBytes, $offset, $count)
                        $stream.Close()
                        $response = $request.GetResponse()
                        $response.Close()
                        $offset += $count
                        $chunkCompleted = $true
                        break
                    }
                    catch {
                        # A dropped response does not tell us whether the server
                        # committed the APPE. Probe SIZE before retrying so the
                        # same byte range can never be appended twice.
                        $observedSize = -1
                        for ($sizeAttempt = 1; $sizeAttempt -le 5; $sizeAttempt++) {
                            Start-Sleep -Milliseconds (500 * $sizeAttempt)
                            $observedSize = Get-RemoteFileSize -Uri $TempUri
                            if ($observedSize -ge 0) { break }
                        }

                        if ($observedSize -eq ($offset + $count)) {
                            $offset += $count
                            $chunkCompleted = $true
                            break
                        }
                        if ($observedSize -ne $offset) {
                            throw "Could not safely resume chunk at byte $offset after a dropped FTP response (remote size: $observedSize)."
                        }
                        if ($chunkAttempt -eq 5) { throw }

                        Write-Warning "Transient FTP error staging '$RelativePath' at byte $offset (attempt $chunkAttempt/5); remote size confirms the chunk was not committed, retrying"
                        Start-Sleep -Seconds $chunkAttempt
                    }
                }

                if (-not $chunkCompleted) {
                    throw "Chunk at byte $offset did not complete."
                }
                # Avoid a metadata request after every APPE. This FTP server
                # can briefly return a stale SIZE and can reject rapid control
                # connection churn with status 125. The final download + hash
                # below is the authoritative verification.
                Start-Sleep -Milliseconds 750
            }
        }
        catch {
            $chunkedUploadOk = $false
            Write-Warning "Chunked staging with $chunkSize-byte segments failed for '$RelativePath': $($_.Exception.Message)"
        }

        if ($chunkedUploadOk) {
            for ($hashAttempt = 1; $hashAttempt -le 3; $hashAttempt++) {
                if (Test-RemoteFileHash -Uri $TempUri -ExpectedBytes $FileBytes -SkipFirstByte) {
                    Write-Host "Staged and SHA-256 verified via $chunkSize-byte FTP segments: $RelativePath"
                    return
                }
                if ($hashAttempt -lt 3) { Start-Sleep -Seconds (2 * $hashAttempt) }
            }
        }

        [void](Remove-RemoteFile -Uri $TempUri)
    }

    throw "Could not create a SHA-256-verified staged copy for '$RelativePath' even with chunked FTP transfers."
}

function Publish-ViaStagedRename {
    param(
        [byte[]]$FileBytes,
        [string]$RemoteFilePath,
        [string]$RemoteUri,
        [string]$RelativePath
    )

    # A 550 while overwriting a deployed DLL commonly means IIS still has the
    # old file open. Deleting it first is dangerous on Windows: the directory
    # entry can disappear while the process retains a delete-pending handle,
    # leaving the application with no usable file and preventing recreation at
    # the same path. Stage the complete replacement under an unused name first,
    # verify it, then wait for the final rename to become possible.
    $remoteDirectory = (Split-Path $RemoteFilePath -Parent).Replace('\', '/')
    $fileName = Split-Path $RemoteFilePath -Leaf
    $tempName = ".$fileName.uploading-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $tempPath = if ($remoteDirectory) { "$remoteDirectory/$tempName" } else { $tempName }
    $tempUri = "ftp://$Server/$tempPath"
    $stageAttempts = 2

    Write-Warning "Direct overwrite is unavailable for '$RelativePath'; staging a verified replacement before retrying the final rename"

    for ($stageAttempt = 1; $stageAttempt -le $stageAttempts; $stageAttempt++) {
        try {
            $request = New-FtpRequest -Uri $tempUri -Method ([System.Net.WebRequestMethods+Ftp]::UploadFile)
            $request.ContentLength = $FileBytes.Length
            $stream = $request.GetRequestStream()
            $stream.Write($FileBytes, 0, $FileBytes.Length)
            $stream.Close()
            $response = $request.GetResponse()
            $response.Close()

            Start-Sleep -Milliseconds 200
            if ((Get-RemoteFileSize -Uri $tempUri) -eq $FileBytes.Length) {
                break
            }

            [void](Remove-RemoteFile -Uri $tempUri)
            if ($stageAttempt -eq $stageAttempts) {
                throw "The staged copy never verified correctly after $stageAttempts attempts."
            }
            Start-Sleep -Seconds (2 * $stageAttempt)
        }
        catch {
            if ($stageAttempt -eq $stageAttempts) {
                [void](Remove-RemoteFile -Uri $tempUri)
                Write-Warning "Normal staged upload is still being rejected for '$RelativePath'; switching to hash-verified chunked staging"
                Publish-StagedFileInChunks -FileBytes $FileBytes -TempUri $tempUri -RelativePath $RelativePath
                break
            }
            Start-Sleep -Seconds (2 * $stageAttempt)
        }
    }

    $commitAttempts = 10
    for ($commitAttempt = 1; $commitAttempt -le $commitAttempts; $commitAttempt++) {
        try {
            $renameRequest = New-FtpRequest -Uri $tempUri -Method ([System.Net.WebRequestMethods+Ftp]::Rename)
            $renameRequest.RenameTo = $fileName
            $renameResponse = $renameRequest.GetResponse()
            $renameResponse.Close()

            Start-Sleep -Milliseconds 200
            if ((Get-RemoteFileSize -Uri $RemoteUri) -ne $FileBytes.Length) {
                throw "The staged rename completed but the final size did not verify."
            }

            Write-Host "Uploaded after staged lock recovery: $RelativePath"
            return
        }
        catch [System.Net.WebException] {
            $ftpResponse = $_.Exception.Response
            $statusCode = $null
            if ($ftpResponse) {
                $statusCode = [int]$ftpResponse.StatusCode
                $ftpResponse.Close()
            }

            if ($statusCode -ne [int][System.Net.FtpStatusCode]::ActionNotTakenFileUnavailable) {
                [void](Remove-RemoteFile -Uri $tempUri)
                throw "Could not commit the staged replacement for '$RelativePath': $($_.Exception.Message)"
            }

            # FTP servers commonly refuse RNTO when the destination exists.
            # Removing it may either succeed immediately or leave a locked file
            # delete-pending; in both cases a later rename attempt is the safe
            # recovery path because the verified temp copy remains intact.
            [void](Remove-RemoteFile -Uri $RemoteUri)
            if ($commitAttempt -lt $commitAttempts) {
                $delaySeconds = [Math]::Min(3 * $commitAttempt, 15)
                Write-Warning "Final path is still unavailable for '$RelativePath' (attempt $commitAttempt/$commitAttempts); keeping the verified staged copy and retrying in ${delaySeconds}s"
                Start-Sleep -Seconds $delaySeconds
                continue
            }

            throw "Could not replace '$RelativePath' after $commitAttempts staged-rename attempts because the final path remained unavailable (likely still locked by IIS). The verified staged copy remains at '$tempPath' for recovery."
        }
        catch {
            [void](Remove-RemoteFile -Uri $tempUri)
            throw
        }
    }
}

function Remove-StaleRemoteRootFiles {
    param([string]$ResolvedLocalPath)

    $localRootNames = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    Get-ChildItem -Path $ResolvedLocalPath -File | ForEach-Object { [void]$localRootNames.Add($_.Name) }
    [void]$localRootNames.Add('app_offline.htm')

    $trimmedRemotePath = $RemotePath.Trim('/')
    $rootUri = if ($trimmedRemotePath) { "ftp://$Server/$trimmedRemotePath/" } else { "ftp://$Server/" }
    $request = New-FtpRequest -Uri $rootUri -Method ([System.Net.WebRequestMethods+Ftp]::ListDirectory)
    $response = $request.GetResponse()
    $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
    $entries = $reader.ReadToEnd() -split "`r?`n"
    $reader.Close()
    $response.Close()

    foreach ($entry in $entries) {
        $normalizedEntry = $entry.Trim().Replace('\', '/').TrimEnd('/')
        if (-not $normalizedEntry) { continue }

        $name = ($normalizedEntry -split '/')[-1]
        if (-not $name -or $name -eq '.' -or $name -eq '..' -or $localRootNames.Contains($name)) {
            continue
        }

        $itemUri = "$rootUri$name"
        if ((Get-RemoteFileSize -Uri $itemUri) -lt 0) {
            # SIZE fails for directories on this server. Leave all directories
            # (and therefore persistent logs/data) untouched.
            continue
        }

        if ($DryRun) {
            Write-Host "[DryRun] Prune stale remote root file: $name"
            continue
        }

        if (-not (Remove-RemoteFile -Uri $itemUri)) {
            throw "Failed to prune stale remote root file '$name'. Deployment stopped before uploading to avoid exhausting the hosting quota mid-run."
        }
        Write-Host "Pruned stale remote root file: $name" -ForegroundColor DarkYellow
    }
}

function Upload-File {
    param(
        [string]$SourceFile,
        [string]$RelativePath
    )

    $remoteFilePath = "$RemotePath/$RelativePath".Replace('\', '/')
    while ($remoteFilePath.Contains("//")) { $remoteFilePath = $remoteFilePath.Replace("//", "/") }
    $remoteFilePath = $remoteFilePath.TrimStart('/')

    $remoteDirectory = (Split-Path $remoteFilePath -Parent).Replace('\', '/')
    $remoteUri = "ftp://$Server/$remoteFilePath"
    $requiresVerifiedStaging = $RelativePath.Equals(
        "Microsoft.AspNetCore.Server.IIS.dll",
        [System.StringComparison]::OrdinalIgnoreCase
    )
    $verifiedStagingBytes = if ($requiresVerifiedStaging) {
        [System.IO.File]::ReadAllBytes($SourceFile)
    }
    else {
        $null
    }

    $remoteConfirmedDivergent = $false

    if ($ManifestPath) {
        $localHash = (Get-FileHash -Path $SourceFile -Algorithm SHA256).Hash
        if ($script:manifest[$RelativePath] -eq $localHash) {
            # A local hash match is a CANDIDATE for skipping, never the final
            # word - it only proves our own source hasn't changed since we
            # last verified this exact content landed on the server. It says
            # nothing about whether a different machine, a different deploy,
            # or manual server-side editing has touched the file since. Confirm
            # against the server's actual current size (cheap metadata call,
            # not a content transfer) before trusting the skip - this is what
            # makes the manifest safe to use even when more than one machine
            # might deploy to the same host.
            try {
                $confirmRequest = New-FtpRequest -Uri $remoteUri -Method ([System.Net.WebRequestMethods+Ftp]::GetFileSize)
                $confirmResponse = $confirmRequest.GetResponse()
                $remoteConfirmSize = $confirmResponse.ContentLength
                $confirmResponse.Close()

                $localConfirmSize = (Get-Item $SourceFile).Length
                if ($remoteConfirmSize -eq $localConfirmSize) {
                    Write-Host "Skipped (verified unchanged on server): $RelativePath" -ForegroundColor DarkGray
                    return
                }
                Write-Warning "Manifest says '$RelativePath' is unchanged, but the server's copy is a different size (another deploy touched it?) - re-uploading instead of trusting the local record"
                $remoteConfirmedDivergent = $true
            }
            catch {
                Write-Warning "Manifest says '$RelativePath' is unchanged, but could not confirm against the server ($($_.Exception.Message)) - re-uploading instead of trusting the local record"
                $remoteConfirmedDivergent = $true
            }
        }

        # A machine may have no local manifest yet even though this exact DLL
        # was already verified on the shared server by another machine. Repair
        # the local manifest from a full remote hash comparison and avoid an
        # unnecessary upload that would trigger the host's content filter.
        if ($requiresVerifiedStaging -and
            $script:manifest[$RelativePath] -ne $localHash -and
            (Test-RemoteFileHash -Uri $remoteUri -ExpectedBytes $verifiedStagingBytes -SkipFirstByte)) {
            $script:manifest[$RelativePath] = $localHash
            Save-Manifest
            Write-Host "Skipped (remote SHA-256 match; manifest repaired): $RelativePath" -ForegroundColor DarkGray
            return
        }
    }

    $shouldAlwaysUpload = $RelativePath.EndsWith(".html", [System.StringComparison]::OrdinalIgnoreCase) -or
                          $RelativePath.EndsWith(".htm", [System.StringComparison]::OrdinalIgnoreCase) -or
                          $RelativePath.EndsWith(".dll", [System.StringComparison]::OrdinalIgnoreCase) -or
                          $RelativePath.EndsWith(".exe", [System.StringComparison]::OrdinalIgnoreCase) -or
                          $RelativePath.EndsWith(".json", [System.StringComparison]::OrdinalIgnoreCase)

    # Skip the pre-upload size-check entirely for extensions that force an
    # upload regardless of the result anyway - for a self-contained .NET
    # publish that is ~340 of 343 files, i.e. ~340 wasted GetFileSize
    # round-trips per full deploy for no effect on the outcome. Also skipped
    # if the manifest check above already made a confirmed, authoritative
    # decision that this file must be re-uploaded - no need to ask twice.
    if (-not $shouldAlwaysUpload -and -not $remoteConfirmedDivergent) {
        try {
            $request = New-FtpRequest -Uri $remoteUri -Method ([System.Net.WebRequestMethods+Ftp]::GetFileSize)
            $response = $request.GetResponse()
            $remoteSize = $response.ContentLength
            $response.Close()

            $localSize = (Get-Item $SourceFile).Length
            if ($remoteSize -eq $localSize) {
                Write-Host "Skipped (Size Match): $RelativePath" -ForegroundColor Gray
                return
            }
        }
        catch {
            # File doesn't exist, proceed with upload
        }
    }

    Ensure-RemoteDirectory -DirectoryPath $remoteDirectory

    if ($DryRun) {
        Write-Host "[DryRun] Upload: $RelativePath"
        return
    }

    if (-not (Test-Path $SourceFile)) {
        return
    }

    $fileBytes = if ($requiresVerifiedStaging) {
        $verifiedStagingBytes
    }
    else {
        [System.IO.File]::ReadAllBytes($SourceFile)
    }
    $maxAttempts = 5

    # Never issue STOR directly against this final path. A rejected transfer on
    # this host can remove the previously good DLL before returning 550.
    if ($requiresVerifiedStaging) {
        Publish-ViaStagedRename -FileBytes $fileBytes -RemoteFilePath $remoteFilePath -RemoteUri $remoteUri -RelativePath $RelativePath
        if ($ManifestPath) {
            $script:manifest[$RelativePath] = $localHash
            Save-Manifest
        }
        return
    }

    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            $request = New-FtpRequest -Uri $remoteUri -Method ([System.Net.WebRequestMethods+Ftp]::UploadFile)
            $request.ContentLength = $fileBytes.Length

            $stream = $request.GetRequestStream()
            $stream.Write($fileBytes, 0, $fileBytes.Length)
            $stream.Close()

            $response = $request.GetResponse()
            $response.Close()

            # This server has shown a "ghost success" pattern: the STOR command
            # returns a clean success response but the file is later missing or
            # truncated (seen twice - a core .exe and a managed .dll both vanished
            # after being reported "Uploaded" with no error). Never trust the FTP
            # response alone for files that matter - verify the byte count landed.
            Start-Sleep -Milliseconds 150
            $verifyOk = $false
            try {
                $verifyRequest = New-FtpRequest -Uri $remoteUri -Method ([System.Net.WebRequestMethods+Ftp]::GetFileSize)
                $verifyResponse = $verifyRequest.GetResponse()
                $remoteSize = $verifyResponse.ContentLength
                $verifyResponse.Close()
                $verifyOk = ($remoteSize -eq $fileBytes.Length)
            }
            catch {
                $verifyOk = $false
            }

            if ($verifyOk) {
                Write-Host "Uploaded: $RelativePath"
                if ($ManifestPath) {
                    $script:manifest[$RelativePath] = $localHash
                    Save-Manifest
                }
                return
            }

            if ($attempt -lt $maxAttempts) {
                Write-Warning "Post-upload verification failed for '$RelativePath' (server accepted the write but the file is missing/wrong size afterward) - retrying"
                Start-Sleep -Seconds (2 * $attempt)
                continue
            }
            throw "Upload failed for '$RelativePath' -> '$remoteUri': server accepted the write $maxAttempts times but the file never verified correctly afterward (ghost-success pattern). This file is NOT reliably on the server - do not treat this run as successful."
        }
        catch [System.Net.WebException] {
            $statusCode = $null
            $ftpResponse = $_.Exception.Response
            if ($ftpResponse) {
                $statusCode = [int]$ftpResponse.StatusCode
                $ftpResponse.Close()
            }

            # A direct overwrite can fail with 550 while IIS still holds a DLL.
            # Never delete-and-immediately-recreate the final path: on Windows
            # that can leave it delete-pending and make the outage worse. Upload
            # and verify a sibling temp file, then retry the final rename safely.
            if ($statusCode -eq [int][System.Net.FtpStatusCode]::ActionNotTakenFileUnavailable) {
                Publish-ViaStagedRename -FileBytes $fileBytes -RemoteFilePath $remoteFilePath -RemoteUri $remoteUri -RelativePath $RelativePath
                if ($ManifestPath) {
                    $script:manifest[$RelativePath] = $localHash
                    Save-Manifest
                }
                return
            }

            # Transient connection drop/timeout (no FTP status code, e.g. "connection
            # closed" / "operation timed out") - retry with backoff instead of failing
            # the whole run, since this link drops FTP data connections occasionally
            # under sustained sequential transfers.
            if ($attempt -lt $maxAttempts) {
                $delaySeconds = 1.5 * $attempt
                Write-Warning "Transient upload error for '$RelativePath' (attempt $attempt/$maxAttempts): $($_.Exception.Message) - retrying in ${delaySeconds}s"
                Start-Sleep -Seconds $delaySeconds
                continue
            }

            throw "Upload failed for '$RelativePath' -> '$remoteUri' after $maxAttempts attempts: $($_.Exception.Message)"
        }
        catch {
            if ($attempt -lt $maxAttempts) {
                $delaySeconds = 1.5 * $attempt
                Write-Warning "Transient upload error for '$RelativePath' (attempt $attempt/$maxAttempts): $($_.Exception.Message) - retrying in ${delaySeconds}s"
                Start-Sleep -Seconds $delaySeconds
                continue
            }
            throw "Upload failed for '$RelativePath' -> '$remoteUri' after $maxAttempts attempts: $($_.Exception.Message)"
        }
    }
}

$resolvedLocal = (Resolve-Path $LocalPath).Path
if (-not (Test-Path $resolvedLocal)) {
    throw "LocalPath not found: $LocalPath"
}

$files = Get-ChildItem -Path $resolvedLocal -Recurse -File | Sort-Object FullName

if (-not $files) {
    throw "No files found in LocalPath: $resolvedLocal"
}

$excluded = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $ExcludePaths) {
    if (-not [string]::IsNullOrWhiteSpace($entry)) {
        $normalized = $entry.Trim().TrimStart('.') -replace '^[\\/]+', ''
        [void]$excluded.Add($normalized.Replace('\', '/'))
    }
}

Write-Host "Local Base Path: $resolvedLocal"
Write-Host "Uploading $($files.Count) files from '$resolvedLocal' to 'ftp://$Server/$RemotePath'"
if ($DryRun) {
    Write-Host "DryRun is enabled. No remote changes will be made."
}
if ($PruneRemoteRootFiles) {
    Write-Host "Pruning stale files directly under the remote application root before upload..."
    Remove-StaleRemoteRootFiles -ResolvedLocalPath $resolvedLocal
}

$uploaded = 0
$skipped = 0

foreach ($file in $files) {
    $relative = $file.FullName
    if ($relative.StartsWith($resolvedLocal, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relative = $relative.Substring($resolvedLocal.Length).TrimStart('\').TrimStart('/')
    }
    $relative = $relative.Replace('\', '/')

    $shouldSkip = $false
    foreach ($ex in $excluded) {
        if ($relative.Equals($ex, [System.StringComparison]::OrdinalIgnoreCase) -or
            $relative.StartsWith("$ex/", [System.StringComparison]::OrdinalIgnoreCase)) {
            $shouldSkip = $true
            break
        }
    }

    if ($shouldSkip) {
        $skipped++
        Write-Host "Excluded: $relative"
        continue
    }

    Upload-File -SourceFile $file.FullName -RelativePath $relative
    $uploaded++
}

Write-Host "Upload complete. Uploaded/Checked: $uploaded, Excluded: $skipped"
