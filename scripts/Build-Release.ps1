# SPDX-License-Identifier: MIT
# Copyright (c) 2026 mugicha_xtx
param(
    [ValidatePattern('^$|^(0x)?[0-9a-fA-F]{1,4}u?$')][string]$UsbVid = '',
    [ValidatePattern('^$|^(0x)?[0-9a-fA-F]{1,4}u?$')][string]$BridgePid = '',
    [ValidatePattern('^$|^(0x)?[0-9a-fA-F]{1,4}u?$')][string]$MixerPid = '',
    [switch]$DevTestIds,
    [string]$DepsDir = ''
)
$ErrorActionPreference = 'Stop'

function Parse-HexId([string]$raw) {
    $clean = ($raw -replace '^0[xX]', '' -replace '[uU]$', '').Trim()
    return [Convert]::ToUInt16($clean, 16)
}

function Format-CDef([string]$raw) {
    $val = Parse-HexId $raw
    return ('0x{0:x4}u' -f $val)
}

$root = Split-Path $PSScriptRoot -Parent
$distDir = Join-Path $root 'dist'
if (!(Test-Path -LiteralPath $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}

# Locate .deps directory (local first, then parent workspace if developing inside a fork/subproject)
$resolvedDeps = $null
$candidates = @()
if ($DepsDir) {
    $candidates += (Resolve-Path -LiteralPath $DepsDir).Path
} else {
    $candidates += (Join-Path $root '.deps')
    $parentRoot = Split-Path $root -Parent
    if ($parentRoot) {
        $candidates += (Join-Path $parentRoot '.deps')
    }
}
foreach ($cand in $candidates) {
    if (Test-Path -LiteralPath (Join-Path $cand 'bin/arm-none-eabi-gcc.exe')) {
        $resolvedDeps = $cand
        break
    }
}
if (!$resolvedDeps) {
    throw 'Could not locate .deps toolchain. Run .\scripts\Setup-Toolchain.ps1 first (or pass -DepsDir).'
}

# Read configured IDs from include/usb_ids.h for artifact checks
$usbIdsPath = Join-Path $root 'include/usb_ids.h'
$usbIdsText = Get-Content -Raw -LiteralPath $usbIdsPath
$headerVid = if ($usbIdsText -match '#define\s+USB_VID\s+(0x[0-9a-fA-F]+u?)') { Parse-HexId $matches[1] } else { 0 }
$headerBridgePid = if ($usbIdsText -match '#define\s+BRIDGE_USB_PID\s+(0x[0-9a-fA-F]+u?)') { Parse-HexId $matches[1] } else { 0 }
$headerMixerPid = if ($usbIdsText -match '#define\s+MIXER_USB_PID\s+(0x[0-9a-fA-F]+u?)') { Parse-HexId $matches[1] } else { 0 }

$effVid = $headerVid
$effBridgePid = $headerBridgePid
$effMixerPid = $headerMixerPid
if ($DevTestIds) {
    $effVid = 0xcafe
    $effBridgePid = 0x401d
    $effMixerPid = 0x4026
}
if ($UsbVid)    { $effVid = Parse-HexId $UsbVid }
if ($BridgePid) { $effBridgePid = Parse-HexId $BridgePid }
if ($MixerPid)  { $effMixerPid = Parse-HexId $MixerPid }

if ($effVid -eq 0 -or $effBridgePid -eq 0 -or $effMixerPid -eq 0) {
    throw 'Authorized USB VID/PID not configured yet (0x0000). Set USB_VID, BRIDGE_USB_PID, and MIXER_USB_PID in include/usb_ids.h after official PID allocation (or pass -DevTestIds for local testing only).'
}
if (!$DevTestIds -and $effVid -eq 0xcafe) {
    throw 'Development test VID (0xCAFE) requires -DevTestIds and must never be packaged into a public release ZIP.'
}
if ($effBridgePid -eq $effMixerPid) {
    throw "BRIDGE_USB_PID and MIXER_USB_PID must be distinct (both were 0x$('{0:x4}' -f $effBridgePid))."
}

$mappedDrives = @()
function Map-AsciiPath([string]$path) {
    if ($path -notmatch '[^\x00-\x7f]') { return $path }
    $used = [IO.Directory]::GetLogicalDrives()
    $letter = @('R','S','T','U','V','W','X','Y','Z') | Where-Object { $used -notcontains "${_}:\" } | Select-Object -First 1
    if ($letter) {
        $drive = "${letter}:"
        $null = & subst.exe $drive $path 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:mappedDrives += $drive
            return "$drive\"
        }
    }
    $fso = New-Object -ComObject Scripting.FileSystemObject
    $short = $fso.GetFolder($path).ShortPath
    if ($short -and ($short -notmatch '[^\x00-\x7f]')) {
        return $short
    }
    throw "Could not map non-ASCII path to ASCII drive: $path"
}

$beforeTestRoot = $env:BRIDGE_TEST_ROOT
$beforeDepsDir = $env:BRIDGE_DEPS_DIR
$oldPath = $env:PATH

try {
    $asciiRoot = Map-AsciiPath $root
    if ($resolvedDeps.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        $relDeps = $resolvedDeps.Substring($root.Length).TrimStart('\')
        $asciiDeps = Join-Path $asciiRoot $relDeps
    } else {
        $depsParent = Split-Path $resolvedDeps -Parent
        $depsLeaf = Split-Path $resolvedDeps -Leaf
        $asciiDepsParent = Map-AsciiPath $depsParent
        $asciiDeps = Join-Path $asciiDepsParent $depsLeaf
    }

    $posixRoot = ($asciiRoot.TrimEnd('\')) -replace '\\', '/'
    $posixDeps = ($asciiDeps.TrimEnd('\')) -replace '\\', '/'
    $cmake = "$posixDeps/cmake-3.31.6-windows-x86_64/bin/cmake.exe"
    $ninja = "$posixDeps/ninja/ninja.exe"
    $arm = "$posixDeps/bin"
    $python = "$posixDeps/python/python.exe"
    $pioasmDir = "$posixDeps/sdk-tools/pioasm"
    $picotoolDir = "$posixDeps/picotool/picotool"
    $picoSdkDir = "$posixDeps/pico-sdk"
    $tinyUsbDir = "$posixDeps/tinyusb"

    foreach ($p in @($cmake, $ninja, "$arm/arm-none-eabi-gcc.exe", $python, $picoSdkDir, $tinyUsbDir)) {
        if (!(Test-Path -LiteralPath $p)) {
            throw "Missing required dependency: $p. Run .\scripts\Setup-Toolchain.ps1 first."
        }
    }

    $env:PATH = "$arm;$posixDeps/python;$pioasmDir;$oldPath"
    $env:BRIDGE_TEST_ROOT = $asciiRoot
    $env:BRIDGE_DEPS_DIR = $asciiDeps

    Write-Host 'Running rx-mixer C unit / EQ selftests...'
    & $python "$posixRoot/tests/test_rx_dsp_arm.py"
    if ($LASTEXITCODE) { throw 'test_rx_dsp_arm.py failed' }

    $cmakeDefs = @(
        "-DPICO_SDK_PATH=$picoSdkDir",
        "-DPICO_TINYUSB_PATH=$tinyUsbDir",
        "-DCMAKE_MAKE_PROGRAM=$ninja",
        "-DPICO_TOOLCHAIN_PATH=$arm",
        "-DPython3_EXECUTABLE=$python",
        "-DPython_EXECUTABLE=$python",
        "-Dpicotool_DIR=$picotoolDir",
        "-Dpioasm_DIR=$pioasmDir",
        '-DPICO_BOARD=pico2',
        '-DCMAKE_BUILD_TYPE=Release'
    )
    if ($DevTestIds) {
        $cmakeDefs += '-DALLOW_DEV_USB_IDS=1'
        $cmakeDefs += "-DUSB_VID=$(Format-CDef ('0x{0:x4}' -f $effVid))"
        $cmakeDefs += "-DBRIDGE_USB_PID=$(Format-CDef ('0x{0:x4}' -f $effBridgePid))"
        $cmakeDefs += "-DMIXER_USB_PID=$(Format-CDef ('0x{0:x4}' -f $effMixerPid))"
    } else {
        if ($UsbVid)    { $cmakeDefs += "-DUSB_VID=$(Format-CDef $UsbVid)" }
        if ($BridgePid) { $cmakeDefs += "-DBRIDGE_USB_PID=$(Format-CDef $BridgePid)" }
        if ($MixerPid)  { $cmakeDefs += "-DMIXER_USB_PID=$(Format-CDef $MixerPid)" }
    }

    # 1. Build Switch-side TX bridge (Pico 2 H)
    $txBin = "$posixRoot/build-tx-bridge"
    Write-Host 'Configuring & building tx-bridge (switch2_usb_bridge)...'
    & $cmake --fresh -S "$posixRoot/firmware/tx-bridge" -B $txBin -G Ninja @cmakeDefs
    if ($LASTEXITCODE) { throw 'CMake configure failed for tx-bridge' }
    & $cmake --build $txBin --parallel 4
    if ($LASTEXITCODE) { throw 'Build failed for tx-bridge' }

    $vidHex = ('{0:x4}' -f $effVid)
    $txPidHex = ('{0:x4}' -f $effBridgePid)
    & $python "$posixRoot/tests/check_tx_artifact.py" "$txBin/switch2_usb_bridge.elf" "$txBin/switch2_usb_bridge.uf2" $vidHex $txPidHex
    if ($LASTEXITCODE) { throw 'tx-bridge artifact check failed' }

    # 2. Build PC-side RX Audio Pack Mixer (Pico 2 H)
    $rxBin = "$posixRoot/build-rx-mixer"
    Write-Host 'Configuring & building rx-mixer (pico_audio_pack)...'
    & $cmake --fresh -S "$posixRoot/firmware/rx-mixer" -B $rxBin -G Ninja @cmakeDefs
    if ($LASTEXITCODE) { throw 'CMake configure failed for rx-mixer' }
    & $cmake --build $rxBin --parallel 4
    if ($LASTEXITCODE) { throw 'Build failed for rx-mixer' }

    $rxPidHex = ('{0:x4}' -f $effMixerPid)
    & $python "$posixRoot/tests/check_rx_artifact.py" "$rxBin/pico_audio_pack.elf" "$rxBin/pico_audio_pack.uf2" $vidHex $rxPidHex
    if ($LASTEXITCODE) { throw 'rx-mixer artifact check failed' }

    $txUf2 = Join-Path $distDir 'biwamasu-mixer-tx.uf2'
    $rxUf2 = Join-Path $distDir 'biwamasu-mixer-rx.uf2'
    $guiExe = Join-Path $distDir 'Biwamasu-Mixer Setting tool.exe'
    Copy-Item -LiteralPath "$txBin/switch2_usb_bridge.uf2" -Destination $txUf2 -Force
    Copy-Item -LiteralPath "$rxBin/pico_audio_pack.uf2"    -Destination $rxUf2 -Force

    Write-Host 'Building Biwamasu-Mixer Setting tool.exe Windows GUI...'
    $guiArgs = @{
        UsbVid    = ('0x{0:x4}' -f $effVid)
        BridgePid = ('0x{0:x4}' -f $effBridgePid)
        MixerPid  = ('0x{0:x4}' -f $effMixerPid)
        OutPath   = $guiExe
    }
    if ($DevTestIds) { $guiArgs['DevTestIds'] = $true }
    & (Join-Path $root 'scripts/Build-Gui.ps1') @guiArgs

    # Package self-contained GitHub Releases archive
    $bundleRoot = Join-Path $distDir 'bundle'
    if (Test-Path -LiteralPath $bundleRoot) {
        Remove-Item -Recurse -Force -LiteralPath $bundleRoot
    }
    $bundleDir = Join-Path $bundleRoot 'biwamasu-mixer'
    foreach ($sub in @('scripts', 'include', 'presets', 'images', 'hardware', 'licenses', 'docs')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $bundleDir $sub) | Out-Null
    }
    Copy-Item -LiteralPath $txUf2, $rxUf2, $guiExe, (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $bundleDir -Force
    Copy-Item -LiteralPath (Join-Path $root 'scripts/Control-BiwamasuMixer.ps1') -Destination (Join-Path $bundleDir 'scripts') -Force
    Copy-Item -LiteralPath (Join-Path $root 'presets/sample-peace.txt') -Destination (Join-Path $bundleDir 'presets') -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs/USB_PID_APPLICATION.md') -Destination (Join-Path $bundleDir 'docs') -Force
    Get-ChildItem -LiteralPath (Join-Path $root 'images') -File |
        Where-Object { $_.Extension -in @('.jpg', '.png') } |
        Copy-Item -Destination (Join-Path $bundleDir 'images') -Force
    Copy-Item -LiteralPath (Join-Path $root 'hardware/schematic.svg'), (Join-Path $root 'hardware/README.md') -Destination (Join-Path $bundleDir 'hardware') -Force
    Get-ChildItem -LiteralPath (Join-Path $root 'licenses') -File |
        Copy-Item -Destination (Join-Path $bundleDir 'licenses') -Force

    $stampedUsbIds = $usbIdsText `
        -replace '(#define\s+USB_VID\s+)0x[0-9a-fA-F]+u?', "`${1}$('0x{0:x4}u' -f $effVid)" `
        -replace '(#define\s+BRIDGE_USB_PID\s+)0x[0-9a-fA-F]+u?', "`${1}$('0x{0:x4}u' -f $effBridgePid)" `
        -replace '(#define\s+MIXER_USB_PID\s+)0x[0-9a-fA-F]+u?', "`${1}$('0x{0:x4}u' -f $effMixerPid)"
    [IO.File]::WriteAllText((Join-Path $bundleDir 'include/usb_ids.h'), $stampedUsbIds, [Text.UTF8Encoding]::new($false))

    $zipName = if ($DevTestIds) { 'biwamasu-mixer-dev-do-not-distribute.zip' } else { 'biwamasu-mixer-release.zip' }
    $zipPath = Join-Path $distDir $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -Force -LiteralPath $zipPath }
    Compress-Archive -Path "$bundleRoot\*" -DestinationPath $zipPath -Force
    Remove-Item -Recurse -Force -LiteralPath $bundleRoot

    if ($DevTestIds) {
        Write-Warning 'Built with -DevTestIds (0xCAFE test IDs). Do NOT distribute these UF2 binaries or dev ZIP publicly.'
    }

    Get-FileHash -LiteralPath $txUf2, $rxUf2, $guiExe, $zipPath -Algorithm SHA256 |
        Format-Table -AutoSize
} finally {
    $env:PATH = $oldPath
    $env:BRIDGE_TEST_ROOT = $beforeTestRoot
    $env:BRIDGE_DEPS_DIR = $beforeDepsDir
    foreach ($d in $mappedDrives) {
        & subst.exe $d /D | Out-Null
    }
}
