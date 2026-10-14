# addons/dlss5/verify.ps1 - end-to-end check of the DLSS5 integration:
# dlls present -> editor scan -> headless probe enhance (PROBE-OK expected).
$ErrorActionPreference = "Stop"
$addon = $PSScriptRoot

if (-not (Test-Path "$addon\bin\libdlss5.windows.template_release.x86_64.dll")) {
    Write-Host "FAIL: extension dll missing (run provision.ps1)"
    exit 1
}
Write-Host "[1/2] runtime blobs present"

$repoRoot = Split-Path (Split-Path (Split-Path (Split-Path $addon -Parent) -Parent) -Parent) -Parent
$godot = Join-Path $repoRoot "tools\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
$proj = Split-Path (Split-Path $addon -Parent) -Parent

# CUDA runtime libs (torch's bundle) enable the GPU path; absent -> CPU is fine.
if (-not $env:PATH.Contains("torch\lib")) {
    $env:PATH = "D:\python312\Lib\site-packages\torch\lib;" + $env:PATH
}

Write-Host "[2/2] headless probe (loads fp16 graph + enhances the DS sample)"
# Sample PNG: pass as first arg or set UO_DLSS_SAMPLE. No machine path is committed here.
$sample = if ($args.Count -gt 0) { $args[0] } elseif ($env:UO_DLSS_SAMPLE) { $env:UO_DLSS_SAMPLE } else { Write-Host "FAIL: pass a sample PNG path or set UO_DLSS_SAMPLE"; exit 1 }
$out    = Join-Path $env:TEMP "dlss5_guo_verify.png"
$p = Start-Process -FilePath $godot -ArgumentList "--headless --path `"$proj`" --script res://dev/dlss5_probe.gd -- `"$sample`" `"$out`" 1.0 0" -NoNewWindow -PassThru -RedirectStandardOutput "$env:TEMP\dlss5_verify.log" -RedirectStandardError "$env:TEMP\dlss5_verify.err"
if (-not $p.WaitForExit(300 * 1000)) { Stop-Process -Id $p.Id -Force; Write-Host "FAIL: probe timed out"; exit 1 }
Select-String -Path "$env:TEMP\dlss5_verify.log","$env:TEMP\dlss5_verify.err" -Pattern "PROBE" | ForEach-Object { Write-Host $_.Line }
if ((Select-String -Path "$env:TEMP\dlss5_verify.log" -Pattern "PROBE-OK").Count -gt 0) {
    Write-Host "`nVERIFY-PASS  output: $out"
    exit 0
}
Write-Host "`nVERIFY-FAIL (see $env:TEMP\dlss5_verify.err)"
exit 1
