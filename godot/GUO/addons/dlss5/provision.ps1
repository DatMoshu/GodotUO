# addons/dlss5/provision.ps1 - copy runtime blobs from the source workspace
# if they are missing, run the one-time editor scan, then verify.
# Safe to re-run any time; skips work that is already done.
$ErrorActionPreference = "Stop"

$addon   = $PSScriptRoot
# DLSS source checkout (uncommitted blobs): set UO_DLSS_SRC to the folder that
# holds DlssGodot + dlss5-standalone (e.g. in config.local.bat, gitignored).
# No machine path is committed here.
if (-not $env:UO_DLSS_SRC -or -not (Test-Path $env:UO_DLSS_SRC)) {
    Write-Host "FAIL: set UO_DLSS_SRC to your DLSS source checkout (holds DlssGodot + dlss5-standalone)"
    exit 1
}
$srcDll  = Join-Path $env:UO_DLSS_SRC "DlssGodot\godot-demo\bin"
$srcWt   = Join-Path $env:UO_DLSS_SRC "dlss5-standalone\weights"
$srcTabs = Join-Path $env:UO_DLSS_SRC "DlssGodot\native\scaffold"

function Copy-IfMissing($from, $to) {
    if (-not (Test-Path $to)) {
        Copy-Item -LiteralPath $from -Destination $to -Force
        Write-Host "copied $(Split-Path $from -Leaf)"
    } else {
        Write-Host "have   $(Split-Path $to -Leaf)"
    }
}

New-Item -ItemType Directory -Force -Path "$addon\bin", "$addon\models" | Out-Null

Copy-IfMissing "$srcDll\libdlss5.windows.template_release.x86_64.dll" "$addon\bin\libdlss5.windows.template_release.x86_64.dll"
Copy-IfMissing "$srcDll\onnxruntime.dll"                              "$addon\bin\onnxruntime.dll"
Copy-IfMissing "$srcDll\onnxruntime_providers_shared.dll"             "$addon\bin\onnxruntime_providers_shared.dll"
Copy-IfMissing "$srcDll\onnxruntime_providers_cuda.dll"               "$addon\bin\onnxruntime_providers_cuda.dll"
Copy-IfMissing "$srcWt\dlssnr-core-320-fp16-vendorsm.onnx"            "$addon\models\dlssnr-core-320-fp16-vendorsm.onnx"
Copy-IfMissing "$srcWt\dlssnr-core-320-vendorsm.onnx"                 "$addon\models\dlssnr-core-320-vendorsm.onnx"
Copy-IfMissing "$srcTabs\dlss_tables.bin"                             "$addon\models\dlss_tables.bin"

Write-Host "`n[1/2] editor scan (registers the GDExtension; fast when .godot exists)"
$godot = (Get-Item "$PSScriptRoot\..\..\..\tools\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe").FullName
$godotExe = Join-Path (Split-Path (Split-Path (Split-Path (Split-Path $addon -Parent) -Parent) -Parent) -Parent) "tools\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
$p = Start-Process -FilePath $godotExe -ArgumentList "--headless","--editor","--quit","--path",(Split-Path (Split-Path $addon -Parent) -Parent) -PassThru -NoNewWindow
if (-not $p.WaitForExit(600 * 1000)) { Stop-Process -Id $p.Id -Force; Write-Warning "editor scan timed out"; exit 1 }
Write-Host "scan ok"

Write-Host "`n[2/2] verify (see verify.ps1)"
& "$addon\verify.ps1"
exit $LASTEXITCODE
