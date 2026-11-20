# Third-Party Notices and Redistribution Terms

Project-owned source code, hardware design/schematics, wiring photos, scripts, presets, and documentation (`firmware/`, `include/`, `host-gui/`, `hardware/`, `images/`, `presets/`, `scripts/`, `tests/`, `docs/`) are licensed under the **MIT License** (`Copyright (c) 2026 mugicha_xtx`; see `LICENSE`).

Third-party libraries and toolchains retain their own licenses and copyright notices. When redistributing compiled `.uf2` firmware or `.exe` GUI binaries (for example via GitHub Releases), include `LICENSE`, `THIRD_PARTY_NOTICES.md`, and the `licenses/` directory alongside the binaries.

## Firmware & Build Dependencies

| Component | Pinned Version / Commit | Role | License & Notice File |
|---|---|---|---|
| **Raspberry Pi Pico SDK** | `2.2.0` (`a1438dff1d38bd9c65dbd693f0e5db4b9ae91779`) | RP2350 runtime, GPIO/UART/PIO/DMA/Flash HAL (`PICO_BOARD=pico2`) | **BSD-3-Clause** — `licenses/pico-sdk-BSD-3-Clause.txt` (individual SDK file notices also apply) |
| **ARM CMSIS Headers** (via Pico SDK) | Bundled with Pico SDK `2.2.0` | Cortex-M33 intrinsics / core headers | **Apache-2.0** — `licenses/cmsis-Apache-2.0.txt` |
| **pico_printf** (via Pico SDK) | Bundled with Pico SDK `2.2.0` | Minimal printf support if referenced by SDK runtime | **MIT** (`Copyright (c) 2014 Marco Paland`) — `licenses/pico-printf-MIT.txt` |
| **TinyUSB** | `8692556e335a0bf3b0a054a959efd2a3ee677cb6` | UAC1 / UAC2 / HID USB device stack, RP2040/RP2350 DCD driver, and descriptor macros | **MIT** (`Copyright (c) 2012-2026 Ha Thach; 2020 Reinhard Panhuber, Jerzy Kasenberg; 2023-2026 HiFiPhile (Zixun LI); 2026 Zhenjiang Zhang; 2020 Raspberry Pi (Trading) Ltd.`) — `licenses/tinyusb-MIT.txt` |
| **Microsoft Fluent UI Emoji** | `microsoft/fluentui-emoji` (`assets/Fish/Color/fish_color.svg`, `U+1F41F` Fish) | Vector geometry rendered at build time into `Biwamasu-Mixer Setting tool.exe` icon | **MIT** (`Copyright (c) Microsoft Corporation`) — `licenses/fluentui-emoji-MIT.txt` |
| **Arm GNU Toolchain (`libgcc` + `newlib`)** | `14.2.Rel1` (GCC `14.2.1`) | Cross-compiler and linked compiler/C runtime (`libgcc`, `newlib`) | **`libgcc`: GPL-3.0-or-later WITH GCC-exception-3.1**; **`newlib`: BSD / permissive notices** — `licenses/newlib-COPYING.txt` (fetched locally by `scripts/Setup-Toolchain.ps1`, not vendored as source) |
| **CMake / Ninja / Python / picotool / pioasm** | `3.31.6` / `1.12.1` / `3.13.2` / `2.2.0-a4` / `2.2.0` | Local build & UF2 packaging tools | Upstream open-source licenses; downloaded locally into ignored `.deps/`, not vendored or relicensed |

### Notes & Attributions
- **TinyUSB Audio & RP2040/RP2350 Modules**: Compiled TinyUSB audio class and device controller sources carry MIT notices by Ha Thach, Reinhard Panhuber, Jerzy Kasenberg, HiFiPhile (Zixun LI), Zhenjiang Zhang, and Raspberry Pi (Trading) Ltd., all included in `licenses/tinyusb-MIT.txt`.
- **Wireless / Bluetooth Exclusion**: Neither `firmware/tx-bridge` nor `firmware/rx-mixer` links `cyw43-driver`, `pico_btstack`, or `lwIP`. Both binaries target the wired `Raspberry Pi Pico 2 H` (`PICO_BOARD=pico2`).
- **USB Identifiers**: `include/usb_ids.h` ships with `0x0000` placeholders and a compile-time `#error` guard so unauthorized test IDs (`0xCAFE`) are never built into public releases by accident. Public `.uf2` binaries must be built with authorized open-source VID/PID values (e.g., allocated via `raspberrypi/usb-pid` or `pid.codes`).
- **Non-Affiliation**: This is an independent community open-source hardware/firmware project and is not affiliated with or endorsed by Nintendo Co., Ltd., Raspberry Pi Ltd, or Pimoroni Ltd.
