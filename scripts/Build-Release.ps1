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

$effVid = if ($UsbVid) { Parse-HexId $UsbVid } else { $headerVid }
$effBridgePid = if ($BridgePid) { Parse-HexId $BridgePid } else { $headerBridgePid }
$effMixerPid = if ($MixerPid) { Parse-HexId $MixerPid } else { $headerMixerPid }
if ($DevTestIds -and ($effVid -eq 0 -or $effBridgePid -eq 0 -or $effMixerPid -eq 0)) {
    $effVid = 0xcafe
    $effBridgePid = 0x401d
    $effMixerPid = 0x4026
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
    if ($UsbVid)    { $cmakeDefs += "-DUSB_VID=$(Format-CDef $UsbVid)" }
    if ($BridgePid) { $cmakeDefs += "-DBRIDGE_USB_PID=$(Format-CDef $BridgePid)" }
    if ($MixerPid)  { $cmakeDefs += "-DMIXER_USB_PID=$(Format-CDef $MixerPid)" }
    if ($DevTestIds) { $cmakeDefs += '-DALLOW_DEV_USB_IDS=1' }

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

    Copy-Item -LiteralPath "$txBin/switch2_usb_bridge.uf2" -Destination (Join-Path $distDir 'switch2-usb-bridge.uf2') -Force
    Copy-Item -LiteralPath "$rxBin/pico_audio_pack.uf2"    -Destination (Join-Path $distDir 'pico2h-audio-pack-uac2.uf2') -Force

    if ($DevTestIds) {
        Write-Warning 'Built with -DevTestIds (0xCAFE test IDs). Do NOT distribute these UF2 binaries publicly.'
    }

    Get-FileHash -LiteralPath (Join-Path $distDir 'switch2-usb-bridge.uf2'), (Join-Path $distDir 'pico2h-audio-pack-uac2.uf2') -Algorithm SHA256 |
        Format-Table -AutoSize
} finally {
    $env:PATH = $oldPath
    $env:BRIDGE_TEST_ROOT = $beforeTestRoot
    $env:BRIDGE_DEPS_DIR = $beforeDepsDir
    foreach ($d in $mappedDrives) {
        & subst.exe $d /D | Out-Null
    }
}
