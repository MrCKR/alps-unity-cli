$ErrorActionPreference = "Stop"
$dataRoot = $env:UNITYCLI_DUAL_WRITER_DATA_ROOT
$instanceId = $env:UNITYCLI_DUAL_WRITER_INSTANCE_ID
$readyPath = $env:UNITYCLI_DUAL_WRITER_READY
$releasedPath = $env:UNITYCLI_DUAL_WRITER_RELEASED
$registryPath = Join-Path $dataRoot "instances/$instanceId.json"
$temporaryPath = $registryPath + ".tmp"
$backupPath = Join-Path (Split-Path $readyPath) "dual-writer-backup.json"
$mutexName = "Local\UnityCli.Registry.$instanceId"
$mutex = [System.Threading.Mutex]::new($false, $mutexName)
$held = $false
try {
  try { $held = $mutex.WaitOne(5000) } catch [System.Threading.AbandonedMutexException] { $held = $true }
  if (-not $held) { throw "Competing writer could not acquire the project mutex." }

  $record = Get-Content -Raw -LiteralPath $registryPath | ConvertFrom-Json
  $current = @($record.sessions)[0]
  $foreign = $current | ConvertTo-Json -Compress -Depth 100 | ConvertFrom-Json
  $foreignSessionId = "session-cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
  $now = [DateTime]::UtcNow.ToString("O")
  $foreign.sessionId = $foreignSessionId
  $foreign.editorSessionId = $foreignSessionId
  $foreign.processId = [long]$PID
  $foreign.processStartedAt = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString("O")
  $foreign.publishedAt = $now
  $foreign.publicationSequence = [long]1
  $foreign.listener.listenerId = "listener-cccccccccccccccccccccccccccccccc"
  $foreign.lastUnitySnapshot.sourceEditorSessionId = $foreignSessionId
  $foreign.lastUnitySnapshot.observedAt = $now
  $record.sessions = @($record.sessions) + @($foreign)
  $record.revision = [long]$record.revision + 1
  $record.publishedAt = $now
  $content = ($record | ConvertTo-Json -Compress -Depth 100) + "`n"

  $stream = [System.IO.FileStream]::new($temporaryPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None, 4096, [System.IO.FileOptions]::WriteThrough)
  try {
    $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false))
    try { $writer.Write($content); $writer.Flush(); $stream.Flush($true) } finally { $writer.Dispose() }
  } finally { $stream.Dispose() }
  if (Test-Path -LiteralPath $registryPath) {
    Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
    [System.IO.File]::Replace($temporaryPath, $registryPath, $backupPath)
    Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
  }
  else { [System.IO.File]::Move($temporaryPath, $registryPath) }

  [ordered]@{ pid = $PID; processStartedAt = $foreign.processStartedAt; acquiredAt = $now } | ConvertTo-Json -Compress | Set-Content -LiteralPath $readyPath -Encoding utf8NoBOM
  Start-Sleep -Seconds 12
  $mutex.ReleaseMutex()
  $held = $false
  [ordered]@{ pid = $PID; releasedAt = [DateTime]::UtcNow.ToString("O") } | ConvertTo-Json -Compress | Set-Content -LiteralPath $releasedPath -Encoding utf8NoBOM
  Start-Sleep -Seconds 20
} finally {
  if ($held) { try { $mutex.ReleaseMutex() } catch {} }
  $mutex.Dispose()
  if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue }
  if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue }
}
