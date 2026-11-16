# Third-Party Notices and Redistribution Terms

Project-owned source code, scripts, presets, and documentation (`firmware/`, `include/`, `presets/`, `scripts/`, `tests/`, `docs/`) are licensed under the **MIT License** (`Copyright (c) 2026 mugicha_xtx`; see `LICENSE`).

Third-party libraries and toolchains retain their own licenses and copyright notices. When redistributing compiled `.uf2` firmware binaries (for example via GitHub Releases), include `LICENSE`, `THIRD_PARTY_NOTICES.md`, and the `licenses/` directory alongside the binaries.

## Firmware & Build Dependencies

| Component | Pinned Version / Commit | Role | License & Notice File |
|---|---|---|---|
| **Raspberry Pi Pico SDK** | `2.2.0` (`a1438dff1d38bd9c65dbd693f0e5db4b9ae91779`) | RP2350 runtime, GPIO/UART/PIO/DMA/Flash HAL (`PICO_BOARD=pico2`) | **BSD-3-Clause** — `licenses/pico-sdk-BSD-3-Clause.txt` (individual SDK file notices also apply) |
| **ARM CMSIS Headers** (via Pico SDK) | Bundled with Pico SDK `2.2.0` | Cortex-M33 intrinsics / core headers | **Apache-2.0** — `licenses/cmsis-Apache-2.0.txt` |
| **pico_printf** (via Pico SDK) | Bundled with Pico SDK `2.2.0` | Minimal printf support if referenced by SDK runtime | **MIT** (`Copyright (c) 2014 Marco Paland`) — `licenses/pico-printf-MIT.txt` |
| **TinyUSB** | `8692556e335a0bf3b0a054a959efd2a3ee677cb6` | UAC1 / UAC2 / HID USB device stack and descriptor macros | **MIT** (`Copyright (c) 2012-2026, Ha Thach`) — `licenses/tinyusb-MIT.txt` |
| **Arm GNU Toolchain** | `14.2.Rel1` (GCC `14.2.1`) | Cross-compiler and linked compiler/C runtime (`libgcc`, `newlib`) | **GPL with GCC Runtime Library Exception** / newlib upstream terms; fetched locally by `scripts/Setup-Toolchain.ps1`, not vendored as source |
| **CMake / Ninja / Python / picotool / pioasm** | `3.31.6` / `1.12.1` / `3.13.2` / `2.2.0-a4` / `2.2.0` | Local build & UF2 packaging tools | Upstream open-source licenses; downloaded locally into ignored `.deps/`, not vendored or relicensed |

### Notes & Attributions
- **TinyUSB UAC Reference**: USB Audio topology and entity callback structure consulted TinyUSB's UAC1/UAC2 speaker & headset examples (`Copyright (c) 2023 HiFiPhile; Copyright (c) 2020 Jerzy Kasenberg`, MIT License — same permission and warranty terms as `licenses/tinyusb-MIT.txt`).
- **Wireless / Bluetooth Exclusion**: Neither `firmware/tx-bridge` nor `firmware/rx-mixer` links `cyw43-driver`, `pico_btstack`, or `lwIP`. Both binaries target the wired `Raspberry Pi Pico 2 H` (`PICO_BOARD=pico2`).
- **USB Identifiers**: `include/usb_ids.h` ships with `0x0000` placeholders and a compile-time `#error` guard so unauthorized test IDs (`0xCAFE`) are never built into public releases by accident. Public `.uf2` binaries must be built with authorized open-source VID/PID values (e.g., allocated via `raspberrypi/usb-pid` or `pid.codes`).
- **Non-Affiliation**: This is an independent community open-source hardware/firmware project and is not affiliated with or endorsed by Nintendo Co., Ltd., Sony Group Corporation, Raspberry Pi Ltd, or Pimoroni Ltd.
