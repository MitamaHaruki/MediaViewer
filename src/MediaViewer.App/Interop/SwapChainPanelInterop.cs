using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace MediaViewer.App.Interop;

/// <summary>SwapChainPanel にスワップチェーンを割り当てる（ISwapChainPanelNative::SetSwapChain）。</summary>
internal static class SwapChainPanelInterop
{
    private static readonly Guid IidSwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    public static unsafe void SetSwapChain(SwapChainPanel panel, IntPtr swapChain)
    {
        var unknown = ((IWinRTObject)panel).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IidSwapChainPanelNative, out var native));
        try
        {
            // vtable: [0..2] IUnknown, [3] SetSwapChain
            var vtable = *(IntPtr**)native;
            var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtable[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }
}
