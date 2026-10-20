<#
.SYNOPSIS
  配布用の発行フォルダを作る（アプリ本体＋更新プログラム＋FFmpeg）。リリース用ワークフローと手元の確認で共通に使う。

.PARAMETER Version
  バージョン番号（例 1.2.3）。

.PARAMETER Output
  発行先のフォルダ。既存の中身は消してから作る。

.PARAMETER FfmpegDir
  同梱する FFmpeg（ffmpeg.exe・ffprobe.exe・DLL・licenses）が入ったフォルダ。発行先の ffmpeg フォルダへ複写する。
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Output,
    [Parameter(Mandatory)] [string] $FfmpegDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$Output = [IO.Path]::GetFullPath($Output)

if (-not (Test-Path (Join-Path $FfmpegDir 'ffmpeg.exe')) -or -not (Test-Path (Join-Path $FfmpegDir 'ffprobe.exe'))) {
    throw "FFmpeg が見つかりません：$FfmpegDir"
}
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

# アプリ本体：自己完結、トリミング、ReadyToRun（起動を速くする、仕様 §11）
dotnet publish (Join-Path $root 'src/MediaViewer.App/MediaViewer.App.csproj') -c Release -p:Platform=x64 `
    -p:Version=$Version -p:PublishTrimmed=true -p:PublishReadyToRun=true -p:DebugType=none -o $Output
if ($LASTEXITCODE -ne 0) { throw 'アプリの発行に失敗しました。' }

# 更新プログラム：Native AOT の単体実行ファイル
$updaterOut = Join-Path ([IO.Path]::GetTempPath()) ("MediaViewer-Updater-" + [guid]::NewGuid().ToString('N'))
dotnet publish (Join-Path $root 'src/MediaViewer.Updater/MediaViewer.Updater.csproj') -c Release `
    -p:Version=$Version -p:DebugType=none -o $updaterOut
if ($LASTEXITCODE -ne 0) { throw '更新プログラムの発行に失敗しました。' }
Copy-Item (Join-Path $updaterOut 'MediaViewer.Updater.exe') $Output
Remove-Item $updaterOut -Recurse -Force

# 変換用の FFmpeg
Copy-Item $FfmpegDir (Join-Path $Output 'ffmpeg') -Recurse

$size = (Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("発行しました：{0}（{1:N1} MB）" -f $Output, ($size / 1MB))
