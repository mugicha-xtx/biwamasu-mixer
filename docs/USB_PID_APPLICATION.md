# USB VID / PID 設定・ソースからのビルドガイド

本リポジトリは、ローカル検証用の仮 USB ID（`0xCAFE`）のままビルドしたバイナリ（`.uf2` / `.exe`）が誤って一般配布されないよう、初期状態の [`include/usb_ids.h`](../include/usb_ids.h) を **`0x0000`（未設定ガード）** にしています。

---

## 1. 正規リリース版のビルド手順（PID 割り当て後）

オープンソースハードウェア／ファームウェア向けの正規 USB VID / PID（`pid.codes`（VID `0x1209`）または `raspberrypi/usb-pid`（VID `0x2e8a`）等）を設定してビルドする場合は、[`include/usb_ids.h`](../include/usb_ids.h) の 3 か所を割り当てられた 16 進数 ID（送信側・受信側で別々の PID 2 個）に書き換えます：

```c
#ifndef USB_VID
#define USB_VID         0x1209u   /* 割り当て元の正規 VID (例: pid.codes = 0x1209u) */
#endif

#ifndef BRIDGE_USB_PID
#define BRIDGE_USB_PID  0xXXXXu   /* 送信側 tx-bridge (Pico①) 用の正規 PID */
#endif

#ifndef MIXER_USB_PID
#define MIXER_USB_PID   0xYYYYu   /* 受信側 rx-mixer (Pico②) 用の正規 PID */
#endif
```

続けて PowerShell で以下を実行します（システム環境変数やレジストリは変更せず、すべて `.deps/` フォルダ内にポータブル展開されます）：

```powershell
# 初回のみ依存ツール（Pico SDK 2.2.0 / TinyUSB / Arm GNU Toolchain 等）を .deps/ に取得・SHA-256検証
.\scripts\Setup-Toolchain.ps1

# 送信側・受信側の両ファームウェア＋Windows GUI をビルド＆自動検査し dist/ に出力
.\scripts\Build-Release.ps1
```

完了すると `dist/` フォルダ内に以下の成果物と SHA-256 ハッシュ一覧が出力されます：
- `dist/biwamasu-mixer-tx.uf2`（Switch 2 側 `Pico①` 用）
- `dist/biwamasu-mixer-rx.uf2`（PC 側 `Pico②` 用）
- `dist/Biwamasu-Mixer Setting tool.exe`（Windows 標準 GUI 設定ツール）
- `dist/biwamasu-mixer-release.zip`（GitHub Releases 配布用アーカイブ。`.uf2`・GUIソフト・操作スクリプト・設定見本・README・ハードウェア資料・配線画像・ライセンス一式を自動同梱）

### 再配布・GitHub Releases 公開時のライセンス同梱要件
`THIRD_PARTY_NOTICES.md` の条件を満たすため、コンパイル済みバイナリを配布する際は以下を必ず同梱してください（`dist/biwamasu-mixer-release.zip` にはすべて自動同梱されます）：
- `LICENSE`
- `THIRD_PARTY_NOTICES.md`
- `licenses/` フォルダ内の全ライセンス・著作権通知ファイル

---

## 2. 自分の手元の実機だけでローカル検証ビルドを行う場合（配布禁止）

正規 PID を持たない開発者が手元の実機だけでビルドや動作確認を行いたい場合は、`include/usb_ids.h` を書き換えずに `-DevTestIds` スイッチを付けて実行できます（※ 生成されたテスト用 `.uf2` / `.exe` / `biwamasu-mixer-dev-do-not-distribute.zip` は仮 ID `0xCAFE` を使用するため、外部へ公開・配布しないでください）：

```powershell
# ローカル検証専用の仮 ID (0xCAFE) ビルド（公開配布禁止）
.\scripts\Build-Release.ps1 -DevTestIds

# GUI 設定ツール単体を仮 ID でビルドする場合
.\scripts\Build-Gui.ps1 -DevTestIds

# 手元の 0xCAFE 版が動いている Pico② をコマンドから操作する場合
.\scripts\Control-BiwamasuMixer.ps1 -DevTestIds
```

手元の開発機（`0xCAFE` 版）から正規 ID 版の `.uf2` へ移行する際は、`.\scripts\Control-BiwamasuMixer.ps1 -DevTestIds -Bootsel`（または GUI の `ツール → BOOTSELモードへ切替`）を実行すると、基板のボタンを押さずに書き込みドライブを開くことができます。

---

## 3. 参考：2 つの USB PID が必要な技術的理由（審査・フォーク向け説明）

本プロジェクトは物理的に独立した 2 台の **Raspberry Pi Pico 2 H（`RP2350`, `PICO_BOARD=pico2`）** を別々の USB ホストに接続し、3 Mbps UART でブリッジする構成のため、機能が異なる 2 つの USB Product ID を使用します：

1. **`biwamasu-mixer-tx`（`firmware/tx-bridge`, `BRIDGE_USB_PID`）**
   - ゲーム機（Switch 2 ドック等）に接続する UAC 1.0 ステレオ 48 kHz スピーカー受信機 ＋ 診断/BOOTSEL 用 Vendor HID（`S2B4`）。
2. **`biwamasu-mixer-rx`（`firmware/rx-mixer`, `MIXER_USB_PID`）**
   - Windows PC に接続する UAC 2.0 複合ヘッドセット（PC ミックス再生用スピーカー OUT ＋ OBS 無加工録音用マイク IN）＋ 11 バンド EQ・音量・フラッシュ保存制御用 Vendor HID（`UAP1`）。
