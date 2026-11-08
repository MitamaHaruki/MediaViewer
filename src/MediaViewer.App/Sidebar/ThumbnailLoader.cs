using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using FFmpegInteropX;
using MediaViewer.Core.Media;
using MediaViewer.Imaging.Conversion;

namespace MediaViewer.App.Sidebar;

/// <summary>
/// サイドバーのサムネイルを作る（作業スレッドで呼ぶ）。
/// 画像は色管理して sRGB に変換したもの（埋め込み ICC・EXIF の向きを反映）、動画・音声は Windows の
/// サムネイル（動画の1コマ・アルバムアート。Windows のサムネイルキャッシュを使う）。作れなければ null。
/// 音声で Windows がアルバムアートを取り出せない場合は、再生と同じ FFmpeg で取り出す。
/// </summary>
internal static partial class ThumbnailLoader
{
    private static readonly Guid IidShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private const int SiigbfThumbnailOnly = 0x08;
    private const uint CoinitMultithreaded = 0;

    public static async Task<SrgbBitmap?> LoadAsync(string path, int size, CancellationToken cancellationToken)
    {
        var kind = MediaKinds.FromPath(path);
        if (kind == MediaKind.Image)
        {
            try
            {
                return ImageConverter.RenderSrgb(path, size, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 読めない画像・HDR 画像などは Windows のサムネイルを試す
            }
        }
        var thumbnail = ShellThumbnail(path, size);
        if (thumbnail is null && kind == MediaKind.Audio) thumbnail = await AlbumArtAsync(path, size, cancellationToken);
        return thumbnail;
    }

    /// <summary>音声ファイルに埋め込まれたアルバムアートを取り出し、色管理して sRGB にする。</summary>
    private static async Task<SrgbBitmap?> AlbumArtAsync(string path, int size, CancellationToken cancellationToken)
    {
        try
        {
            using var source = await FFmpegMediaSource.CreateFromFileAsync(path, new MediaSourceConfig());
            if (!source.HasThumbnail) return null;
            using var thumb = source.ExtractThumbnail();
            var bytes = thumb?.Buffer?.ToArray();
            return bytes is { Length: > 0 } ? ImageConverter.RenderSrgb(bytes, size, cancellationToken) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static unsafe SrgbBitmap? ShellThumbnail(string path, int size)
    {
        var initialized = CoInitializeEx(0, CoinitMultithreaded) >= 0;
        nint factory = 0;
        nint bitmap = 0;
        try
        {
            if (SHCreateItemFromParsingName(path, 0, IidShellItemImageFactory, out factory) < 0 || factory == 0) return null;
            // IShellItemImageFactory の vtable：0 QueryInterface、1 AddRef、2 Release、3 GetImage
            var getImage = (delegate* unmanaged[Stdcall]<nint, NativeSize, int, nint*, int>)(*(nint**)factory)[3];
            if (getImage(factory, new NativeSize(size, size), SiigbfThumbnailOnly, &bitmap) < 0 || bitmap == 0) return null;
            return CopyBitmap(bitmap);
        }
        catch (Exception e) when (e is COMException or SEHException)
        {
            return null;
        }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (factory != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)factory)[2])(factory);
            if (initialized) CoUninitialize();
        }
    }

    /// <summary>HBITMAP を上から下への BGRA（アルファ乗算済み）にする。アルファがすべて 0 なら不透明として扱う。</summary>
    private static unsafe SrgbBitmap? CopyBitmap(nint bitmap)
    {
        NativeBitmap info;
        if (GetObjectW(bitmap, sizeof(NativeBitmap), &info) == 0 || info.Width <= 0 || info.Height == 0) return null;
        var width = info.Width;
        var height = Math.Abs(info.Height);
        var pixels = new byte[width * height * 4];
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
        };
        var dc = GetDC(0);
        try
        {
            fixed (byte* p = pixels)
            {
                if (GetDIBits(dc, bitmap, 0, (uint)height, p, &header, 0) == 0) return null;
            }
        }
        finally
        {
            ReleaseDC(0, dc);
        }
        var anyAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                anyAlpha = true;
                break;
            }
        }
        if (!anyAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }
        return new SrgbBitmap(width, height, pixels);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int cx, int cy)
    {
        public readonly int Cx = cx;
        public readonly int Cy = cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        // GetDIBits は BITMAPINFO（色表を含む）を受け取る。32bpp・BI_RGB では色表は使わないが、念のため余白を取る
        public fixed uint Colors[3];
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid riid, out nint item);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetObjectW(nint handle, int size, void* buffer);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, void* info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
