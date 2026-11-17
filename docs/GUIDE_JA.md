# 組み立て・使い方ガイド（Pico Audio Pack 版）

Switch 2（ヘッドホン バーチャルサラウンド ON）と PC 音（Discord・ブラウザ等）を、Windows のミキサーを通さずに **PC 側 Pico② 内部でデジタル合成 → Pimoroni Pico Audio Pack へ直接再生（Switch 2 音声遅延 約 4.6〜4.9 ms）** しつつ、**OBS には無加工の生音** を送る手順です。

---

## 1. 配布ビルドの初期設定（安全仕様）

公開版のファームウェアでは、どんなヘッドホンでも安心して使い始められるよう初期状態が以下のように設定されています：

- **初期 EQ 状態**：**OFF（フラット）**  
  （※ 全バンド `0 dB` の記入見本ファイルを [presets/sample-peace.txt](../presets/sample-peace.txt) に同梱しています。お好みの数値に書き換えるか、ご自身の Peace ファイルを指定して適用できます）
- **初期マスター音量**：**`-40.0 dB`**（高感度なヘッドホン・イヤホンでも突然大音量にならない控えめ設定）
- **Windows 上での表示名**：
  - 再生デバイス（PC音を混ぜる出力先）：`ヘッドホン (Switch2 Pico Audio Pack Bridge)`
  - 録音デバイス（OBS に生音を入れる入力元）：`マイク (Switch2 Pico Audio Pack Bridge)`

---

## 2. ファームウェアの書き込み（まず単体で書き込む）

ブレッドボードに挿す前に、PC へ 1 台ずつ繋いで `.uf2` ファイルを書き込みます。

1. `Pimoroni Pico Audio Pack` 基板上にあるスライドスイッチ（`GAIN`）が **`Low`** 側になっていることを確認する。
2. **Switch 2 側にする `Pico 2 H`（以降 `Pico①`）** の `BOOTSEL` ボタンを押しながら PC に USB 接続し、現れた `RP2350` ドライブへ **`switch2-usb-bridge.uf2`** をコピーする（自動再起動したら USB を抜く）。
3. **PC 側にする `Pico 2 H`（以降 `Pico②`）** も同じように `BOOTSEL` ボタンを押しながら PC に接続し、**`pico2h-audio-pack-uac2.uf2`** をコピーする（自動再起動したら USB を抜く）。

（※ 自分でビルドする場合は [USB_PID_APPLICATION.md](USB_PID_APPLICATION.md) や `scripts/Setup-Toolchain.ps1` → `scripts/Build-Release.ps1` を参照してください）

---

## 3. MB-102 ブレッドボード配線手順（必ず電源 OFF で作業）

配線中のショートを防ぐため、**両方の USB ケーブルを完全に抜いた状態**で行います。

### ボードの番号の見方（重要）
`MB-102` ブレッドボードはメーカーによって「手前が `A〜E`・奥が `F〜J`」のものと「奥が `A〜E`・手前が `F〜J`」のものがあり、左端の数字も `0` 始まりと `1` 始まりがあります。  
アルファベットよりも **「USB 端子を向けた側の端の足から何番目か」** と **「手前（下側）か奥（上側）か」** で数えると間違いが起きません。

※ ボード上下端の赤線（`+`）・青線（`-`）の細長い電源レーンは今回は使いません。

---

### Step A：`Pico②` と `Pico Audio Pack` を繋ぐ（オス-オス 7本）

1. `Pico②` をボードの **左端側** に、**USB 端子を「左外向き」・白い BOOTSEL ボタン面を「上」** にして、中央の溝をまたぐようにしっかり奥まで挿し込みます（手前に空き穴が 2 列、奥にも空き穴が 2 列残る位置に挿します）。
2. `Pico Audio Pack` は **黒いピンソケット穴を上・USB 印字側を左（3.5mm ジャック側を右）** にして隣に置きます。
3. **`Pico②` の一番左端の足を「1番目」** と数え、同じ位置同士をオス-オス 7 本で繋ぎます：

| 機能 | `Pico②` の足（左端から何番目か） | 繋ぐ側 | `Pico Audio Pack` 側の穴 |
|---|---|---|---|
| **電源 `VSYS` (5V)** | 奥列・左から **2番目** | 奥 ↔ 奥 | 奥列・左から **2番目**（`VSYS`） |
| **`GND`** | 奥列・左から **3番目** | 奥 ↔ 奥 | 奥列・左から **3番目**（`GND`） |
| **DAC電源 `3V3` (3.3V)** | 奥列・左から **5番目** | 奥 ↔ 奥 | 奥列・左から **5番目**（`3V3`） |
| **ミュート解除 `GP22`** | 奥列・左から **12番目** | 奥 ↔ 奥 | 奥列・左から **12番目**（`MUTE`） |
| **音声データ `GP9`** | 手前列・左から **12番目** | 手前 ↔ 手前 | 手前列・左から **12番目**（`DATA`） |
| **ビットクロック `GP10`** | 手前列・左から **14番目** | 手前 ↔ 手前 | 手前列・左から **14番目**（`BCK`） |
| **左右クロック `GP11`** | 手前列・左から **15番目** | 手前 ↔ 手前 | 手前列・左から **15番目**（`LRCK`） |

> ⚠️ **注意**：奥列・左から **1番目（一番左上角）の `VBUS`** には何も繋がないでください。

---

### Step B：`Pico①`（Switch 2 側）と `Pico②` を繋ぐ

1枚のブレッドボードに両方まとめる場合は、`Pico①` をボード **右端** に **`USB` 端子を「右外向き」・白い BOOTSEL ボタン面を「上」** にして挿します（`USB` が左向きと右向きに分かれるためケーブル同士が干渉しません）。

- **`GND` 同士（オス-オス 1本）**：
  - `Pico②` **手前列・左から 18番目**（右端の足から左へ 3本目の `GND`） ⇔ `Pico①` **奥列・右端から左へ 3本目（`GND`）**
- **音声信号 ＋ `33Ω` 抵抗（オス-オス 1本 ＋ 抵抗 1本）**：
  - `Pico①` **奥列・一番右端の足（`GP0`）** ⇔ ブレッドボードの使っていない空き縦列の穴
  - **`33Ω` 抵抗**（向き不問）の両足を折り曲げて、**その空き縦列の別の穴** ⇔ **`Pico②` 手前列・左から 17番目**（右端の足から左へ 4本目の **`GP13`**）

※ もし 2 枚の距離が遠くて `オス-メス` ワイヤを使う場合は、`Pico①` 裏面の白い印字を見て `GP0` と `GND` のピンから同じ場所へ繋いでください。

> ⛔ **絶対禁止事項**  
> `Pico①` と `Pico②` の間で **`VBUS`・`VSYS`・`3V3` などの電源ピン同士は絶対に繋がないでください。** また、`Pico①` の手前一番右端の足は **`VBUS`（5V 電源）** なので、`GP0`（奥側の一番右端）と間違えないよう必ず裏面の印字を確認してから配線してください。

---

## 4. 通電・初回チェック

1. `Pico Audio Pack` の **`HEADPHONE`** ジャック（`LINE OUT` ではない方）にヘッドホンを挿す。  
   （※ 新品ジャックは硬いため、**プラグが根元まで完全に奥へ刺さっていないと「左耳しか鳴らない」状態になります**。最後までしっかり押し込んでください）
2. まず **`Pico②` の USB だけ** を Windows PC に繋ぐ。
3. PowerShell でテストトーン（左 440 Hz / 右 660 Hz）を鳴らして左右確認：
   ```powershell
   .\scripts\Control-PicoAudioPack.ps1 -TestTone On
   # 左右から音が聞こえたら停止
   .\scripts\Control-PicoAudioPack.ps1 -TestTone Off
   ```
4. **`Pico①` の USB** を Switch 2 ドックに接続し、Switch 2 本体の電源を入れる。
   - Switch 2 本体の設定 →「オーディオ」→ **USB 出力音量（最大推奨）**、**ヘッドホンの音声出力「バーチャルサラウンド」ON** にします。
5. PowerShell でステータスを確認：
   ```powershell
   .\scripts\Control-PicoAudioPack.ps1
   ```
   `UartLocked: true`、`ActiveUartRxPin: "GP13"`、`CrcErrors: 0` になっていればデジタル通信は完璧です。

---

## 5. Windows と OBS の設定

### Windows の再生（PC 音も一緒に聴く）
- Windows 右下のスピーカーアイコン → 出力先を **`ヘッドホン (Switch2 Pico Audio Pack Bridge)`** に切り替えます。
- Windows の音量スライダーを動かすと **PC 側の音量だけ** 変わり、Switch 2 の音量には影響しません。
- ※ ヘッドホン用 EQ は `Pico②` 内部で行うため、Windows の **Equalizer APO / Peace はこの出力デバイスに掛けないでください**。

### OBS の録音（Switch 2 生音を取り込む）
- OBS「ソース」→ `＋` →「音声入力キャプチャ」→ デバイスに **`マイク (Switch2 Pico Audio Pack Bridge)`** を選択します（映像側と音がわずかにズレる場合は OBS の「オーディオの詳細プロパティ」→「同期オフセット」で映像に合わせて微調整してください）。
- **推奨 Windows マイク設定**：Windows「設定」→「システム」→「サウンド」→「入力」→ `マイク (Switch2 Pico Audio Pack Bridge)` を開き、**入力音量を `100`（等倍 0.0 dB）**、**「オーディオの強化」を「オフ」** にしておくと、Switch 2 から来たデジタル音声が 1bit も加工されず OBS に届きます。

---

## 6. 音量の調整・EQ の適用・本体メモリへの保存

すべて [scripts/Control-PicoAudioPack.ps1](../scripts/Control-PicoAudioPack.ps1) から実行できます。

- **全体音量（`-MasterDb`）の調整と本体保存**：
  ```powershell
  # 例：全体音量を -35 dB にして、USB を抜いても維持されるよう Pico② 本体に保存する
  .\scripts\Control-PicoAudioPack.ps1 -MasterDb -35 -Save
  ```
- **Peace 形式 EQ ファイルの読み込み・有効化・保存（最大 11 バンドまで）**：
  ```powershell
  # 見本ファイル（presets\sample-peace.txt）や自作の Peace 形式 EQ ファイルを読み込んで ON にし、本体へ保存する
  .\scripts\Control-PicoAudioPack.ps1 -PeaceFile .\presets\sample-peace.txt -Eq On -Save
  ```
  （※ 同梱の `sample-peace.txt` は見本のためすべて `0 dB` になっています。お好みの値に編集するか、ご自身のヘッドホン用 `peace.txt` を `-PeaceFile` で指定してください。EQ を切りたいときは `-Eq Off -Save` を実行してください）
- **Switch 2 音と PC 音の音量バランス調整**：
  ```powershell
  # 例：Switch 2 側を少し下げ（-6 dB）、PC 音を基準より控えめに（-3 dB）して保存
  .\scripts\Control-PicoAudioPack.ps1 -GameDb -6 -PcTrimDb -3 -Save
  ```
- **設定を工場出荷状態（EQ OFF / `-40.0 dB`）に戻す**：
  ```powershell
  .\scripts\Control-PicoAudioPack.ps1 -ResetDefaults -Save
  ```
- **再書き込み用に BOOTSEL モードへ入れる**：
  ```powershell
  .\scripts\Control-PicoAudioPack.ps1 -Bootsel
  ```
