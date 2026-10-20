#!/usr/bin/env bash
# メディアビューワー同梱用の FFmpeg（変換に必要な機能だけ）をビルドする。
# MSYS2 の UCRT64 環境で実行する（GitHub Actions の ffmpeg.yml から呼ばれる）。
#
# 方針（docs/Phase4_検証報告.md §3）：
#   - 読み込み（デマルチプレクサー・デコーダー）は原則すべて残す（再生できるファイルは変換もできるように）
#   - 書き出しは変換に使う形式・エンコーダーだけ
#   - ネットワーク・デバイス・ハードウェア拡張は含めない
#   - ffmpeg と ffprobe で共有ライブラリを共用し、サイズを抑える
set -euo pipefail

FFMPEG_VERSION="9.0.2"
FFMPEG_SHA256="8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e"
BUILD_REVISION="${BUILD_REVISION:-1}"

ROOT="$(cd "$(dirname "$0")" && pwd)"
WORK="${WORK:-$ROOT/work}"
PREFIX="$WORK/install"
OUT="${OUT:-$ROOT/out}"
PACKAGE="ffmpeg-${FFMPEG_VERSION}-mv${BUILD_REVISION}-win64"

mkdir -p "$WORK" "$OUT"
cd "$WORK"

# ---- ソースの取得と検証 ----
TARBALL="ffmpeg-${FFMPEG_VERSION}.tar.xz"
if [ ! -f "$TARBALL" ]; then
  curl -fsSL --retry 3 -o "$TARBALL" "https://ffmpeg.org/releases/${TARBALL}"
fi
echo "${FFMPEG_SHA256}  ${TARBALL}" | sha256sum -c -
rm -rf "ffmpeg-${FFMPEG_VERSION}"
tar -xf "$TARBALL"
cd "ffmpeg-${FFMPEG_VERSION}"

# ---- 構成 ----
ENCODERS="libx264,libvpx_vp9,aac,libopus,libmp3lame,libvorbis,flac,pcm_s16le,pcm_s24le,mov_text,webvtt,libwebp,mjpeg,png"
MUXERS="mp4,mov,ipod,matroska,webm,avi,mp3,wav,flac,ogg,webp,null"
FILTERS="scale,format,aformat,aresample,null,anull,transpose,hflip,vflip,setsar,pad,copy,acopy,fps"

# MSYS2 の iconv.h は関数名を libiconv に置き換えるが、configure はリンク指定を加えないため明示する
./configure \
  --prefix="$PREFIX" \
  --arch=x86_64 \
  --target-os=mingw32 \
  --enable-shared --disable-static \
  --enable-gpl \
  --disable-doc --disable-debug --disable-ffplay \
  --disable-autodetect --enable-w32threads \
  --disable-network --disable-devices \
  --disable-protocols --enable-protocol=file,pipe \
  --disable-encoders --enable-encoder="$ENCODERS" \
  --disable-muxers --enable-muxer="$MUXERS" \
  --disable-filters --enable-filter="$FILTERS" \
  --enable-libx264 --enable-libvpx --enable-libopus --enable-libmp3lame --enable-libvorbis \
  --enable-libwebp --enable-libdav1d \
  --enable-zlib --enable-bzlib --enable-iconv \
  --extra-libs=-liconv \
  --extra-version="mediaviewer${BUILD_REVISION}"

make -j"$(nproc)"
make install

# ---- 配布用にまとめる ----
STAGE="$OUT/$PACKAGE"
rm -rf "$STAGE"
mkdir -p "$STAGE/licenses"
cp "$PREFIX/bin/ffmpeg.exe" "$PREFIX/bin/ffprobe.exe" "$STAGE/"
cp "$PREFIX"/bin/*.dll "$STAGE/"

# 依存する MSYS2 の DLL（x264・libvpx・opus 等）を ldd で集める
collect() {
  ldd "$1" | awk '{print $3}' | grep -i '/ucrt64/' || true
}
for exe in "$STAGE"/*.exe "$STAGE"/*.dll; do
  for dep in $(collect "$exe"); do
    cp -n "$dep" "$STAGE/" || true
  done
done
# 2段目の依存も集める
for dll in "$STAGE"/*.dll; do
  for dep in $(collect "$dll"); do
    cp -n "$dep" "$STAGE/" || true
  done
done

# ライセンスと、ソースの入手先（GPL の「対応するソース」）の情報
cp COPYING.GPLv2 LICENSE.md "$STAGE/licenses/" 2>/dev/null || cp LICENSE.md "$STAGE/licenses/"
{
  echo "FFmpeg ${FFMPEG_VERSION}（メディアビューワー同梱用ビルド ${BUILD_REVISION}）"
  echo
  echo "ライセンス: GNU General Public License v2 以降（--enable-gpl）"
  echo "FFmpeg のソース: https://ffmpeg.org/releases/${TARBALL}（SHA-256 ${FFMPEG_SHA256}）"
  echo "ビルド手順: https://github.com/MitamaHaruki/MediaViewer/tree/main/build/ffmpeg"
  echo
  echo "configure の指定:"
  sed -n 's/^  *--/  --/p' "$ROOT/build.sh"
  echo
  echo "同梱している MSYS2 パッケージ（ソースは同じリリースに添付）:"
  pacman -Q mingw-w64-ucrt-x86_64-x264 mingw-w64-ucrt-x86_64-libvpx mingw-w64-ucrt-x86_64-opus \
    mingw-w64-ucrt-x86_64-lame mingw-w64-ucrt-x86_64-libvorbis mingw-w64-ucrt-x86_64-libogg \
    mingw-w64-ucrt-x86_64-libwebp mingw-w64-ucrt-x86_64-dav1d mingw-w64-ucrt-x86_64-zlib \
    mingw-w64-ucrt-x86_64-bzip2 mingw-w64-ucrt-x86_64-libiconv mingw-w64-ucrt-x86_64-gcc-libs \
    mingw-w64-ucrt-x86_64-libwinpthread 2>/dev/null || true
  echo
  echo "含まれる DLL:"
  (cd "$STAGE" && ls -1 *.dll)
} > "$STAGE/licenses/FFMPEG-BUILD-INFO.txt"

# 動作確認：必要なエンコーダー・書き出し形式が入っているか
"$STAGE/ffmpeg.exe" -hide_banner -encoders > "$WORK/encoders.txt"
for e in libx264 libvpx-vp9 aac libopus libmp3lame libvorbis flac mov_text webvtt libwebp; do
  grep -q " $e " "$WORK/encoders.txt" || { echo "エンコーダー $e がありません"; exit 1; }
done
"$STAGE/ffprobe.exe" -hide_banner -version | head -1

(cd "$OUT" && rm -f "$PACKAGE.zip" && 7z a -tzip -mx=9 "$PACKAGE.zip" "$PACKAGE" > /dev/null)
(cd "$OUT" && sha256sum "$PACKAGE.zip" > "$PACKAGE.zip.sha256")
cp "$WORK/$TARBALL" "$OUT/"
du -sh "$STAGE" "$OUT/$PACKAGE.zip"
echo "PACKAGE=$PACKAGE" >> "${GITHUB_ENV:-/dev/null}"
