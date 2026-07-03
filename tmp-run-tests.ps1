$out = & npm test 2>&1
$txt = ($out -split "`n") -join "`n"
$start = $txt.IndexOf("not ok 84")
if ($start -ge 0) {
  $segment = $txt.Substring($start, [Math]::Min(2500, $txt.Length - $start))
  Write-Output $segment
}
