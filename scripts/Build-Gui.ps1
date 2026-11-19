# SPDX-License-Identifier: MIT
# Copyright (c) 2026 mugicha_xtx
param(
    [ValidatePattern('^$|^(0x)?[0-9a-fA-F]{1,4}u?$')][string]$UsbVid = '',
    [ValidatePattern('^$|^(0x)?[0-9a-fA-F]{1,4}u?$')][string]$MixerPid = '',
    [switch]$DevTestIds,
    [string]$OutPath = '',
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'

function Parse-HexId([string]$raw) {
    $clean = ($raw -replace '^0[xX]', '' -replace '[uU]$', '').Trim()
    return [Convert]::ToUInt16($clean, 16)
}

$root = Split-Path $PSScriptRoot -Parent
$guiSrc = Join-Path $root 'host-gui/BiwamasuMixerGui.cs'
if (!(Test-Path -LiteralPath $guiSrc)) {
    throw "GUI source not found: $guiSrc"
}

$usbIdsPath = Join-Path $root 'include/usb_ids.h'
$headerVid = 0
$headerMixerPid = 0
if (Test-Path -LiteralPath $usbIdsPath) {
    $usbIdsText = Get-Content -Raw -Encoding UTF8 -LiteralPath $usbIdsPath
    if ($usbIdsText -match '#define\s+USB_VID\s+(0x[0-9a-fA-F]+u?)') { $headerVid = Parse-HexId $matches[1] }
    if ($usbIdsText -match '#define\s+MIXER_USB_PID\s+(0x[0-9a-fA-F]+u?)') { $headerMixerPid = Parse-HexId $matches[1] }
}

$effVid = if ($UsbVid) { Parse-HexId $UsbVid } else { $headerVid }
$effMixerPid = if ($MixerPid) { Parse-HexId $MixerPid } else { $headerMixerPid }
if (($DevTestIds -or $CheckOnly) -and ($effVid -eq 0 -or $effMixerPid -eq 0)) {
    $effVid = 0xcafe
    $effMixerPid = 0x4026
}

if (!$CheckOnly -and ($effVid -eq 0 -or $effMixerPid -eq 0)) {
    throw 'Authorized USB VID/PID not configured yet (0x0000). Set USB_VID and MIXER_USB_PID in include/usb_ids.h after official PID allocation (or pass -DevTestIds for local testing only).'
}

$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$csc) {
    throw 'Built-in .NET Framework 4.x C# compiler (csc.exe) was not found on this Windows installation.'
}

$tempCfg = Join-Path ([IO.Path]::GetTempPath()) ("BiwamasuBuildConfig_" + $PID + ".cs")
$tempBootExe = Join-Path ([IO.Path]::GetTempPath()) ("BiwamasuIconBoot_" + $PID + ".exe")
$tempIco = Join-Path ([IO.Path]::GetTempPath()) ("BiwamasuFish_" + $PID + ".ico")
$targetExe = if ($CheckOnly) {
    Join-Path ([IO.Path]::GetTempPath()) ("BiwamasuMixer_Check_" + $PID + ".exe")
} elseif ($OutPath) {
    $OutPath
} else {
    Join-Path $root 'dist/Biwamasu-Mixer Setting tool.exe'
}

$outDir = Split-Path $targetExe -Parent
if ($outDir -and !(Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

try {
    $cfgSource = @"
namespace BiwamasuMixerGui {
    internal static class BuildConfig {
        public const ushort DefaultVid = 0x$('{0:x4}' -f $effVid);
        public const ushort DefaultMixerPid = 0x$('{0:x4}' -f $effMixerPid);
    }
}
"@
    [IO.File]::WriteAllText($tempCfg, $cfgSource, [Text.UTF8Encoding]::new($false))

    # Stage 1: Build icon generator bootstrapper and render multi-size U+1F41F fish emoji (.ico) via Direct2D
    & $csc /nologo /codepage:65001 /target:winexe /optimize+ /utf8output `
        /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
        "/out:$tempBootExe" $guiSrc $tempCfg
    if ($LASTEXITCODE -ne 0) {
        throw "csc.exe icon bootstrap failed with exit code $LASTEXITCODE"
    }

    $icoProc = Start-Process -FilePath $tempBootExe -ArgumentList @('--write-ico', "`"$tempIco`"") -Wait -PassThru -WindowStyle Hidden
    if ($icoProc.ExitCode -ne 0 -or !(Test-Path -LiteralPath $tempIco) -or ((Get-Item -LiteralPath $tempIco).Length -lt 1024)) {
        throw "Failed to generate fish emoji .ico resource (exit code $($icoProc.ExitCode))"
    }

    # Stage 2: Compile final executable with Win32 PE icon + embedded managed icon stream
    & $csc /nologo /codepage:65001 /target:winexe /optimize+ /utf8output `
        /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
        "/win32icon:$tempIco" "/resource:$tempIco,BiwamasuMixerGui.fish.ico" `
        "/out:$targetExe" $guiSrc $tempCfg
    if ($LASTEXITCODE -ne 0) {
        throw "csc.exe failed with exit code $LASTEXITCODE"
    }

    $proc = Start-Process -FilePath $targetExe -ArgumentList '--self-test' -Wait -PassThru -WindowStyle Hidden
    if ($proc.ExitCode -ne 0) {
        throw "Biwamasu-Mixer Setting tool.exe --self-test failed with exit code $($proc.ExitCode)"
    }

    if ($CheckOnly) {
        [ordered]@{
            CheckOnlyPassed = $true
            Compiler = $csc
            DefaultVid = ('0x{0:x4}' -f $effVid)
            DefaultMixerPid = ('0x{0:x4}' -f $effMixerPid)
            IconBytes = (Get-Item -LiteralPath $tempIco).Length
            SizeBytes = (Get-Item -LiteralPath $targetExe).Length
        } | ConvertTo-Json
    } else {
        try {
            if (!('ShellIconRefresh' -as [type])) {
                Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ShellIconRefresh { [DllImport("shell32.dll")] public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2); }'
            }
            [ShellIconRefresh]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
        } catch {}
        Write-Host ("Built GUI executable: {0} (VID=0x{1:x4}, PID=0x{2:x4})" -f $targetExe, $effVid, $effMixerPid)
    }
} finally {
    foreach ($tmp in @($tempCfg, $tempBootExe, $tempIco)) {
        if ($tmp -and (Test-Path -LiteralPath $tmp)) {
            Remove-Item -Force -LiteralPath $tmp -ErrorAction SilentlyContinue
        }
    }
    if ($CheckOnly -and (Test-Path -LiteralPath $targetExe)) {
        Remove-Item -Force -LiteralPath $targetExe -ErrorAction SilentlyContinue
    }
}
