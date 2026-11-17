# Biwamasu Mixer (Switch2 Pico Audio Pack Bridge)
(以下AIによる説明)

**Biwamasu Mixer** は、**Raspberry Pi Pico 2 H（RP2350）× 2台 ＋ Pimoroni Pico Audio Pack** を使い、Nintendo Switch 2 の USB 音声（ヘッドホン バーチャルサラウンド ON 対応）を **約 5ms の超低遅延** で PC 音とデジタル合成して聴きながら、**OBS には無加工（EQ・減衰なし）の Switch 2 生音** を渡すオープンソースのオーディオブリッジです。

> [!NOTE]
> **公開ビルドについて / USB VID/PID ステータス**  
> 本リポジトリのソースコードは **MIT License** で公開されています。初期状態の `include/usb_ids.h` は未設定プレースホルダー（`0x0000`）となっており、テスト用の仮 ID（`0xCAFE`）が誤って配布用バイナリに混入しないようコンパイル時にブロックされます。  
> 正規のオープンソース USB PID 取得およびリリース用バイナリのビルド手順は [docs/USB_PID_APPLICATION.md](docs/USB_PID_APPLICATION.md) を参照してください。

---

## 主な特徴

1. **超低遅延（Switch 2 → ヘッドホン 約 4.6〜4.9 ms）**
   - Windows のソフトウェアミキサーを経由せず、`Pico①` → 3 Mbps UART（`33Ω` 抵抗経由）→ `Pico②` → I2S DAC（`PCM5100A` ＋ `PAM8908`）へ直接出力します。
2. **「耳用の精密 EQ・音量」と「OBS 用の生音」を完全分離**
   - **ヘッドホン側**：`Pico②` 内蔵 DSP で最大 11 バンドのパラメトリック EQ（[Equalizer APO / Peace](https://sourceforge.net/projects/peace-equalizer-apo-extension/) 形式ファイルを読み込み可能）と出力音量を適用します。配布設定の初期値は安全のため **EQ オフ・`-45.0 dB`** です。
   - **OBS（録音）側**：USB Audio Class 2.0 入力（等倍 0.0 dB）へ **Switch 2 から届いた無加工の 48kHz / 16bit / ステレオ生音** をそのまま送ります。
3. **PC 音（Discord・ブラウザ等）も同時ミックス**
   - `Pico②` は Windows 上で USB ヘッドホンとしても認識され、内蔵 4点エルミート補間リサンプラで Switch 2 とのクロック誤差を吸収して同時再生します。Windows の音量スライダーで PC 側の音量だけを調整できます。
4. **本体フラッシュメモリに設定を保存**
   - EQ 係数・音量設定は `Pico②` 本体のフラッシュに保存でき、一度設定すれば常駐ソフトなし・電源抜き差し後も自動で同じ状態で起動します。

---

## 必要なもの

| 品名 | 数量 | 用途・備考 |
|---|---|---|
| **Raspberry Pi Pico 2 H**（RP2350・ピンヘッダ実装済） | **2枚** | Switch 2 ドック側（`tx-bridge`）と PC 側（`rx-mixer`） |
| **Pimoroni Pico Audio Pack**（PIM544） | **1枚** | PCM5100A I2S DAC ＋ PAM8908 ヘッドホンアンプ |
| **ブレッドボード（MB-102 等）** | 1枚 | 無ハンダ配線用 |
| **ジャンパワイヤ（オス-オス）** | 9本 | 1枚ボード構成の場合（2箇所に置く場合は オス-オス 7本＋オス-メス 2本） |
| **抵抗（`33Ω` カーボン等、1/4W）** | **1本** | 3 Mbps UART 信号線のダンピング用 |
| USB ケーブル（PC 接続用・Switch 2 ドック接続用） | 各1本 | Micro-USB データ通信対応品 |

詳しい組み立て・配線・使い方は **[docs/GUIDE_JA.md](docs/GUIDE_JA.md)** をご覧ください。

---

## 免責事項・商標について（Disclaimer & Trademarks）

- 本プロジェクトは個人のオープンソース実験・制作物であり、**任天堂株式会社、Raspberry Pi Ltd、Pimoroni Ltd その他各社とは一切関係なく、公式製品・認証製品でもありません。**
- 記載されている製品名・会社名等は各社の商標または登録商標です。
- 配線ミス（特に `VBUS` 電源ピン同士の接続等）は接続機器の故障につながるおそれがあります。制作・使用はすべて自己責任で行ってください。
- 大音量による聴覚障害を防ぐため、初期音量は `-45.0 dB` に抑えてありますが、使用前に必ずヘッドホンアンプのゲインスイッチを **`Low`** にして音量を確かめてください。

---

## English Summary (For Open-Source Hardware & USB PID Reviewers)

**Biwamasu Mixer** (`switch2-pico-audio-pack-bridge`) is a dual-RP2350 open-source USB audio bridge and hardware mixer licensed under the **MIT License** (`Copyright (c) 2026 mugicha_xtx`).

### Architecture & Hardware
- **Hardware Platform & Schematic**: Two physically wired, non-wireless **Raspberry Pi Pico 2 H** (`RP2350`, `PICO_BOARD=pico2`, no `cyw43` wireless stack) paired with a **Pimoroni Pico Audio Pack** (`PCM5100A` I2S DAC + `PAM8908` headphone amp). Complete circuit diagram, BOM, and netlist are documented in **[hardware/README.md](hardware/README.md)** ([hardware/schematic.svg](hardware/schematic.svg)).
- **Inter-MCU Link**: Unidirectional 3 Mbps UART DMA link (`GP0` TX -> `33Ω` series resistor -> `GP13` RX, common `GND`, isolated power rails) carrying `S2AUD002` framed 48 kHz / 16-bit stereo PCM packets with IEEE CRC32 integrity checking.

### Two Separate USB Functions (Why Two USB PIDs Are Requested)
1. **`firmware/tx-bridge` (`BRIDGE_USB_PID`) — Console USB Host Port**
   - Enumerates as a **UAC 1.0 stereo 48 kHz speaker receiver + Vendor HID telemetry interface (`S2B4`)**.
   - Receives digital stereo PCM over USB from the console host and streams packets over UART without re-encoding.
2. **`firmware/rx-mixer` (`MIXER_USB_PID`) — PC USB Host Port**
   - Enumerates as a **UAC 2.0 composite Headset (Speaker OUT + Microphone IN) + Vendor HID control interface (`UAP1`)**.
   - **Speaker OUT**: Accepts 48 kHz stereo PC audio, resamples via 4-point Catmull-Rom Hermite interpolation, and mixes digitally with the UART console audio before 11-band floating-point biquad EQ and 32-bit PIO I2S output (`~5 ms` console-to-headphone latency).
   - **Microphone IN**: Streams bit-exact pre-EQ raw console PCM (`0.0 dB` max Feature Unit; Windows level `100` = bit-exact `0.0 dB` pass-through with no OS mic boost) for clean OBS recording.
   - **Vendor HID**: Allows the host script (`scripts/Control-PicoAudioPack.ps1`) to load EQ filters, set gains, save configuration to flash, read diagnostics, or enter BOOTSEL.

### Build & Dependencies
- Build configuration and dependency provenance are documented in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and reproduced portably via `scripts/Setup-Toolchain.ps1` and `scripts/Build-Release.ps1`.
- Project source uses `include/usb_ids.h` (`0x0000` placeholders with compile-time `#error`) until official open-source USB PIDs are assigned.
