using projectFrameCut.Render.PreviewAudio;
using System.Runtime.InteropServices;

namespace projectFrameCut.Platforms.Windows;

internal sealed class WasapiPreviewAudioSink : IAudioPreviewSink
{
    private const uint StreamFlags = 0x80000000 | 0x08000000;
    private IAudioClient? _client;
    private IAudioRenderClient? _render;
    private IAudioClock? _clock;
    private object? _enumerator;
    private object? _device;
    private uint _bufferFrames;
    private PreviewAudioFormat _format;
    private long _writtenFrames;
    private ulong _clockFrequency;
    private bool _running;
    private bool _paused;
    private bool _startRequested;

    public ValueTask InitializeAsync(PreviewAudioFormat format, CancellationToken cancellationToken)
    {
        _format = format;
        _enumerator = new MMDeviceEnumerator();
        var enumerator = (IMMDeviceEnumerator)_enumerator;
        ThrowIfFailed(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device));
        _device = device;
        var audioClientId = typeof(IAudioClient).GUID;
        ThrowIfFailed(device.Activate(ref audioClientId, 23, IntPtr.Zero, out var clientObject));
        _client = (IAudioClient)clientObject;
        var wave = new WaveFormatEx
        {
            FormatTag = 3,
            Channels = checked((ushort)format.Channels),
            SamplesPerSec = checked((uint)format.SampleRate),
            BitsPerSample = 32,
            BlockAlign = checked((ushort)(format.Channels * sizeof(float))),
            AvgBytesPerSec = checked((uint)(format.SampleRate * format.Channels * sizeof(float))),
            Size = 0,
        };
        ThrowIfFailed(_client.Initialize(AudioClientShareMode.Shared, StreamFlags, 20_000_000, 0, ref wave, IntPtr.Zero));
        ThrowIfFailed(_client.GetBufferSize(out _bufferFrames));
        var renderId = typeof(IAudioRenderClient).GUID;
        ThrowIfFailed(_client.GetService(ref renderId, out var renderObject));
        _render = (IAudioRenderClient)renderObject;
        var clockId = typeof(IAudioClock).GUID;
        ThrowIfFailed(_client.GetService(ref clockId, out var clockObject));
        _clock = (IAudioClock)clockObject;
        ThrowIfFailed(_clock.GetFrequency(out _clockFrequency));
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        _startRequested = true;
        if (!_running && _client is not null && Interlocked.Read(ref _writtenFrames) > 0)
        {
            ThrowIfFailed(_client.Start());
            _running = true;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<float> interleavedSamples, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("WASAPI is not initialized.");
        var render = _render!;
        float[] source;
        var sourceOffset = 0;
        if (MemoryMarshal.TryGetArray(interleavedSamples, out var segment) && segment.Array is not null)
        {
            source = segment.Array;
            sourceOffset = segment.Offset;
        }
        else
        {
            source = interleavedSamples.ToArray();
        }
        var frameOffset = 0;
        var totalFrames = interleavedSamples.Length / _format.Channels;
        while (frameOffset < totalFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_paused) { Thread.Sleep(5); continue; }
            ThrowIfFailed(client.GetCurrentPadding(out var padding));
            var writable = (int)(_bufferFrames - padding);
            if (writable <= 0)
            {
                if (!_running && Interlocked.Read(ref _writtenFrames) > 0) { ThrowIfFailed(client.Start()); _running = true; }
                Thread.Sleep(2);
                continue;
            }
            var frames = Math.Min(writable, totalFrames - frameOffset);
            ThrowIfFailed(render.GetBuffer((uint)frames, out var target));
            try { Marshal.Copy(source, sourceOffset + frameOffset * _format.Channels, target, frames * _format.Channels); }
            finally { ThrowIfFailed(render.ReleaseBuffer((uint)frames, 0)); }
            frameOffset += frames;
            Interlocked.Add(ref _writtenFrames, frames);
            if (_startRequested && !_running) { ThrowIfFailed(client.Start()); _running = true; }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken)
    {
        _paused = true;
        if (_running) ThrowIfFailed(_client!.Stop());
        _running = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAsync(CancellationToken cancellationToken)
    {
        _paused = false;
        if (!_running && _client is not null && Interlocked.Read(ref _writtenFrames) > 0) { ThrowIfFailed(_client.Start()); _running = true; }
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            if (_running) ThrowIfFailed(_client.Stop());
            ThrowIfFailed(_client.Reset());
        }
        _running = false;
        Interlocked.Exchange(ref _writtenFrames, 0);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            try { _client.Stop(); } catch { }
            try { _client.Reset(); } catch { }
        }
        _running = false;
        _paused = false;
        _startRequested = false;
        return ValueTask.CompletedTask;
    }

    public PreviewAudioSinkClock GetClock()
    {
        long played = 0;
        if (_clock is not null && _clockFrequency > 0 && _clock.GetPosition(out var position, out _) >= 0)
            played = checked((long)(position * (ulong)_format.SampleRate / _clockFrequency));
        return new(played, Math.Max(0, Interlocked.Read(ref _writtenFrames) - played), _running, _paused);
    }

    public ValueTask DisposeAsync()
    {
        StopAsync(CancellationToken.None);
        Release(_clock);
        Release(_render);
        Release(_client);
        Release(_device);
        Release(_enumerator);
        _clock = null;
        _render = null;
        _client = null;
        _device = null;
        _enumerator = null;
        return ValueTask.CompletedTask;
    }

    private static void ThrowIfFailed(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    private enum EDataFlow { Render, Capture, All }
    private enum ERole { Console, Multimedia, Communications }
    private enum AudioClientShareMode { Shared, Exclusive }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out object devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint classContext, IntPtr activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(AudioClientShareMode shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(AudioClientShareMode shareMode, ref WaveFormatEx format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClock
    {
        [PreserveSig] int GetFrequency(out ulong frequency);
        [PreserveSig] int GetPosition(out ulong position, out ulong qpcPosition);
        [PreserveSig] int GetCharacteristics(out uint characteristics);
    }
}
