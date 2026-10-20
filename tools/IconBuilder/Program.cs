using System.Buffers.Binary;
using Vortice.Mathematics;
using Vortice.WIC;

// 使い方: IconBuilder <元画像.png> <出力フォルダ>
// 1. 透明部分に残った小さなゴミ（本体とつながっていない点）を取り除く
// 2. 本体を中央に置いた正方形へ切り出す
// 3. 各サイズへ高品質に縮小し、AppIcon.ico（PNG形式のエントリ）と PNG を出力する
if (args.Length != 2)
{
    Console.Error.WriteLine("使い方: IconBuilder <元画像.png> <出力フォルダ>");
    return 1;
}

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
int[] pngSizes = [32, 64, 256];
const int AlphaThreshold = 8;
const double Margin = 0.02; // 本体の周囲に残す余白（辺の長さに対する割合）

using var wic = new IWICImagingFactory();
var (width, height, pixels) = Decode(wic, args[0]);
Console.WriteLine($"元画像: {width}x{height}");

// ---- ゴミ取り：アルファのある画素の連結成分のうち、最大のもの（アイコン本体）だけを残す ----
var labels = new int[width * height];
var sizes = new List<int> { 0 };
var stack = new Stack<int>();
for (var i = 0; i < labels.Length; i++)
{
    if (labels[i] != 0 || pixels[i * 4 + 3] <= AlphaThreshold) continue;
    var id = sizes.Count;
    var count = 0;
    labels[i] = id;
    stack.Push(i);
    while (stack.Count > 0)
    {
        var p = stack.Pop();
        count++;
        int x = p % width, y = p / width;
        Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
        void Visit(int vx, int vy)
        {
            if (vx < 0 || vy < 0 || vx >= width || vy >= height) return;
            var q = vy * width + vx;
            if (labels[q] == 0 && pixels[q * 4 + 3] > AlphaThreshold)
            {
                labels[q] = id;
                stack.Push(q);
            }
        }
    }
    sizes.Add(count);
}
var main = sizes.IndexOf(sizes.Max());
int removedComponents = 0, removedPixels = 0;
int minX = width, minY = height, maxX = 0, maxY = 0;
for (var i = 0; i < labels.Length; i++)
{
    if (labels[i] == main)
    {
        int x = i % width, y = i / width;
        minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        continue;
    }
    // 本体以外（ゴミ、閾値以下のかすかな画素）は完全に透明にする
    if (pixels[i * 4 + 3] != 0) removedPixels++;
    pixels[i * 4 + 0] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = pixels[i * 4 + 3] = 0;
}
removedComponents = sizes.Count - 2;
Console.WriteLine($"ゴミ取り: 本体以外の成分 {removedComponents} 個、画素 {removedPixels} 個を透明化");
Console.WriteLine($"本体の範囲: ({minX},{minY})-({maxX},{maxY})");

// ---- 本体を中央に置いた正方形へ切り出す ----
var side = Math.Max(maxX - minX + 1, maxY - minY + 1);
side += (int)Math.Ceiling(side * Margin * 2);
var squarePixels = new byte[side * side * 4];
var offX = (side - (maxX - minX + 1)) / 2 - minX;
var offY = (side - (maxY - minY + 1)) / 2 - minY;
for (var y = minY; y <= maxY; y++)
{
    Buffer.BlockCopy(pixels, (y * width + minX) * 4, squarePixels, ((y + offY) * side + minX + offX) * 4, (maxX - minX + 1) * 4);
}
Console.WriteLine($"正方形: {side}x{side}");

Directory.CreateDirectory(args[1]);
using var square = CreateBitmap(wic, side, squarePixels);
// 縮小は乗算済みアルファで行い、縁の色にじみ（ハロー）を防ぐ
using var premultiplied = wic.CreateFormatConverter();
premultiplied.Initialize(square, PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);

var icoEntries = new List<(int Size, byte[] Png)>();
foreach (var size in icoSizes.Union(pngSizes).Order())
{
    var png = EncodePng(wic, premultiplied, size);
    if (icoSizes.Contains(size)) icoEntries.Add((size, png));
    if (pngSizes.Contains(size)) File.WriteAllBytes(Path.Combine(args[1], $"AppIcon-{size}.png"), png);
}
File.WriteAllBytes(Path.Combine(args[1], "AppIcon.ico"), BuildIco(icoEntries));
Console.WriteLine($"出力: {Path.GetFullPath(args[1])}");
return 0;

static (int Width, int Height, byte[] Pixels) Decode(IWICImagingFactory wic, string path)
{
    using var decoder = wic.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnLoad);
    using var frame = decoder.GetFrame(0);
    using var converter = wic.CreateFormatConverter();
    converter.Initialize(frame, PixelFormat.Format32bppBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
    var size = converter.Size;
    var pixels = new byte[size.Width * size.Height * 4];
    converter.CopyPixels((uint)(size.Width * 4), pixels);
    return (size.Width, size.Height, pixels);
}

static unsafe IWICBitmap CreateBitmap(IWICImagingFactory wic, int side, byte[] bgra)
{
    fixed (byte* p = bgra)
    {
        return wic.CreateBitmapFromMemory((uint)side, (uint)side, PixelFormat.Format32bppBGRA, (uint)(side * 4), (uint)bgra.Length, p);
    }
}

static byte[] EncodePng(IWICImagingFactory wic, IWICBitmapSource source, int size)
{
    using var scaler = wic.CreateBitmapScaler();
    scaler.Initialize(source, (uint)size, (uint)size, BitmapInterpolationMode.HighQualityCubic);
    using var straight = wic.CreateFormatConverter();
    straight.Initialize(scaler, PixelFormat.Format32bppBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
    var pixels = new byte[size * size * 4];
    straight.CopyPixels((uint)(size * 4), pixels);

    using var ms = new MemoryStream();
    using (var encoder = wic.CreateEncoder(ContainerFormat.Png, ms, BitmapEncoderCacheOption.NoCache))
    {
        using var frame = encoder.CreateNewFrame(out var props);
        frame.Initialize(props);
        frame.SetSize((uint)size, (uint)size);
        frame.SetPixelFormat(PixelFormat.Format32bppBGRA);
        frame.WritePixels((uint)size, (uint)(size * 4), pixels);
        frame.Commit();
        encoder.Commit();
        props?.Dispose();
    }
    return ms.ToArray();
}

// ICO 形式：ヘッダー（6バイト）＋ エントリ（16バイト×枚数）＋ 各画像（PNG）
static byte[] BuildIco(List<(int Size, byte[] Png)> entries)
{
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[6];
    BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 1);
    BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)entries.Count);
    ms.Write(header);
    var offset = 6 + 16 * entries.Count;
    Span<byte> e = stackalloc byte[16];
    foreach (var (size, png) in entries)
    {
        e.Clear();
        e[0] = (byte)(size >= 256 ? 0 : size);
        e[1] = (byte)(size >= 256 ? 0 : size);
        BinaryPrimitives.WriteUInt16LittleEndian(e[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(e[6..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)offset);
        ms.Write(e);
        offset += png.Length;
    }
    foreach (var (_, png) in entries) ms.Write(png);
    return ms.ToArray();
}
