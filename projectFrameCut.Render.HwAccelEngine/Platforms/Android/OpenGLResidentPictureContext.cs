#if ANDROID
using Android.Opengl;
using Java.Nio;
using projectFrameCut.Render.HwAccelEngine.Effect;
using System.Collections.Concurrent;
using EGLDisplay = Android.Opengl.EGLDisplay;
using EGLContext = Android.Opengl.EGLContext;
using EGLSurface = Android.Opengl.EGLSurface;

namespace projectFrameCut.Render.HwAccelEngine.Platforms.Android;
/// <summary>All EGL and buffer operations run on the context's owning thread.</summary>
internal sealed class OpenGLResidentPictureContext : ResidentPictureContext
{
    private static readonly Lazy<EGLDisplay> sharedDisplay = new(() =>
    {
        var display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
        if (!EGL14.EglInitialize(display, new int[1], 0, new int[1], 0))
            throw new InvalidOperationException("Could not initialize EGL.");
        return display;
    });
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread worker;
    private EGLDisplay display = null !;
    private EGLContext context = null !;
    private EGLSurface surface = null !;
    private int program;
    private sealed class Buffer(OpenGLResidentPictureContext owner, int id, int length) : ResidentBuffer
    {
        internal readonly int Id = id;
        internal override int Length => length;
        internal override void Destroy() => owner.Invoke(() =>
        {
            GLES31.GlDeleteBuffers(1, new[] { Id }, 0);
            return true;
        });
    }

    internal OpenGLResidentPictureContext()
    {
        worker = new Thread(() =>
        {
            foreach (var action in queue.GetConsumingEnumerable())
                action();
        })
        {
            IsBackground = true,
            Name = "GPU picture EGL"
        };
        worker.Start();
        try
        {
            Invoke(() =>
            {
                Initialize();
                return true;
            });
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private T Invoke<T>(Func<T> action)
    {
        if (Thread.CurrentThread == worker)
            return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    private void Initialize()
    {
        display = sharedDisplay.Value;
        var configs = new EGLConfig[1];
        var count = new int[1];
        if (!EGL14.EglChooseConfig(display, new[] { EGL14.EglRenderableType, 0x40, EGL14.EglSurfaceType, EGL14.EglPbufferBit, EGL14.EglNone }, 0, configs, 0, 1, count, 0) || count[0] == 0)
            throw new NotSupportedException("OpenGL ES 3 compute is unavailable.");
        context = EGL14.EglCreateContext(display, configs[0], EGL14.EglNoContext, new[] { EGL14.EglContextClientVersion, 3, EGL14.EglNone }, 0);
        surface = EGL14.EglCreatePbufferSurface(display, configs[0], new[] { EGL14.EglWidth, 1, EGL14.EglHeight, 1, EGL14.EglNone }, 0);
        if (!EGL14.EglMakeCurrent(display, surface, surface, context))
            throw new InvalidOperationException("Could not bind EGL context.");
        int shader = GLES31.GlCreateShader(GLES31.GlComputeShader);
        try
        {
            GLES31.GlShaderSource(shader, ResidentShaders.OpenGL);
            GLES31.GlCompileShader(shader);
            var status = new int[1];
            GLES31.GlGetShaderiv(shader, GLES31.GlCompileStatus, status, 0);
            if (status[0] == 0)
                throw new InvalidOperationException(GLES31.GlGetShaderInfoLog(shader));
            program = GLES31.GlCreateProgram();
            GLES31.GlAttachShader(program, shader);
            GLES31.GlLinkProgram(program);
            GLES31.GlGetProgramiv(program, GLES31.GlLinkStatus, status, 0);
            if (status[0] == 0)
                throw new InvalidOperationException(GLES31.GlGetProgramInfoLog(program));
        }
        finally
        {
            GLES31.GlDeleteShader(shader);
        }
    }

    private Buffer AllocateBuffer(int length, float[]? values = null)
    {
        var ids = new int[1];
        GLES31.GlGenBuffers(1, ids, 0);
        GLES31.GlBindBuffer(GLES31.GlShaderStorageBuffer, ids[0]);
        using var bytes = values is null ? null : ByteBuffer.AllocateDirect(checked(length * sizeof(float))).Order(ByteOrder.NativeOrder());
        using var floats = bytes?.AsFloatBuffer();
        floats?.Put(values!);
        floats?.Position(0);
        GLES31.GlBufferData(GLES31.GlShaderStorageBuffer, checked(length * sizeof(float)), floats, GLES31.GlDynamicCopy);
        Check();
        return new(this, ids[0], length);
    }

    private static void Check()
    {
        int error = GLES31.GlGetError();
        if (error != GLES31.GlNoError)
            throw new InvalidOperationException($"OpenGL image operation failed: 0x{error:X}.");
    }

    protected override ResidentBuffer CreateBuffer(int length) => Invoke(() => AllocateBuffer(length));
    protected override void WriteBuffer(ResidentBuffer buffer, float[] values) => Invoke(() =>
    {
        using var bytes = ByteBuffer.AllocateDirect(checked(values.Length * sizeof(float))).Order(ByteOrder.NativeOrder());
        using var floats = bytes.AsFloatBuffer();
        floats.Put(values);
        floats.Position(0);
        GLES31.GlBindBuffer(GLES31.GlShaderStorageBuffer, ((Buffer)buffer).Id);
        GLES31.GlBufferSubData(GLES31.GlShaderStorageBuffer, 0, checked(values.Length * sizeof(float)), floats);
        Check();
        return true;
    });
    protected override float[] Read(ResidentBuffer buffer) => Invoke(() =>
    {
        var b = (Buffer)buffer;
        GLES31.GlBindBuffer(GLES31.GlShaderStorageBuffer, b.Id);
        var mapped = GLES31.GlMapBufferRange(GLES31.GlShaderStorageBuffer, 0, checked(b.Length * sizeof(float)), GLES31.GlMapReadBit) as ByteBuffer ?? throw new InvalidOperationException("Could not map GPU image.");
        try
        {
            mapped.Order(ByteOrder.NativeOrder());
            using var floats = mapped.AsFloatBuffer();
            var data = new float[b.Length];
            floats.Get(data);
            return data;
        }
        finally
        {
            GLES31.GlUnmapBuffer(GLES31.GlShaderStorageBuffer);
            mapped.Dispose();
        }
    });
    protected override ResidentBuffer Dispatch(ResidentBuffer source, ResidentBuffer second, float[] parameters, int count)
    {
        BeginSubmission();
        var output = RentBuffer(checked((int)parameters[3] * (int)parameters[4] * (parameters[14] > 0 ? 5 : 4)));
        try
        {
            using var p = UploadBuffer(parameters);
            Invoke(() =>
            {
                GLES31.GlUseProgram(program);
                GLES31.GlBindBufferBase(GLES31.GlShaderStorageBuffer, 0, ((Buffer)source).Id);
                GLES31.GlBindBufferBase(GLES31.GlShaderStorageBuffer, 1, ((Buffer)second).Id);
                GLES31.GlBindBufferBase(GLES31.GlShaderStorageBuffer, 2, ((Buffer)p).Id);
                GLES31.GlBindBufferBase(GLES31.GlShaderStorageBuffer, 3, ((Buffer)output).Id);
                GLES31.GlDispatchCompute((count + 255) / 256, 1, 1);
                GLES31.GlMemoryBarrier(GLES31.GlShaderStorageBarrierBit | GLES31.GlBufferUpdateBarrierBit);
                Check();
                return true;
            });
            return output;
        }
        catch { output.Dispose(); throw; }
    }
    protected override void Synchronize() => Invoke(() =>
    {
        GLES31.GlFinish();
        Check();
        return true;
    });
    protected override void Release()
    {
        try
        {
            Invoke(() =>
            {
                if (program != 0)
                    GLES31.GlDeleteProgram(program);
                if (display is not null && !display.Equals(EGL14.EglNoDisplay))
                {
                    EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
                    if (surface is not null && !surface.Equals(EGL14.EglNoSurface))
                    {
                        EGL14.EglDestroySurface(display, surface);
                        surface.Dispose();
                    }

                    if (context is not null && !context.Equals(EGL14.EglNoContext))
                    {
                        EGL14.EglDestroyContext(display, context);
                        context.Dispose();
                    }
                // The shared display also serves other frames and Android views.
                }

                return true;
            });
        }
        finally
        {
            queue.CompleteAdding();
            if (Thread.CurrentThread != worker)
                worker.Join();
            queue.Dispose();
        }
    }
}
#endif
