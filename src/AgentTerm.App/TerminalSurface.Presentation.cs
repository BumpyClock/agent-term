using System;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace AgentTerm_App;

public sealed partial class TerminalSurface
{
    internal sealed class SharedRasterSink(CanvasDevice raster, CanvasDevice presentation, CompositionDrawingSurface surface)
        : IRasterSink, IDisposable
    {
        private SharedTexture? _shared;
        private CanvasRenderTarget? _staging;
        private SizeInt32 _surfaceSize;
        private Rect _transfer;

        public CanvasRenderTarget CreateRaster(float width, float height, float dpi)
        {
            ReleaseShared();
            _shared = new SharedTexture(raster, presentation, PixelsOf(width, dpi), PixelsOf(height, dpi));
            return CanvasRenderTarget.CreateFromDirect3D11Surface(raster, _shared.RasterSurface, dpi);
        }

        public bool IsDeviceLost(int hresult) => presentation.IsDeviceLost(hresult);

        public bool Present(CanvasRenderTarget target, Rect dirtyPixels)
        {
            var shared = _shared ?? throw new InvalidOperationException("No shared raster was allocated.");
            var size = target.SizeInPixels;
            var pixels = new SizeInt32((int)size.Width, (int)size.Height);
            if (_staging is null || _staging.SizeInPixels != size)
            {
                _staging?.Dispose();
                _staging = new CanvasRenderTarget(presentation, pixels.Width, pixels.Height, 96);
                dirtyPixels = new Rect(0, 0, pixels.Width, pixels.Height);
            }
            if (_surfaceSize != pixels)
            {
                surface.Resize(pixels);
                _surfaceSize = pixels;
                dirtyPixels = new Rect(0, 0, pixels.Width, pixels.Height);
            }
            _transfer = dirtyPixels;
            shared.Yield();
            return TryCompletePresent();
        }

        public bool TryCompletePresent()
        {
            var shared = _shared ?? throw new InvalidOperationException("No shared raster was allocated.");
            if (shared.Holder == SharedHolder.AwaitingPresentation)
            {
                if (!shared.TryStage(_staging!, _transfer)) return false;
                using var drawing = CanvasComposition.CreateDrawingSession(surface, _transfer, 96);
                // Win2D maps the update rectangle's top-left corner to the session origin.
                drawing.DrawImage(_staging, 0, 0, _transfer, 1, CanvasImageInterpolation.NearestNeighbor, CanvasComposite.Copy);
            }
            return shared.TryReclaim();
        }

        public void Dispose()
        {
            ReleaseShared();
            _staging?.Dispose();
            _staging = null;
        }

        private void ReleaseShared()
        {
            _shared?.Dispose();
            _shared = null;
        }

        internal static int PixelsOf(float dips, float dpi) => Math.Max(1, (int)MathF.Round(dips * dpi / 96, MidpointRounding.AwayFromZero));
    }

    internal enum SharedHolder { Raster, AwaitingPresentation, AwaitingRaster }

    internal sealed unsafe class SharedTexture : IDisposable
    {
        private const ulong RasterKey = 0, PresentationKey = 1;
        private static readonly Guid DxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        private static readonly Guid D3D11Device = new("DB6F6DDB-AC77-4E88-8253-819DF9BBF140");
        private static readonly Guid DxgiResource = new("035F3AB4-482E-4E50-B41F-8A7F8BD8960B");
        private static readonly Guid DxgiKeyedMutex = new("9D8E1289-D7B3-465F-8126-250E349AF85D");
        private static readonly Guid DxgiSurface = new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");
        private const uint FormatB8G8R8A8 = 87, BindShaderResource = 0x8, BindRenderTarget = 0x20, MiscSharedKeyedMutex = 0x100;

        private readonly CanvasDevice _raster, _presentation;
        private readonly CanvasBitmap? _view;
        private nint _rasterMutex, _presentationMutex, _presentationContext;
        internal IDirect3DSurface RasterSurface { get; }
        internal SharedHolder Holder { get; private set; } = SharedHolder.AwaitingRaster;

        internal SharedTexture(CanvasDevice raster, CanvasDevice presentation, int width, int height)
        {
            (_raster, _presentation) = (raster, presentation);
            nint rasterDevice = 0, texture = 0, resource = 0, rasterDxgi = 0, presentationDevice = 0, opened = 0;
            try
            {
                rasterDevice = D3DDevice(raster);
                var desc = stackalloc uint[11] { (uint)width, (uint)height, 1, 1, FormatB8G8R8A8, 1, 0, 0,
                    BindRenderTarget | BindShaderResource, 0, MiscSharedKeyedMutex };
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Slot(rasterDevice, 5))(rasterDevice, desc, 0, &texture));
                resource = Query(texture, DxgiResource);
                nint handle;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(resource, 8))(resource, &handle));
                _rasterMutex = Query(texture, DxgiKeyedMutex);
                rasterDxgi = Query(texture, DxgiSurface);
                presentationDevice = D3DDevice(presentation);
                var surfaceIid = DxgiSurface;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(presentationDevice, 28))(
                    presentationDevice, handle, &surfaceIid, &opened));
                _presentationMutex = Query(opened, DxgiKeyedMutex);
                fixed (nint* context = &_presentationContext)
                    ((delegate* unmanaged[Stdcall]<nint, nint*, void>)Slot(presentationDevice, 40))(presentationDevice, context);
                RasterSurface = Direct3DSurface(rasterDxgi);
                using var presentationSurface = Direct3DSurface(opened);
                _view = CanvasBitmap.CreateFromDirect3D11Surface(presentation, presentationSurface, 96);
                if (!TryReclaim()) throw new InvalidOperationException("A new shared raster was not available to its creator.");
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                foreach (var pointer in (ReadOnlySpan<nint>)[rasterDevice, texture, resource, rasterDxgi, presentationDevice, opened])
                    if (pointer != 0) Marshal.Release(pointer);
            }
        }

        internal void Yield()
        {
            using (_raster.Lock()) KeyedMutex.Release(_rasterMutex, PresentationKey);
            Holder = SharedHolder.AwaitingPresentation;
        }

        internal bool TryStage(CanvasRenderTarget staging, Rect dirtyPixels)
        {
            using (_presentation.Lock())
                if (!KeyedMutex.TryAcquire(_presentationMutex, PresentationKey)) return false;
            staging.CopyPixelsFromBitmap(_view!, (int)dirtyPixels.X, (int)dirtyPixels.Y, (int)dirtyPixels.X, (int)dirtyPixels.Y,
                (int)dirtyPixels.Width, (int)dirtyPixels.Height);
            using (_presentation.Lock())
            {
                ((delegate* unmanaged[Stdcall]<nint, void>)Slot(_presentationContext, 111))(_presentationContext);
                KeyedMutex.Release(_presentationMutex, RasterKey);
            }
            Holder = SharedHolder.AwaitingRaster;
            return true;
        }

        internal bool TryReclaim()
        {
            using (_raster.Lock())
                if (!KeyedMutex.TryAcquire(_rasterMutex, RasterKey)) return false;
            Holder = SharedHolder.Raster;
            return true;
        }

        public void Dispose()
        {
            _view?.Dispose();
            RasterSurface?.Dispose();
            foreach (var pointer in (ReadOnlySpan<nint>)[_rasterMutex, _presentationMutex, _presentationContext])
                if (pointer != 0) Marshal.Release(pointer);
            _rasterMutex = _presentationMutex = _presentationContext = 0;
        }

        private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

        private static nint Query(nint instance, Guid iid)
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(instance, in iid, out var result));
            return result;
        }

        private static nint D3DDevice(CanvasDevice device)
        {
            var access = Query(((IWinRTObject)device).NativeObject.ThisPtr, DxgiInterfaceAccess);
            try
            {
                var iid = D3D11Device;
                nint result;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(access, 3))(access, &iid, &result));
                return result;
            }
            finally { Marshal.Release(access); }
        }

        private static IDirect3DSurface Direct3DSurface(nint dxgiSurface)
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out var inspectable));
            try { return MarshalInterface<IDirect3DSurface>.FromAbi(inspectable); }
            finally { Marshal.Release(inspectable); }
        }

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint dxgiSurface, out nint graphicsSurface);
    }

    private static unsafe class KeyedMutex
    {
        private const int WaitAbandoned = 0x80, WaitTimeout = 0x102, DeviceRemoved = unchecked((int)0x887A0005);

        internal static bool TryAcquire(nint mutex, ulong key)
        {
            var result = ((delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)(*(nint**)mutex)[8])(mutex, key, 0);
            if (result == WaitTimeout) return false;
            Check(result, "AcquireSync");
            return true;
        }

        internal static void Release(nint mutex, ulong key) =>
            Check(((delegate* unmanaged[Stdcall]<nint, ulong, int>)(*(nint**)mutex)[9])(mutex, key), "ReleaseSync");

        private static void Check(int result, string operation)
        {
            Marshal.ThrowExceptionForHR(result == WaitAbandoned ? DeviceRemoved : result);
            if (result != 0) throw new InvalidOperationException($"{operation} returned 0x{result:X8}.");
        }
    }
}
