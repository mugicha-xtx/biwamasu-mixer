# SPDX-License-Identifier: MIT
# Copyright (c) 2026 mugicha_xtx
param(
    [Nullable[double]]$MasterDb = $null,
    [Nullable[double]]$GameDb = $null,
    [Nullable[double]]$PcTrimDb = $null,
    [ValidateSet('', 'On', 'Off')][string]$Eq = '',
    [ValidateSet('', 'Auto', '1', '13')][string]$UartRxPin = '',
    [string]$PeaceFile = '',
    [ValidateSet('', 'On', 'Off')][string]$TestTone = '',
    [ValidatePattern('^$|^(0x)?[0-9a-fA-F]{1,4}u?$')][string]$UsbVid = '',
    [ValidatePattern('^$|^(0x)?[0-9a-fA-F]{1,4}u?$')][string]$UsbPid = '',
    [switch]$DevTestIds,
    [switch]$Save,
    [switch]$ResetDefaults,
    [switch]$Bootsel,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'

function Format-IdHex([string]$raw) {
    $clean = ($raw -replace '^0[xX]', '' -replace '[uU]$', '').Trim()
    return ('{0:x4}' -f [Convert]::ToUInt16($clean, 16))
}

$root = Split-Path $PSScriptRoot -Parent
$usbIdsPath = Join-Path $root 'include/usb_ids.h'
$vidHex = '0000'
$pidHex = '0000'
if (Test-Path -LiteralPath $usbIdsPath) {
    $idsText = Get-Content -Raw -LiteralPath $usbIdsPath
    if ($idsText -match '#define\s+USB_VID\s+(0x[0-9a-fA-F]+u?)') { $vidHex = Format-IdHex $matches[1] }
    if ($idsText -match '#define\s+MIXER_USB_PID\s+(0x[0-9a-fA-F]+u?)') { $pidHex = Format-IdHex $matches[1] }
}
if ($DevTestIds -and ($vidHex -eq '0000' -or $pidHex -eq '0000')) {
    $vidHex = 'cafe'
    $pidHex = '4026'
}
if ($UsbVid) { $vidHex = Format-IdHex $UsbVid }
if ($UsbPid) { $pidHex = Format-IdHex $UsbPid }

$csharpSource = @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public static class AudioPackHid {
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, string e, IntPtr p, uint f);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint n, ref IFACE i);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref IFACE i, IntPtr b, uint z, out uint needed, IntPtr d);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string p, uint a, uint share, IntPtr sec, uint mode, uint flags, IntPtr t);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int n);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int n);

    [StructLayout(LayoutKind.Sequential)]
    struct IFACE {
        public uint size;
        public Guid guid;
        public uint flags;
        public UIntPtr reserved;
    }

    public static byte[] Exchange(string vidHex, string pidHex, byte[] command, bool readBack) {
        Guid g;
        HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new IOException("HID enumeration failed");
        string needle = "vid_" + vidHex + "&pid_" + pidHex;
        SafeFileHandle chosen = null;
        try {
            for (uint n = 0; ; n++) {
                IFACE iface = new IFACE();
                iface.size = (uint)Marshal.SizeOf(typeof(IFACE));
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, n, ref iface)) break;
                uint size;
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out size, IntPtr.Zero);
                IntPtr b = Marshal.AllocHGlobal((int)size);
                try {
                    Marshal.WriteInt32(b, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, b, size, out size, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(IntPtr.Add(b, 4));
                    if (path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    SafeFileHandle h = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (h.IsInvalid) { h.Dispose(); continue; }
                    if (chosen != null) { h.Dispose(); throw new IOException("Multiple Audio Pack devices found"); }
                    chosen = h;
                } finally {
                    Marshal.FreeHGlobal(b);
                }
            }
            if (chosen == null) throw new IOException("Pico Audio Pack (VID_" + vidHex.ToUpperInvariant() + "&PID_" + pidHex.ToUpperInvariant() + ") not found. Connect USB to PC.");
            if (command != null) {
                byte[] send = new byte[64];
                int copyLen = Math.Min(command.Length, 63);
                Array.Copy(command, 0, send, 1, copyLen);
                if (!HidD_SetFeature(chosen, send, 64)) {
                    throw new IOException("HID SetFeature failed: " + Marshal.GetLastWin32Error());
                }
            }
            if (!readBack) return new byte[64];
            byte[] data = new byte[64];
            if (!HidD_GetFeature(chosen, data, data.Length)) {
                throw new IOException("HID GetFeature failed: " + Marshal.GetLastWin32Error());
            }
            if (Encoding.ASCII.GetString(data, 1, 4) != "UAP1" || data[5] != 1) {
                throw new IOException("Unexpected Audio Pack HID signature");
            }
            return data;
        } finally {
            if (chosen != null) chosen.Dispose();
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    public static byte[] ReadPage(string vidHex, string pidHex, int page) {
        byte[] data = Exchange(vidHex, pidHex, new byte[] { 0x10, (byte)page }, true);
        if (data[6] != page) throw new IOException("HID page mismatch");
        return data;
    }
}
'@

if (-not ('AudioPackHid' -as [type])) {
    Add-Type -TypeDefinition $csharpSource -Language CSharp
}

function New-BiquadFromSpec([string]$kind, [double]$fc, [double]$gdb, [double]$q, [string]$srcDesc) {
    if ($fc -lt 10.0 -or $fc -gt 23500.0 -or $q -le 0.05) {
        throw "Out-of-range filter parameter in $srcDesc"
    }
    $fs = 48000.0
    $A = [Math]::Pow(10.0, $gdb / 40.0)
    $w0 = 2.0 * [Math]::PI * $fc / $fs
    $cw = [Math]::Cos($w0)
    $sw = [Math]::Sin($w0)
    $alpha = $sw / (2.0 * $q)
    if ($kind -eq 'PK' -or $kind -eq 'PEQ') {
        $b0 = 1.0 + $alpha * $A; $b1 = -2.0 * $cw; $b2 = 1.0 - $alpha * $A
        $a0 = 1.0 + $alpha / $A; $a1 = -2.0 * $cw; $a2 = 1.0 - $alpha / $A
    } elseif ($kind -eq 'LSC' -or $kind -eq 'LS') {
        $sa = 2.0 * [Math]::Sqrt($A) * $alpha
        $b0 = $A * (($A + 1.0) - ($A - 1.0) * $cw + $sa)
        $b1 = 2.0 * $A * (($A - 1.0) - ($A + 1.0) * $cw)
        $b2 = $A * (($A + 1.0) - ($A - 1.0) * $cw - $sa)
        $a0 = ($A + 1.0) + ($A - 1.0) * $cw + $sa
        $a1 = -2.0 * (($A - 1.0) + ($A + 1.0) * $cw)
        $a2 = ($A + 1.0) + ($A - 1.0) * $cw - $sa
    } else {
        $sa = 2.0 * [Math]::Sqrt($A) * $alpha
        $b0 = $A * (($A + 1.0) + ($A - 1.0) * $cw + $sa)
        $b1 = -2.0 * $A * (($A - 1.0) + ($A + 1.0) * $cw)
        $b2 = $A * (($A + 1.0) + ($A - 1.0) * $cw - $sa)
        $a0 = ($A + 1.0) - ($A - 1.0) * $cw + $sa
        $a1 = 2.0 * (($A - 1.0) - ($A + 1.0) * $cw)
        $a2 = ($A + 1.0) - ($A - 1.0) * $cw - $sa
    }
    $nb0 = [single]($b0 / $a0); $nb1 = [single]($b1 / $a0); $nb2 = [single]($b2 / $a0)
    $na1 = [single]($a1 / $a0); $na2 = [single]($a2 / $a0)
    if ([Math]::Abs($na2) -ge 1.0 -or (1.0 + $na2) -le [Math]::Abs($na1)) {
        throw "Unstable biquad generated from $srcDesc"
    }
    return [single[]]@($nb0, $nb1, $nb2, $na1, $na2)
}

function Parse-PeaceFilters {
    param([string]$Path)
    if (!(Test-Path -LiteralPath $Path)) { throw "Peace file not found: $Path" }
    $lines = Get-Content -Encoding UTF8 -LiteralPath $Path
    $isIni = $Path.EndsWith('.peace', [StringComparison]::OrdinalIgnoreCase)
    foreach ($line in $lines) {
        if ($line -match '^\s*FilterCurve:') {
            throw 'This file is an Audacity FilterCurve export. Do not click Export in Peace; load your .peace preset or peace.txt directly from C:\Program Files\EqualizerAPO\config.'
        }
        if ($line -match '^\s*\[(Frequencies|General)\]\s*$') { $isIni = $true }
    }

    $preampDb = 0.0
    $specs = @()
    if ($isIni) {
        $sec = ''
        $freqs = @{}; $gains = @{}; $quals = @{}; $filts = @{}; $dis = @{}
        foreach ($line in $lines) {
            $t = $line.Trim()
            if (!$t -or $t.StartsWith(';') -or $t.StartsWith('#')) { continue }
            if ($t -match '^\[([^\]]+)\]$') { $sec = $matches[1].Trim(); continue }
            if ($sec -ieq 'General' -and $t -match '^PreAmp\s*=\s*([+-]?\d+(?:\.\d+)?)$') {
                $preampDb = [double]$matches[1]
            } elseif ($sec -ieq 'Frequencies' -and $t -match '^Frequency(\d+)\s*=\s*([0-9.]+)$') {
                $freqs[[int]$matches[1]] = [double]$matches[2]
            } elseif ($sec -ieq 'Gains' -and $t -match '^Gain(\d+)\s*=\s*([+-]?[0-9.]+)$') {
                $gains[[int]$matches[1]] = [double]$matches[2]
            } elseif ($sec -ieq 'Qualities' -and $t -match '^Quality(\d+)\s*=\s*([0-9.]+)$') {
                $quals[[int]$matches[1]] = [double]$matches[2]
            } elseif ($sec -ieq 'Filters' -and $t -match '^Filter(\d+)\s*=\s*([+-]?\d+)$') {
                $filts[[int]$matches[1]] = [int]$matches[2]
            } elseif ($sec -ieq 'Disabled' -and $t -match '^Disabled(\d+)\s*=\s*(\d+)$') {
                if ([int]$matches[2] -ne 0) { $dis[[int]$matches[1]] = $true }
            }
        }
        foreach ($slot in ($freqs.Keys | Sort-Object)) {
            $fc = $freqs[$slot]
            $gdb = if ($gains.ContainsKey($slot)) { $gains[$slot] } else { 0.0 }
            $fcode = if ($filts.ContainsKey($slot)) { $filts[$slot] } else { 0 }
            if ($dis.ContainsKey($slot) -or $fcode -lt 0) { continue }
            $kind = if ($fcode -eq 0) { 'PK' }
                elseif ($fcode -in @(4, 8, 14, 16)) { 'LSC' }
                elseif ($fcode -in @(5, 9, 15, 17)) { 'HSC' }
                elseif ([Math]::Abs($gdb) -lt 0.001) { continue }
                else { throw "Unsupported Peace filter code $fcode on slider $slot" }
            $defQ = if ($kind -eq 'PK') { 1.41 } else { 0.70710678 }
            $q = if ($quals.ContainsKey($slot)) { $quals[$slot] } else { $defQ }
            if (($fcode -eq 8 -or $fcode -eq 9) -and $q -gt 3.0) { $q = 0.70710678 }
            $specs += [PSCustomObject]@{ Kind = $kind; Fc = $fc; Gain = $gdb; Q = $q; Desc = "Slider $slot" }
        }
    } else {
        foreach ($line in $lines) {
            if ($line -match '^\s*Preamp:\s*([+-]?\d+(?:\.\d+)?)\s*dB') {
                $preampDb = [double]$matches[1]
            } elseif ($line -match '^\s*Filter\s+\d+:\s*ON\s+(PK|PEQ|LSC|LS|HSC|HS)\s+Fc\s+([0-9.]+)\s*Hz\s+Gain\s+([+-]?[0-9.]+)\s*dB(?:\s+Q\s+([0-9.]+))?') {
                $kind = $matches[1].ToUpperInvariant()
                $fc = [double]$matches[2]
                $gdb = [double]$matches[3]
                $defQ = if ($kind -eq 'PK' -or $kind -eq 'PEQ') { 1.41 } else { 0.70710678 }
                $q = if ($matches[4]) { [double]$matches[4] } else { $defQ }
                $specs += [PSCustomObject]@{ Kind = $kind; Fc = $fc; Gain = $gdb; Q = $q; Desc = $line.Trim() }
            }
        }
    }

    if ($specs.Count -eq 0) { throw "No active PK/LSC/HSC filters found in $Path" }
    if ($specs.Count -gt 11) {
        $nonZero = @($specs | Where-Object { [Math]::Abs($_.Gain) -gt 0.001 })
        $zeros   = @($specs | Where-Object { [Math]::Abs($_.Gain) -le 0.001 })
        if ($nonZero.Count -gt 11) { throw "Found $($nonZero.Count) non-zero filters, maximum supported is 11" }
        $specs = @($nonZero + $zeros) | Select-Object -First 11
    }

    $bands = @()
    foreach ($s in $specs) {
        $bands += ,(New-BiquadFromSpec $s.Kind $s.Fc $s.Gain $s.Q $s.Desc)
    }
    while ($bands.Count -lt 11) {
        $bands += ,([single[]]@(1.0, 0.0, 0.0, 0.0, 0.0))
    }
    return [ordered]@{
        PreampDb = $preampDb
        PreampLinear = [single][Math]::Pow(10.0, $preampDb / 20.0)
        Bands = $bands
    }
}

function Clamp-Int32([double]$val, [int]$minVal, [int]$maxVal) {
    $v = [int][Math]::Round($val)
    if ($v -lt $minVal) { return $minVal }
    if ($v -gt $maxVal) { return $maxVal }
    return $v
}

function Words-FromPage([byte[]]$raw) {
    $w = New-Object uint32[] 13
    for ($i = 0; $i -lt 13; $i++) {
        $w[$i] = [BitConverter]::ToUInt32($raw, 9 + 4 * $i)
    }
    return $w
}

function To-Signed32([uint32]$u) {
    return [BitConverter]::ToInt32([BitConverter]::GetBytes($u), 0)
}

if ($CheckOnly) {
    $samplePeace = Join-Path $root 'presets/sample-peace.txt'
    $parsed = if (Test-Path -LiteralPath $samplePeace) { Parse-PeaceFilters -Path $samplePeace } else { $null }
    [ordered]@{
        CheckOnlyPassed = ($null -ne ('AudioPackHid' -as [type])) -and ($null -ne $parsed)
        HidClassCompiled = ($null -ne ('AudioPackHid' -as [type]))
        SamplePeaceLoaded = ($null -ne $parsed)
        SamplePreampDb = if ($parsed) { $parsed.PreampDb } else { $null }
        SampleBandCount = if ($parsed) { $parsed.Bands.Count } else { 0 }
        TargetUsbVid = "0x$vidHex"
        TargetUsbPid = "0x$pidHex"
    } | ConvertTo-Json -Depth 3
    exit 0
}

if ($vidHex -eq '0000' -or $pidHex -eq '0000') {
    throw 'USB VID/PID in include/usb_ids.h is still 0x0000. Configure your official IDs in include/usb_ids.h, or pass -DevTestIds (or -UsbVid / -UsbPid) when testing development firmware.'
}

if ($Bootsel) {
    [void][AudioPackHid]::Exchange($vidHex, $pidHex, [byte[]]@(0x7f, 0x00), $false)
    Write-Host 'Sent BOOTSEL command. RPI-RP2350 drive should appear shortly.'
    exit 0
}

if ($ResetDefaults) {
    [void][AudioPackHid]::Exchange($vidHex, $pidHex, [byte[]]@(0x51, 0xa5), $false)
    Start-Sleep -Milliseconds 30
}

if ($TestTone -ne '') {
    $m = if ($TestTone -eq 'On') { [byte]1 } else { [byte]0 }
    [void][AudioPackHid]::Exchange($vidHex, $pidHex, [byte[]]@(0x20, $m), $false)
}

if ($PeaceFile -ne '') {
    $eqData = Parse-PeaceFilters -Path $PeaceFile
    for ($b = 0; $b -lt 11; $b++) {
        $cmd = New-Object byte[] 22
        $cmd[0] = 0x42
        $cmd[1] = [byte]$b
        for ($j = 0; $j -lt 5; $j++) {
            $fb = [BitConverter]::GetBytes([single]$eqData.Bands[$b][$j])
            [Array]::Copy($fb, 0, $cmd, 2 + $j * 4, 4)
        }
        [void][AudioPackHid]::Exchange($vidHex, $pidHex, $cmd, $false)
    }
    $pcmd = New-Object byte[] 5
    $pcmd[0] = 0x43
    [Array]::Copy([BitConverter]::GetBytes([single]$eqData.PreampLinear), 0, $pcmd, 1, 4)
    [void][AudioPackHid]::Exchange($vidHex, $pidHex, $pcmd, $false)
}

$needParamWrite = ($null -ne $MasterDb) -or ($null -ne $GameDb) -or ($null -ne $PcTrimDb) -or ($Eq -ne '') -or ($UartRxPin -ne '')
if ($needParamWrite) {
    $curRaw = [AudioPackHid]::ReadPage($vidHex, $pidHex, 3)
    $cw = Words-FromPage $curRaw

    $eqByte = if ($Eq -eq 'On') { [byte]1 } elseif ($Eq -eq 'Off') { [byte]0 } else { [byte]($cw[0] -band 1) }
    $gameX100 = if ($null -ne $GameDb) { Clamp-Int32 ($GameDb * 100.0) -6000 1200 } else { To-Signed32 $cw[1] }
    $masterX100 = if ($null -ne $MasterDb) { Clamp-Int32 ($MasterDb * 100.0) -6000 600 } else { To-Signed32 $cw[2] }
    $pcX100 = if ($null -ne $PcTrimDb) { Clamp-Int32 ($PcTrimDb * 100.0) -2400 1200 } else { To-Signed32 $cw[3] }
    $pinByte = if ($UartRxPin -eq 'Auto') { [byte]0 } elseif ($UartRxPin -eq '1') { [byte]1 } elseif ($UartRxPin -eq '13') { [byte]13 } else { [byte]$cw[8] }

    $cmd = New-Object byte[] 9
    $cmd[0] = 0x40
    $cmd[1] = $eqByte
    [Array]::Copy([BitConverter]::GetBytes([int16]$gameX100), 0, $cmd, 2, 2)
    [Array]::Copy([BitConverter]::GetBytes([int16]$masterX100), 0, $cmd, 4, 2)
    [Array]::Copy([BitConverter]::GetBytes([int16]$pcX100), 0, $cmd, 6, 2)
    $cmd[8] = $pinByte
    [void][AudioPackHid]::Exchange($vidHex, $pidHex, $cmd, $false)
}

if ($Save) {
    [void][AudioPackHid]::Exchange($vidHex, $pidHex, [byte[]]@(0x50, 0xa5), $false)
    Start-Sleep -Milliseconds 150
}

# Read back all 4 pages for status display
$r0 = [AudioPackHid]::ReadPage($vidHex, $pidHex, 0); $w0 = Words-FromPage $r0
$r1 = [AudioPackHid]::ReadPage($vidHex, $pidHex, 1); $w1 = Words-FromPage $r1
$r2 = [AudioPackHid]::ReadPage($vidHex, $pidHex, 2); $w2 = Words-FromPage $r2
$r3 = [AudioPackHid]::ReadPage($vidHex, $pidHex, 3); $w3 = Words-FromPage $r3

if ($PeaceFile -ne '' -and $w3[0] -eq 0) {
    Write-Warning 'Peace filter coefficients were loaded, but EqEnabled is False. Pass -Eq On (and -Save to persist) to enable the EQ.'
}

$flags = $r0[7]
[ordered]@{
    ObsMicActive = (($flags -band 1) -ne 0)
    PcSpeakerActive = (($flags -band 2) -ne 0)
    SwitchI2sActive = (($flags -band 4) -ne 0)
    PcI2sActive = (($flags -band 8) -ne 0)
    UartLocked = (($flags -band 16) -ne 0)
    TestToneOn = (($flags -band 32) -ne 0)
    FlashConfigLoaded = (($flags -band 64) -ne 0)
    ActiveUartRxPin = "GP$($r0[61])"
    UartRxPinMode = switch ($r0[63]) { 0 { 'Auto' } 1 { 'GP1' } 13 { 'GP13' } default { "$($r0[63])" } }
    UartPinSwitches = $w0[12]
    EqEnabled = ($w3[0] -ne 0)
    MasterDb = [Math]::Round((To-Signed32 $w3[2]) / 100.0, 2)
    GameDb = [Math]::Round((To-Signed32 $w3[1]) / 100.0, 2)
    PcTrimDb = [Math]::Round((To-Signed32 $w3[3]) / 100.0, 2)
    WindowsMasterVolDb = [Math]::Round((To-Signed32 $w3[4]) / 100.0, 2)
    WindowsMuted = (($w3[7] -band 1) -ne 0)
    FlashSaves = $w3[10]
    SelfTestFailureLine = $w0[0]
    Baud = $w0[1]
    UartAudioPackets = $w0[2]
    UartStatusPackets = $w0[3]
    CrcErrors = $w0[4]
    BadHeaders = $w0[5]
    UartErrors = $w0[6]
    SwI2sQueueFrames = $w2[0]
    SwI2sUnderflows = $w2[1]
    SwI2sOverflows = $w2[2]
    PcI2sQueueFrames = $w2[3]
    PcI2sUnderflows = $w2[4]
    PcI2sOverflows = $w2[5]
    ObsMicQueueFrames = $w1[2]
    ObsMicLowWater = $w1[4]
    ObsMicUnderflows = $w1[5]
    ObsMicOverflows = $w1[6]
    ObsMicMissingFrames = $w1[7]
    ObsMicRecoveries = $w1[12]
    ClipCount = $w2[8]
    MainLoopMaxUs = $w0[8]
    UsbIsrMaxUs = $w0[9]
    I2sIsrMaxUs = $w0[10]
} | ConvertTo-Json
