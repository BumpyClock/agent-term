using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas.Text;

namespace AgentTerm_App;

internal readonly record struct ScriptRange(int Start, int Length, ushort Script, int Shapes);

internal sealed unsafe class DirectWriteShaper : IDisposable
{
    private static readonly Guid FactoryId = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
    private static readonly Guid FontFaceReferenceId = new("5E7FA7CA-DDE3-424C-89F0-9FCD6FED58CD");
    private static readonly Guid ResourceWrapperId = new("5F10688D-EA55-4D55-A3B0-4DDB55C0C20A");
    private static readonly Guid UnknownId = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid SourceId = new("688e1a58-5094-47c8-adc8-fbcea60ae92b");
    private static readonly Guid SinkId = new("5810cd44-0ca0-4701-b3fa-bec5182ae4f6");
    private static readonly nint SourceTable = Table(
        (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, char**, uint*, int>)&GetTextAtPosition,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, char**, uint*, int>)&GetTextBeforePosition,
        (nint)(delegate* unmanaged[Stdcall]<nint, int>)&GetParagraphReadingDirection,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint*, char**, int>)&GetLocaleName,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint*, nint*, int>)&GetNumberSubstitution);
    private static readonly nint SinkTable = Table(
        (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, ushort*, int>)&SetScriptAnalysis,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, nint, int>)&SetLineBreakpoints,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, byte, byte, int>)&SetBidiLevel,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, nint, int>)&SetNumberSubstitution);
    private static readonly char* EmptyLocale = (char*)NativeMemory.AllocZeroed(sizeof(char));

    private readonly Callbacks* _callbacks;
    private GCHandle _self;
    private readonly nint _analyzer;
    private ScriptRange[] _scripts = new ScriptRange[4];
    private int _scriptCount;
    private bool _rightToLeft;
    private ushort[] _clusters = new ushort[64];
    private ushort[] _textProperties = new ushort[64];
    private ushort[] _glyphs = new ushort[96];
    private ushort[] _glyphProperties = new ushort[96];
    private float[] _advances = new float[96];
    private CanvasGlyphOffset[] _offsets = new CanvasGlyphOffset[96];
    private uint[] _codepoints = new uint[64];

    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        internal nint Source;
        internal nint Sink;
        internal char* Text;
        internal uint Length;
        internal nint Owner;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CanvasGlyphOffset
    {
        internal float AdvanceOffset;
        internal float AscenderOffset;
    }

    internal DirectWriteShaper()
    {
        try
        {
            _callbacks = (Callbacks*)NativeMemory.AllocZeroed((nuint)sizeof(Callbacks));
            _analyzer = CreateAnalyzer();
            _self = GCHandle.Alloc(this, GCHandleType.Weak);
            _callbacks->Source = SourceTable;
            _callbacks->Sink = SinkTable;
            _callbacks->Owner = GCHandle.ToIntPtr(_self);
        }
        catch
        {
            if (_self.IsAllocated) _self.Free();
            if (_analyzer != 0) Marshal.Release(_analyzer);
            NativeMemory.Free(_callbacks);
            throw;
        }
    }

    internal ReadOnlySpan<ScriptRange> Scripts => _scripts.AsSpan(0, _scriptCount);
    internal ReadOnlySpan<ushort> Clusters => _clusters;
    internal ReadOnlySpan<ushort> Glyphs => _glyphs;
    internal ReadOnlySpan<float> Advances => _advances;
    internal ReadOnlySpan<CanvasGlyphOffset> Offsets => _offsets;

    internal bool Analyze(ReadOnlySpan<char> text)
    {
        _scriptCount = 0;
        _rightToLeft = false;
        fixed (char* characters = text)
        {
            _callbacks->Text = characters;
            _callbacks->Length = (uint)text.Length;
            var source = (nint)(&_callbacks->Source);
            var sink = (nint)(&_callbacks->Sink);
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, nint, int>)Slot(_analyzer, 4))(_analyzer, source, 0, (uint)text.Length, sink));
                if (_rightToLeft) return false;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, nint, int>)Slot(_analyzer, 3))(_analyzer, source, 0, (uint)text.Length, sink));
            }
            finally { _callbacks->Text = null; }
        }
        Array.Sort(_scripts, 0, _scriptCount, ScriptOrder.Instance);
        return true;
    }

    internal bool Covers(nint face, ReadOnlySpan<char> text)
    {
        if (_codepoints.Length < text.Length) Array.Resize(ref _codepoints, Math.Max(_codepoints.Length * 2, text.Length));
        if (_glyphs.Length < text.Length) Grow(text.Length, text.Length);
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                _codepoints[count++] = (uint)char.ConvertToUtf32(text[index], text[++index]);
            else _codepoints[count++] = text[index];
        }
        fixed (uint* codepoints = _codepoints)
        fixed (ushort* glyphs = _glyphs)
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, uint, ushort*, int>)Slot(face, 11))(face, codepoints, (uint)count, glyphs));
        return !_glyphs.AsSpan(0, count).Contains((ushort)0);
    }

    internal int Shape(nint face, float fontSize, ReadOnlySpan<char> text, ScriptRange script)
    {
        if (_clusters.Length < text.Length) Grow(text.Length, _glyphs.Length);
        var analysis = stackalloc ushort[4];
        analysis[0] = script.Script;
        *(int*)(analysis + 2) = script.Shapes;
        for (var attempt = 0; ; attempt++)
        {
            uint count;
            int result;
            fixed (char* characters = text)
            fixed (ushort* clusters = _clusters, textProperties = _textProperties, glyphs = _glyphs, glyphProperties = _glyphProperties)
                result = ((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, int, int, ushort*, char*, nint, nint, nint, uint, uint, ushort*, ushort*, ushort*, ushort*, uint*, int>)Slot(_analyzer, 7))(
                    _analyzer, characters, (uint)text.Length, face, 0, 0, analysis, EmptyLocale, 0, 0, 0, 0, (uint)_glyphs.Length,
                    clusters, textProperties, glyphs, glyphProperties, &count);
            if (result == unchecked((int)0x8007007A) && attempt < 8)
            {
                Grow(text.Length, _glyphs.Length * 2);
                continue;
            }
            Check(result);
            fixed (char* characters = text)
            fixed (ushort* clusters = _clusters, textProperties = _textProperties, glyphs = _glyphs, glyphProperties = _glyphProperties)
            fixed (float* advances = _advances)
            fixed (CanvasGlyphOffset* offsets = _offsets)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, ushort*, ushort*, uint, ushort*, ushort*, uint, nint, float, int, int, ushort*, char*, nint, nint, uint, float*, CanvasGlyphOffset*, int>)Slot(_analyzer, 8))(
                    _analyzer, characters, clusters, textProperties, (uint)text.Length, glyphs, glyphProperties, count, face, fontSize, 0, 0,
                    analysis, EmptyLocale, 0, 0, 0, advances, offsets));
            return (int)count;
        }
    }

    private void Grow(int characters, int glyphs)
    {
        if (_clusters.Length < characters)
        {
            Array.Resize(ref _clusters, characters);
            Array.Resize(ref _textProperties, characters);
        }
        if (_glyphs.Length < glyphs)
        {
            Array.Resize(ref _glyphs, glyphs);
            Array.Resize(ref _glyphProperties, glyphs);
            Array.Resize(ref _advances, glyphs);
            Array.Resize(ref _offsets, glyphs);
        }
    }

    public void Dispose()
    {
        if (!_self.IsAllocated) return;
        _self.Free();
        Marshal.Release(_analyzer);
        NativeMemory.Free(_callbacks);
    }

    internal static nint CreateFontFace(CanvasFontFace face)
    {
        var inspectable = WinRT.MarshalInspectable<object>.FromManaged(face);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in ResourceWrapperId, out var wrapper));
            try
            {
                nint reference;
                fixed (Guid* iid = &FontFaceReferenceId)
                    Check(((delegate* unmanaged[Stdcall]<nint, nint, float, Guid*, nint*, int>)Slot(wrapper, 3))(wrapper, 0, 0, iid, &reference));
                try
                {
                    nint native;
                    Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(reference, 3))(reference, &native));
                    return native;
                }
                finally { Marshal.Release(reference); }
            }
            finally { Marshal.Release(wrapper); }
        }
        finally { Marshal.Release(inspectable); }
    }

    internal static void ReleaseFontFace(nint face)
    {
        if (face != 0) Marshal.Release(face);
    }

    [DllImport("dwrite.dll")]
    private static extern int DWriteCreateFactory(int factoryType, in Guid iid, out nint factory);

    private static nint CreateAnalyzer()
    {
        Check(DWriteCreateFactory(0, FactoryId, out var factory));
        try
        {
            nint analyzer;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(factory, 21))(factory, &analyzer));
            return analyzer;
        }
        finally { Marshal.Release(factory); }
    }

    private static nint Table(params nint[] methods)
    {
        var table = (nint*)NativeMemory.Alloc((nuint)(methods.Length * sizeof(nint)));
        for (var index = 0; index < methods.Length; index++) table[index] = methods[index];
        return (nint)table;
    }

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    private static Callbacks* FromSource(nint self) => (Callbacks*)self;

    private static Callbacks* FromSink(nint self) => (Callbacks*)(self - sizeof(nint));

    private static DirectWriteShaper Owner(Callbacks* callbacks) => (DirectWriteShaper)GCHandle.FromIntPtr(callbacks->Owner).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(nint self, Guid* iid, nint* result)
    {
        var callbacks = *(nint*)self == SourceTable ? FromSource(self) : FromSink(self);
        *result = *iid == SinkId ? (nint)(&callbacks->Sink) : *iid == SourceId || *iid == UnknownId ? (nint)(&callbacks->Source) : 0;
        return *result == 0 ? unchecked((int)0x80004002) : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTextAtPosition(nint self, uint position, char** text, uint* length)
    {
        var callbacks = FromSource(self);
        var inside = position < callbacks->Length;
        *text = inside ? callbacks->Text + position : null;
        *length = inside ? callbacks->Length - position : 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTextBeforePosition(nint self, uint position, char** text, uint* length)
    {
        var callbacks = FromSource(self);
        var inside = position > 0 && position <= callbacks->Length;
        *text = inside ? callbacks->Text : null;
        *length = inside ? position : 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetParagraphReadingDirection(nint self) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetLocaleName(nint self, uint position, uint* length, char** locale)
    {
        var callbacks = FromSource(self);
        *length = position < callbacks->Length ? callbacks->Length - position : 0;
        *locale = EmptyLocale;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetNumberSubstitution(nint self, uint position, uint* length, nint* substitution)
    {
        var callbacks = FromSource(self);
        *length = position < callbacks->Length ? callbacks->Length - position : 0;
        *substitution = 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetScriptAnalysis(nint self, uint position, uint length, ushort* analysis)
    {
        var owner = Owner(FromSink(self));
        if (owner._scriptCount == owner._scripts.Length) Array.Resize(ref owner._scripts, owner._scripts.Length * 2);
        owner._scripts[owner._scriptCount++] = new ScriptRange((int)position, (int)length, analysis[0], *(int*)(analysis + 2));
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetLineBreakpoints(nint self, uint position, uint length, nint breakpoints) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetBidiLevel(nint self, uint position, uint length, byte explicitLevel, byte resolvedLevel)
    {
        if (resolvedLevel % 2 != 0) Owner(FromSink(self))._rightToLeft = true;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetNumberSubstitution(nint self, uint position, uint length, nint substitution) => 0;

    private sealed class ScriptOrder : System.Collections.Generic.IComparer<ScriptRange>
    {
        internal static readonly ScriptOrder Instance = new();
        public int Compare(ScriptRange left, ScriptRange right) => left.Start.CompareTo(right.Start);
    }
}
