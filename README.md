# MediaViewer（仮称）

画像・動画・音声を、同じ画面・同じ操作で扱う Windows 向けの軽量メディアビューワーです。
画像の色管理（埋め込みICC・広色域・HDR／自動色管理）を最重要の品質要件としています。

- 開発者：三玉春樹 / はるたまプロジェクト
- アカウント：https://x.com/MitamaHaruki
- 対象OS：Windows 11 24H2 以降（x64）
- ライセンス：GNU General Public License v3.0 以降（[LICENSE](LICENSE)）。Windows App SDK とのリンクについて GPLv3 第7条の追加許可あり（[LICENSE-ADDITIONAL-PERMISSION.txt](LICENSE-ADDITIONAL-PERMISSION.txt)）

> 現在は開発中です。正式名称は未決定です。

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
| 形式変換（画像：PNG／JPEG／BMP／GIF／WebP／TIFF、動画：MP4／MKV／MOV／AVI／WebM、音声：MP3／WAV／FLAC／M4A／OGG） | 実装済み（配布用 FFmpeg の同梱は Phase 5） |
| 自動更新 | 未実装（Phase 5） |

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

配布用の発行（自己完結・トリミング・ReadyToRun）：

```bash
dotnet publish src/MediaViewer.App/MediaViewer.App.csproj -c Release -r win-x64 -p:PublishReadyToRun=true -p:PublishTrimmed=true -o artifacts/publish
```

## 構成

```text
MediaViewer.slnx
├─ src/MediaViewer.App       WinUI 3 の画面・入力・再生制御
├─ src/MediaViewer.Core      UI非依存のロジック（表示倍率・前後移動・ログ）
├─ src/MediaViewer.Imaging   画像デコード（WIC）・ICC色変換（Direct2D）・scRGB描画（Direct3D 11）
├─ tests/MediaViewer.Tests   単体テスト（色変換の数値検証を含む）
└─ tools/IconBuilder         アイコン生成ツール（開発用）
```

アイコンを差し替えるときは、元画像（`アイコン.png`）を更新して次を実行します。

```bash
dotnet run --project tools/IconBuilder/IconBuilder.csproj -- アイコン.png src/MediaViewer.App/Assets
```

仕様は `MediaViewer_開発指示書.md`、合意済みの変更は [MediaViewer_仕様決定事項.md](MediaViewer_仕様決定事項.md) を参照してください。

## 外部ライブラリ

外部ライブラリとそのライセンスは [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) を参照してください（アプリの「ヘルプ → このアプリについて」からも開けます）。
