"""インストーラーのライセンス画面に表示する installer/License.rtf を生成する。

LICENSE（GPL-3.0）と LICENSE-ADDITIONAL-PERMISSION.txt を連結し、冒頭に日本語の説明を付ける。
使い方（リポジトリ直下で）: python tools/LicenseRtf/make_license_rtf.py
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

INTRO = """メディアビューワー
開発者：三玉春樹 / はるたまプロジェクト（https://x.com/MitamaHaruki）

このソフトウェアは、GNU 一般公衆利用許諾書 第3版（GPL-3.0）またはそれ以降の版の条件のもとで配布するフリーソフトウェアです。どなたでも無料で使用でき、条件に従って再配布・改変できます。ソースコードは https://github.com/MitamaHaruki/MediaViewer で公開しています。

このソフトウェアは無保証です。詳しくは以下の条文（英語）をご覧ください。同梱しているライブラリ（FFmpeg など）のライセンスは、インストール先の THIRD-PARTY-NOTICES.txt と ffmpeg\\licenses フォルダにあります。

「同意する」を選ぶと、インストールを続けます。
"""

BACKSLASH = chr(0x5C)


def rtf_escape(text: str) -> str:
    out = []
    for ch in text:
        code = ord(ch)
        if ch in (BACKSLASH, "{", "}"):
            out.append(BACKSLASH + ch)
        elif ch == "\n":
            out.append(BACKSLASH + "par\n")
        elif ch == "\t":
            out.append(BACKSLASH + "tab ")
        elif code < 0x80:
            out.append(ch)
        else:
            # RTF の u 制御語は符号付き16ビット。続く ? は Unicode 非対応の読み手向けの代替文字
            out.append(f"{BACKSLASH}u{code - 0x10000 if code > 0x7FFF else code}?")
    return "".join(out)


def main() -> None:
    license_text = (ROOT / "LICENSE").read_text(encoding="utf-8")
    permission = (ROOT / "LICENSE-ADDITIONAL-PERMISSION.txt").read_text(encoding="utf-8")
    body = INTRO + "\n" + permission.strip() + "\n\n" + license_text.strip() + "\n"
    header = r"{\rtf1\ansi\ansicpg932\deff0\uc1{\fonttbl{\f0\fnil\fcharset128 Yu Gothic UI;}}\viewkind4\pard\f0\fs18" + "\n"
    rtf = header + rtf_escape(body) + "}\n"
    (ROOT / "installer" / "License.rtf").write_text(rtf, encoding="ascii", newline="\n")


if __name__ == "__main__":
    main()
