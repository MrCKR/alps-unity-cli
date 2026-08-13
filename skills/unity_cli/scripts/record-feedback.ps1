[CmdletBinding()]
# 保留 UTF-8 BOM，确保 Windows PowerShell 5.1 正确解析中文注释。
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("failure", "ambiguity", "missing", "misleading", "workaround")]
    [string]$Category,

    [Parameter(Mandatory = $true)]
    [ValidateLength(1, 512)]
    [string]$Summary,

    [ValidateLength(0, 512)]
    [string]$Workaround = "",

    [ValidatePattern('^$|^[A-Za-z0-9._-]{1,128}$')]
    [string]$Capability = "",

    [ValidatePattern('^$|^[A-Z][A-Z0-9_]{1,63}$')]
    [string]$ErrorCode = "",

    [ValidateSet("discovery", "describe", "execute", "verify", "install", "other")]
    [string]$Phase = "other"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$maxFileBytes = 64KB
$archiveCount = 3
$mutex = $null
$mutexHeld = $false

function Assert-SafeFeedbackText {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowEmptyString()][string]$Value
    )

    # 反馈只接受单行摘要，并拒绝常见路径、网络地址和凭据形态。
    if ($Name -eq "Summary" -and [string]::IsNullOrWhiteSpace($Value)) {
        throw "Summary must contain non-whitespace text."
    }
    if ($Value -match '[\r\n]') {
        throw "$Name must be a single line."
    }
    $containsNetworkAddress = $Value -match '(?i)\b(?:https?|wss?|file)://'
    $containsEndpointLabel = $Value -match '(?i)\bendpoint\s*[:=]'
    # 只拒绝明显 hostname：localhost 或已知公共/保留 TLD 结尾，避免误伤三段式工具名。
    $containsHostName = $Value -match '(?i)\b(?:localhost|(?:[A-Za-z0-9-]+\.)+(?:com|org|net|edu|gov|mil|info|biz|io|dev|app|ai|co|me|tv|cc|gg|sh|xyz|top|tech|cloud|site|online|store|shop|club|live|news|media|world|email|blog|wiki|page|link|fun|run|one|pro|plus|space|host|cn|uk|us|jp|de|fr|ru|in|br|au|ca|it|es|nl|se|ch|kr|tw|hk|sg|example|test|invalid))\b'
    $containsIpAddress = $Value -match '\b(?:\d{1,3}\.){3}\d{1,3}\b'
    $containsHostPort = $Value -match '(?i)\b(?:localhost|(?:\d{1,3}\.){3}\d{1,3}|[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*):\d{2,5}\b'
    $containsAbsolutePath = $Value -match '(?i)(?:[A-Z]:[\\/]|(?:^|\s)/(?:[^/\s]+/)*[^/\s]+)'
    $containsUncPath = $Value -match '\\\\[^\\\s]+\\'
    $containsRelativePath = $Value -match '(?i)(?:^|\s)(?:\.{1,2}[\\/]|[^\s<>:"|?*]+[\\/][^\s<>:"|?*]+)'
    if ($containsNetworkAddress -or $containsEndpointLabel -or $containsHostName -or $containsIpAddress -or $containsHostPort -or $containsAbsolutePath -or $containsUncPath -or $containsRelativePath) {
        throw "$Name must not contain a URL, endpoint, or path."
    }
    $containsCredentialLabel = $Value -match '(?i)\b(?:authorization|api[-_ ]?key|access[-_ ]?token|token|password|secret)(?:\s*[:=]\s*|\s+)[^\s]{4,}'
    $containsBearer = $Value -match '(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}'
    $containsJwt = $Value -match '\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b'
    $containsTokenShape = $Value -match '(?i)\b(?:sk|ghp|xox[baprs])-[A-Za-z0-9_-]{10,}\b' -or $Value -match '\bAKIA[0-9A-Z]{16}\b'
    $containsOpaqueSecret = $Value -match '\b[A-Fa-f0-9]{32,}\b'
    if ($containsCredentialLabel -or $containsBearer -or $containsJwt -or $containsTokenShape -or $containsOpaqueSecret) {
        throw "$Name must not contain credentials or secrets."
    }
}

function Move-FeedbackArchive {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    # 目标 archive 固定覆盖，保证文件数量不会随调用次数增长。
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Force
    }
    if (Test-Path -LiteralPath $Source) {
        Move-Item -LiteralPath $Source -Destination $Destination
    }
}

function Assert-NoReparsePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    # 逐段检查已存在组件；缺失尾段由调用方创建后再次检查。
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
    $current = $pathRoot
    $relativePath = $fullPath.Substring($pathRoot.Length)
    foreach ($segment in $relativePath.Split([char[]]@('\', '/'), [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        try {
            $attributes = [System.IO.File]::GetAttributes($current)
        } catch {
            $cause = $_.Exception.InnerException
            if ($_.Exception -is [System.IO.FileNotFoundException] -or
                $_.Exception -is [System.IO.DirectoryNotFoundException] -or
                $cause -is [System.IO.FileNotFoundException] -or
                $cause -is [System.IO.DirectoryNotFoundException]) {
                break
            }
            throw
        }
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "LOCALAPPDATA and feedback paths must not contain reparse points."
        }
    }
}

Assert-SafeFeedbackText -Name "Summary" -Value $Summary
Assert-SafeFeedbackText -Name "Workaround" -Value $Workaround
if ($Category -eq "workaround" -and [string]::IsNullOrWhiteSpace($Workaround)) {
    throw "Workaround is required when Category is workaround."
}
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw "LOCALAPPDATA is required to store local unity_cli feedback."
}
# UNC、映射盘和其他远端 provider 会绕过本机 mutex，因此仅接受固定本地盘。
try {
    $localAppDataRoot = [System.IO.Path]::GetFullPath($env:LOCALAPPDATA)
    $driveRoot = [System.IO.Path]::GetPathRoot($localAppDataRoot)
    if ($driveRoot -notmatch '^[A-Za-z]:\\$') {
        throw "LOCALAPPDATA must use a local drive path."
    }
    $driveInfo = New-Object System.IO.DriveInfo($driveRoot)
    if ($driveInfo.DriveType -ne [System.IO.DriveType]::Fixed) {
        throw "LOCALAPPDATA must use a fixed local drive."
    }
} catch {
    throw "LOCALAPPDATA must resolve to a fixed local drive."
}

$feedbackRoot = Join-Path $localAppDataRoot "UnityCli\feedback"
$mainPath = Join-Path $feedbackRoot "feedback.jsonl"
$recordId = "fb_" + [Guid]::NewGuid().ToString("N")
$capabilityValue = $null
$errorCodeValue = $null
$workaroundValue = $null
if (-not [string]::IsNullOrWhiteSpace($Capability)) { $capabilityValue = $Capability }
if (-not [string]::IsNullOrWhiteSpace($ErrorCode)) { $errorCodeValue = $ErrorCode }
if (-not [string]::IsNullOrWhiteSpace($Workaround)) { $workaroundValue = $Workaround }
$record = [ordered]@{
    schemaVersion = 1
    recordId = $recordId
    createdAtUtc = [DateTime]::UtcNow.ToString("o")
    category = $Category
    phase = $Phase
    capability = $capabilityValue
    errorCode = $errorCodeValue
    summary = $Summary
    workaround = $workaroundValue
    resolved = -not [string]::IsNullOrWhiteSpace($Workaround)
}
$jsonLine = $record | ConvertTo-Json -Compress -Depth 4
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$lineBytes = $utf8NoBom.GetByteCount($jsonLine + [Environment]::NewLine)
if ($lineBytes -gt $maxFileBytes) {
    throw "Feedback record exceeds the per-file byte limit."
}

try {
    # named mutex 只保护本机同一用户反馈目录；进程退出后由 Windows 自动释放。
    $mutex = New-Object System.Threading.Mutex($false, "Local\UnityCliFeedback_v1")
    try {
        $mutexHeld = $mutex.WaitOne(10000)
    } catch [System.Threading.AbandonedMutexException] {
        $mutexHeld = $true
    }
    if (-not $mutexHeld) {
        throw "Timed out waiting for the unity_cli feedback writer."
    }

    Assert-NoReparsePath -Path $feedbackRoot
    [System.IO.Directory]::CreateDirectory($feedbackRoot) | Out-Null
    Assert-NoReparsePath -Path $feedbackRoot
    $candidatePaths = @($mainPath)
    $candidatePaths += @(1..$archiveCount | ForEach-Object { Join-Path $feedbackRoot ("feedback.{0}.jsonl" -f $_) })
    foreach ($candidatePath in $candidatePaths) {
        Assert-NoReparsePath -Path $candidatePath
    }
    $rotated = $false
    if (Test-Path -LiteralPath $mainPath) {
        $currentBytes = (Get-Item -LiteralPath $mainPath).Length
        if ($currentBytes + $lineBytes -gt $maxFileBytes) {
            for ($index = $archiveCount; $index -ge 2; $index--) {
                Move-FeedbackArchive `
                    -Source (Join-Path $feedbackRoot ("feedback.{0}.jsonl" -f ($index - 1))) `
                    -Destination (Join-Path $feedbackRoot ("feedback.{0}.jsonl" -f $index))
            }
            Move-FeedbackArchive -Source $mainPath -Destination (Join-Path $feedbackRoot "feedback.1.jsonl")
            $rotated = $true
        }
    }

    [System.IO.File]::AppendAllText($mainPath, $jsonLine + [Environment]::NewLine, $utf8NoBom)
    [ordered]@{
        ok = $true
        recordId = $recordId
        path = $mainPath
        rotated = $rotated
    } | ConvertTo-Json -Compress
} finally {
    if ($mutexHeld -and $null -ne $mutex) {
        try { $mutex.ReleaseMutex() } catch { }
    }
    if ($null -ne $mutex) {
        $mutex.Dispose()
    }
}
