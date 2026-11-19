# Hardware Design & Pinout Schematic

This project uses off-the-shelf open hardware modules wired on a standard solderless **MB-102** breadboard (no custom PCB is required).

![Hardware Schematic](schematic.svg)

---

## Bill of Materials (BOM)

| Ref | Part | Description |
|---|---|---|
| `U1` | **Raspberry Pi Pico 2 H** (`RP2350`) | Console-side USB Audio 1.0 receiver (`firmware/tx-bridge`) |
| `U2` | **Raspberry Pi Pico 2 H** (`RP2350`) | PC-side USB Audio 2.0 composite mixer & 11-band DSP (`firmware/rx-mixer`) |
| `U3` | **Pimoroni Pico Audio Pack** (`PIM544`) | `PCM5100A` I2S DAC + `PAM8908JER` headphone amp (`GAIN` switch = `LO GAIN`) |
| `R1` | **33 ohm resistor** (1/4W axial) | Series damping resistor on the 3 Mbps UART signal line |
| `W1..W9` | Jumper wires (9x) | Solderless interconnections on an `MB-102` breadboard |

---

## Netlist (Pin-to-Pin Wiring)

### 1. Inter-MCU 3 Mbps UART Link (`U1` Console <-> `U2` PC)

| Net Name | From (`U1` TX Bridge) | Through | To (`U2` RX Mixer) | Notes |
|---|---|---|---|---|
| `UART_TX_RX` | Physical Pin `1` (`GP0`) | **`R1` (`33Ω`)** | Physical Pin `17` (`GP13`) | Unidirectional 3,000,000 bps 8N1 DMA (`GP1` Pin `2` also auto-detected) |
| `GND_LINK` | Physical Pin `3` (`GND`) | Direct wire | Physical Pin `23` (`GND`) | Common logic reference |

> **Power Isolation Rule**: Never connect `VBUS` (`Pin 40`), `VSYS` (`Pin 39`), or `3V3(OUT)` (`Pin 36`) between `U1` and `U2`. Each Pico 2 H is powered solely by its own USB host.

### 2. I2S DAC & Amp Wiring (`U2` PC Mixer <-> `U3` Pimoroni Pico Audio Pack)

| Net Name | `U2` Pico 2 H Pin | `U3` Audio Pack Pin | Function |
|---|---|---|---|
| `VSYS_5V` | Physical Pin `39` (`VSYS`) | Physical Pin `39` (`VSYS`) | Amp 5V supply (do NOT connect `Pin 40 VBUS`) |
| `GND_AUDIO` | Physical Pin `38` (`GND`) | Physical Pin `38` (`GND`) | Ground |
| `VDD_3V3` | Physical Pin `36` (`3V3`) | Physical Pin `36` (`3V3`) | DAC 3.3V logic supply |
| `AMP_UNMUTE` | Physical Pin `29` (`GP22`) | Physical Pin `29` (`MUTE`) | Active-high amp unmute (muted during boot/flash save) |
| `I2S_DATA` | Physical Pin `12` (`GP9`) | Physical Pin `12` (`DATA`) | 32-bit stereo PCM @ 48 kHz (`64fs`) |
| `I2S_BCK` | Physical Pin `14` (`GP10`) | Physical Pin `14` (`BCK`) | 3.072 MHz bit clock |
| `I2S_LRCK` | Physical Pin `15` (`GP11`) | Physical Pin `15` (`LRCK`) | 48 kHz word select |

For step-by-step Japanese breadboard assembly instructions and wiring photos, see [README.md](../README.md).
