# メディアビューワー（MediaViewer）

画像・動画・音声を、同じ画面・同じ操作で扱う Windows 向けの軽量メディアビューワーです。
画像の色管理（埋め込みICC・広色域・HDR／自動色管理）を最重要の品質要件としています。

- 開発者：三玉春樹 / はるたまプロジェクト
- アカウント：https://x.com/MitamaHaruki
- 対象OS：Windows 11 24H2 以降（x64）
- ライセンス：GNU General Public License v3.0 以降（[LICENSE](LICENSE)）。Windows App SDK とのリンクについて GPLv3 第7条の追加許可あり（[LICENSE-ADDITIONAL-PERMISSION.txt](LICENSE-ADDITIONAL-PERMISSION.txt)）

> 現在は開発中です（初回の公開前）。

## インストール

1. [Releases](https://github.com/MitamaHaruki/MediaViewer/releases/latest) から `MediaViewer-x.y.z-x64.msi` をダウンロードして実行します。
2. 全ユーザー共通で `C:\Program Files\MediaViewer\` にインストールされます（インストール先は変更できます）。管理者権限の確認（ユーザーアカウント制御）が表示されます。
3. スタートメニューの「メディアビューワー」から起動します。対応するファイルの「プログラムから開く」にも追加されます（既定のアプリは変更しません。Windows の設定 → アプリ → 既定のアプリ で選べます）。

コード署名証明書を使っていないため、ダウンロードや初回の実行時に Microsoft Defender SmartScreen の警告が表示されることがあります。その場合は「詳細情報」→「実行」を選んでください。

アンインストールは Windows の設定 → アプリ → インストールされているアプリ から行います。設定ファイル（`%LocalAppData%\MediaViewer`）は残ります。

## 更新

- 起動後しばらくしてから、GitHub Releases の最新の安定版を確認します（前回の確認から約24時間は確認しません。「ヘルプ → 更新を確認」ではすぐに確認します）。
- 新しい版があれば自動でダウンロードし、ファイルのサイズ・SHA-256・署名（ECDSA P-256、公開鍵はアプリに埋め込み）を確認します。確認できないファイルは使いません。
- 既定では、アプリを終了するときに更新します（ほかのウィンドウが開いている間は更新しません）。「今すぐ再起動して更新」や、終了時に確認する設定も選べます（設定 → 更新設定）。
- 更新は別のプログラム（`MediaViewer.Updater.exe`）が行い、インストーラーの実行時に管理者権限の確認が表示されます。更新後に新しい版が正常に起動するかを確かめ、起動しなければ直前の版に戻します。途中で失敗した場合は Windows Installer が元の状態に戻します。
- 更新しても設定は消えません。通信できない環境や GitHub の利用制限中でも、更新以外の機能はそのまま使えます。

## 主な機能（現時点）

| 機能 | 状態 |
|---|---|
| 画像表示（PNG / JPEG / BMP / GIF / WebP / TIFF、WIC でデコード） | 実装済み（複数フレームは最初のフレームのみ） |
| 埋め込みICCによる色管理（SDR はモニターICCへ、HDR／自動色管理は scRGB で出力し二重変換を防止） | 実装済み |
| CMYK・グレーICC、PNG の cICP / gAMA / cHRM、EXIF の Adobe RGB 指定 | 実装済み |
| HDR 画像（PNG の PQ / BT.2020）のトーンマップ表示 | 実装済み |
| GPU のリセット・ドライバー更新からの自動復旧 | 実装済み |
| EXIF の向き、透過（アルファ） | 実装済み |
| 動画・音声の再生（FFmpeg によるデコード、読込み時に自動再生、終端で停止） | 実装済み |
| 複数音声トラックの切替（読込み時はファイル内で最初のトラック） | 実装済み |
| 音声のアルバムアート表示（なければ音符アイコン） | 実装済み |
| 画像・動画で共通の拡大縮小・移動（ホイール・ドラッグ・ピンチ・ダブルクリック） | 実装済み（タッチは実機確認待ち） |
| 同じフォルダ内の前後移動・種類フィルター | 実装済み |
| 仮想フルスクリーン（操作バーの自動非表示） | 実装済み |
| 設定の保存（音量・ミュート・フィルター・シーク幅・ウィンドウ位置）、一般設定画面 | 実装済み |
| 形式変換（画像：PNG／JPEG／BMP／GIF／WebP／TIFF、動画：MP4／MKV／MOV／AVI／WebM、音声：MP3／WAV／FLAC／M4A／OGG） | 実装済み（必要な機能だけの FFmpeg を同梱） |
| 自動更新（GitHub Releases、署名の確認、終了時の適用、起動確認とロールバック） | 実装済み |
| インストーラー（MSI、スタートメニュー、ファイルの関連付け） | 実装済み |

設定は `%LocalAppData%\MediaViewer\settings.json` に保存されます（インストール先とは別の場所のため、更新しても消えません）。

## 操作方法

| 操作 | マウス・キーボード | タッチ |
|---|---|---|
| 拡大・縮小 | ホイール | ピンチ |
| 移動（拡大中） | 左ドラッグ | 1本指ドラッグ |
| フィット ↔ 等倍 | ダブルクリック | ダブルタップ |
| 倍率リセット | `Ctrl+0`／フィットボタン | フィットボタン |
| 前／次のファイル | `PageUp`／`PageDown`、画像表示中は `←`／`→` | 横スワイプ（フィット表示時） |
| 再生／一時停止 | `Space` | 再生ボタン |
| シーク（±5秒） | `←`／`→`（動画・音声） | シークバー |
| 音量・ミュート | `↑`／`↓`、`M` | 音量スライダー |
| フルスクリーン | `F11`（解除は `Esc` も可） | フルスクリーンボタン |
| ファイルを開く | `Ctrl+O`、ドラッグ＆ドロップ | — |

## ビルド方法

必要なもの：Windows 11、.NET SDK 10.0 以降。

```bash
dotnet build MediaViewer.slnx -c Debug
```

```bash
dotnet test tests/MediaViewer.Tests/MediaViewer.Tests.csproj
```

動画・音声の変換と WebP の保存には FFmpeg（ffmpeg.exe・ffprobe.exe）が必要です。開発中は次のいずれかに置きます：アプリと同じ場所の `ffmpeg` フォルダ、環境変数 `MEDIAVIEWER_FFMPEG_DIR` のフォルダ、またはリポジトリの `tools/ffmpeg-dl`（Git 管理外）。FFmpeg がない環境では、変換の結合テストは結論保留になります。

配布用の発行（アプリ本体は自己完結・トリミング・ReadyToRun、更新プログラムは Native AOT。同梱する FFmpeg のフォルダを指定）：

```powershell
./build/publish.ps1 -Version 0.1.0 -Output artifacts/publish -FfmpegDir <ffmpeg.exe のあるフォルダ>
```

インストーラー（MSI）の作成（WiX Toolset v5）：

```bash
dotnet build installer/MediaViewer.Installer.wixproj -c Release -p:Version=0.1.0 -p:PublishDir=artifacts/publish/
```

### リリースの手順（開発者向け）

1. `Directory.Build.props` の `Version` を更新してコミットし、`main` へ push します。
2. 変更内容を書いた注釈付きタグを作って push します（例：`git tag -a v1.2.3 -m "変更内容..."`、`git push origin v1.2.3`）。
3. GitHub Actions の「リリース」が、テスト・発行・起動確認・MSI の作成・署名を行い、安定版として公開します。タグの説明文がリリースノートになります。

更新ファイルの署名に使う秘密鍵は、リポジトリの Secrets（`UPDATE_SIGNING_KEY`）に登録しています。対になる公開鍵は `src/MediaViewer.Core/Updates/UpdateConfiguration.cs` にあります（古い版のアプリは別の鍵で署名した更新を受け付けないため、鍵は作り直さない前提です）。

同梱する FFmpeg は「FFmpeg ビルド」のワークフロー（`build/ffmpeg/build.sh`、MSYS2）で作り、プレリリースとして公開しています。

## 構成

```text
MediaViewer.slnx
├─ src/MediaViewer.App       WinUI 3 の画面・入力・再生制御
├─ src/MediaViewer.Core      UI非依存のロジック（表示倍率・前後移動・設定・変換・更新・ログ）
├─ src/MediaViewer.Imaging   画像デコード（WIC）・ICC色変換（Direct2D）・scRGB描画（Direct3D 11）
├─ src/MediaViewer.Updater   更新プログラム（MSI の適用・起動確認・ロールバック、Native AOT）
├─ tests/MediaViewer.Tests   単体テスト（色変換の数値検証、更新ファイルの検証を含む）
├─ installer/                MSI インストーラー（WiX Toolset v5）
├─ build/                    発行スクリプト、配布用 FFmpeg のビルド手順
└─ tools/                    アイコン生成・リリース署名・ライセンス文（RTF）生成のツール（開発用）
```

アイコンを差し替えるときは、元画像（`アイコン.png`）を更新して次を実行します。

```bash
dotnet run --project tools/IconBuilder/IconBuilder.csproj -- アイコン.png src/MediaViewer.App/Assets
```

仕様は `MediaViewer_開発指示書.md`、合意済みの変更は [MediaViewer_仕様決定事項.md](MediaViewer_仕様決定事項.md) を参照してください。

## 外部ライブラリ

外部ライブラリとそのライセンスは [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) を参照してください（アプリの「ヘルプ → このアプリについて」からも開けます）。
