# 公式 USB PID 取得・公開ビルド手順（mugicha_xtx 用メモ）

本リポジトリは、テスト用の仮 USB ID（`0xCAFE`）のまま完成バイナリ（`.uf2`）をネット配布しないよう、初期状態の [`include/usb_ids.h`](../include/usb_ids.h) を **`0x0000`（未設定ガード）** にしています。

**GitHub で公開 → 無料の正規 USB PID を申請 → 正式版 `.uf2` をリリース** までの手順は以下のとおりです。

---

## 手順 1：メールアドレスを出さずに GitHub へソースを公開する

PID を申請するには、まずこのプロジェクトのソースコードがインターネット上（GitHub のパブリックリポジトリ）に公開されている必要があります。

1. **GitHub の匿名（noreply）メールアドレスを確認する**
   - GitHub にログイン → 右上アイコン → **Settings** → 左メニュー **Emails** を開く。
   - **「Keep my email addresses private」** にチェックを入れ、そこに表示されている **`数字+mugicha_xtx@users.noreply.github.com`** をメモする。
2. **この公開フォルダ（`public-release/`）の中身を新しい GitHub リポジトリへ Push する**
   - コミット時に個人のメールアドレスが入らないよう、対象フォルダ内で先ほど確認した `noreply` メールを設定します：
     ```powershell
     git init
     git config user.name "mugicha_xtx"
     git config user.email "119422185+mugicha-xtx@users.noreply.github.com"
     git add .
     git commit -m "Initial public source release (MIT)"
     ```
   - ⚠️ **この時点では `dist/*.uf2` バイナリは含めず、ソースコードとドキュメントだけを公開してください。**

---

## 手順 2：無料の正規 USB PID を 2 つ取得する（推奨：`raspberrypi/usb-pid`）

本プロジェクトは 2 台の **Raspberry Pi Pico 2 H（RP2350）** を使うため、Raspberry Pi 公式の無料 PID 割り当て窓口である **`raspberrypi/usb-pid`** を使うのが一番自然です（代替としてオープンソース向けの `pid.codes`（VID `0x1209`）もあります）。

### A. `raspberrypi/usb-pid` で申請する場合
1. GitHub の [raspberrypi/usb-pid](https://github.com/raspberrypi/usb-pid) リポジトリを開き、`README` に記載されている申請フォームのリンクを開きます。
2. `RP2350` を使用したオープンソースプロジェクトであること、公開したあなたの GitHub リポジトリ URL、および **送信側（`tx-bridge`）と PC 側（`rx-mixer`）の 2 つ分の PID が必要であること** を記入して申請します。
   （※ 先方の `README` の指示に従い、割り当て後の Pull Request 登録も忘れずに行ってください）

#### 申請フォームにそのまま使える英文説明テンプレート
```text
Project: Biwamasu Mixer / Switch2 Pico Audio Pack Bridge (Open-source MIT hardware/firmware project)
Repository: https://github.com/mugicha-xtx/switch2-pico-audio-pack-bridge
MCU: 2x Raspberry Pi Pico 2 H (RP2350, PICO_BOARD=pico2)

This project requires 2 USB Product IDs for two physically distinct RP2350 firmware images connected to separate USB hosts and bridged over 3 Mbps UART:
1. switch2-usb-bridge (tx-bridge): UAC 1.0 stereo 48kHz audio sink + Vendor HID telemetry interface (console side).
2. pico2h-audio-pack-uac2 (rx-mixer): UAC 2.0 composite headset (PC mix playback + bit-exact raw capture mic) + Vendor HID control interface for onboard 11-band EQ, gain settings, flash persistence, and firmware BOOTSEL trigger (PC side).
```

### B. もし `pid.codes`（VID `0x1209`）を使う場合
1. GitHub の [pidcodes/pidcodes.github.com](https://github.com/pidcodes/pidcodes.github.com) を開き、既存の `1209/` ディレクトリで使われていない 4 桁の 16 進数を 2 つ選びます。
2. ガイドに従い `org/mugicha-xtx/index.md` と `1209/<PID1>/index.md`、`1209/<PID2>/index.md` を追加する Pull Request を送ります（公開リポジトリ URL・MIT ライセンス・RP2350 ハードウェア構成のリンクを記載）。

---

## 手順 3：`include/usb_ids.h` に正規 ID を書き込んでビルド・配布する

PID が正式に割り当てられたら（例：`USB_VID = 0x2e8a`、`BRIDGE_USB_PID = 0xXXXX`、`MIXER_USB_PID = 0xYYYY`）、[`include/usb_ids.h`](../include/usb_ids.h) の 3 行だけを書き換えます：

```c
#ifndef USB_VID
#define USB_VID         0x2e8au
#endif

#ifndef BRIDGE_USB_PID
#define BRIDGE_USB_PID  0xXXXXu
#endif

#ifndef MIXER_USB_PID
#define MIXER_USB_PID   0xYYYYu
#endif
```

続けてビルドを実行します：

```powershell
# 初回のみ依存ツール（Pico SDK / TinyUSB / コンパイラ等）を .deps/ に取得
.\scripts\Setup-Toolchain.ps1

# 送信側・受信側の両ファームウェアをビルド＆自動検査
.\scripts\Build-Release.ps1
```

完了すると `dist/` フォルダ内に以下の 2 ファイルが生成されます：
- `dist/switch2-usb-bridge.uf2`（Switch 2 側 `Pico①` 用）
- `dist/pico2h-audio-pack-uac2.uf2`（PC 側 `Pico②` 用）

### GitHub Releases へ公開するときに必ず同梱するもの
`THIRD_PARTY_NOTICES.md` のライセンス条件を満たすため、2 つの `.uf2` ファイルと一緒に以下のファイルを必ず同梱（同じ Zip に入れる、またはリリース添付）してください：
- `LICENSE`
- `THIRD_PARTY_NOTICES.md`
- `licenses/` フォルダ内の全テキストファイル（4 ファイル）

---

## 補足：自分の手元の実機やローカル開発テストについて

PID 申請前のローカル環境だけでビルドや制御スクリプトの動作をテストしたい場合は、コマンドに `-DevTestIds` を付けて実行できます（※ 生成されたテスト用 `.uf2` は他人に配布しないでください）：

```powershell
# ローカル検証専用の仮 ID (0xCAFE) ビルド（公開配布禁止）
.\scripts\Build-Release.ps1 -DevTestIds

# すでに手元で 0xCAFE 版が動いている Pico② を操作する場合
.\scripts\Control-PicoAudioPack.ps1 -DevTestIds
```

後日、正規 ID 版の新しい `.uf2` を作ったら、手元の `Pico②` を `.\scripts\Control-PicoAudioPack.ps1 -DevTestIds -Bootsel` で再書き込みモードにして新しい `.uf2` に書き換えることで、自分用と公開版を完全一致させることができます（`Control-PicoAudioPack.ps1` は `include/usb_ids.h` に書かれた ID を自動参照するようになります）。
