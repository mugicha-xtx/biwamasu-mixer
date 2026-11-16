# SPDX-License-Identifier: MIT
# Copyright (c) 2026 mugicha_xtx
# Portable build toolchain setup under .deps/ only. No installers, registry edits, or system PATH changes.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    New-Item -ItemType Directory -Force .deps, dist | Out-Null
    $downloads = @(
        @('cmake.zip',     'https://github.com/Kitware/CMake/releases/download/v3.31.6/cmake-3.31.6-windows-x86_64.zip', '.deps'),
        @('ninja.zip',     'https://github.com/ninja-build/ninja/releases/download/v1.12.1/ninja-win.zip',                  '.deps/ninja'),
        @('arm.zip',       'https://developer.arm.com/-/media/Files/downloads/gnu/14.2.rel1/binrel/arm-gnu-toolchain-14.2.rel1-mingw-w64-x86_64-arm-none-eabi.zip', '.deps'),
        @('python.zip',    'https://www.python.org/ftp/python/3.13.2/python-3.13.2-embed-amd64.zip',                        '.deps/python'),
        @('picotool.zip',  'https://github.com/raspberrypi/pico-sdk-tools/releases/download/v2.2.0-4/picotool-2.2.0-a4-x64-win.zip', '.deps/picotool'),
        @('sdk-tools.zip', 'https://github.com/raspberrypi/pico-sdk-tools/releases/download/v2.2.0-4/pico-sdk-tools-2.2.0-x64-win.zip', '.deps/sdk-tools')
    )
    $lockPath = Join-Path $root 'tools/downloads.sha256.json'
    $lock = if (Test-Path -LiteralPath $lockPath) { Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json } else { $null }
    foreach ($d in $downloads) {
        $zip = Join-Path '.deps' $d[0]
        if (!(Test-Path -LiteralPath $zip)) {
            Write-Host "Downloading $($d[0])..."
            Invoke-WebRequest -UseBasicParsing $d[1] -OutFile $zip
        }
        if ($lock) {
            $expected = $lock.($d[0])
            if (!$expected -or (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expected) {
                throw "Checksum mismatch: $zip. Delete the incomplete file and retry."
            }
        }
        Expand-Archive -LiteralPath $zip -DestinationPath $d[2] -Force
    }

    $repos = @(
        @('pico-sdk', 'https://github.com/raspberrypi/pico-sdk.git', 'a1438dff1d38bd9c65dbd693f0e5db4b9ae91779'),
        @('tinyusb',  'https://github.com/hathach/tinyusb.git',       '8692556e335a0bf3b0a054a959efd2a3ee677cb6')
    )
    foreach ($r in $repos) {
        $dir = ".deps/$($r[0])"
        if (!(Test-Path -LiteralPath "$dir/.git")) {
            git init $dir
            if ($LASTEXITCODE) { throw "git init failed for $dir" }
            git -C $dir remote add origin $r[1]
        }
        $head = git -C $dir rev-parse --verify -q HEAD
        if ($head -ne $r[2]) {
            if (git -C $dir status --porcelain) { throw "Local dependency edits detected in $dir" }
            git -C $dir fetch --depth 1 origin $r[2]
            if ($LASTEXITCODE) { throw "Dependency fetch failed for $dir" }
            git -C $dir checkout --detach $r[2]
            if ($LASTEXITCODE) { throw "Dependency checkout failed for $dir" }
        }
    }

    Write-Host 'Toolchain ready. Configure include/usb_ids.h, then run: .\scripts\Build-Release.ps1'
} finally {
    Pop-Location
}
