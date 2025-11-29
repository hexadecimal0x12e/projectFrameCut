using Android.Content;
using Android.Opengl;
using Android.Util;
using Android.Views;
using Java.Nio;
using Javax.Microedition.Khronos.Opengles;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using projectFrameCut.Shared;
using System;
using System.Linq;
using System.Threading.Tasks;
using MauiGraphics = Microsoft.Maui.Graphics;

namespace projectFrameCut.Render.AndroidOpenGL.Platforms.Android
{
    public class GLComputeView : GLSurfaceView, GLSurfaceView.IRenderer
    {
        string TAG = "GLComputeView[???]";
        int WORKGROUP_SIZE = 256;

        private int program = 0;
        private readonly int[] inputBuffers = new int[6];
        private int outputBuffer = 0;
        private float[][] hostInputs;
        private int length;
        private bool initialized = false;
        private string shaderSrc;

        private TaskCompletionSource<float[]>? _tcs;
        private TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GLComputeView(Context context, string glSource, params float[][] inputs) : base(context)
        {
            if (inputs == null || inputs.Length == 0 || inputs.Length > 6)
                throw new ArgumentOutOfRangeException("Must input 1-6 array(s).", nameof(inputs));
            if (inputs.Any(arr => arr.Length != inputs[0].Length))
                throw new InvalidDataException("All input arrays must have same length");
            if (string.IsNullOrWhiteSpace(glSource))
                throw new NullReferenceException("glSource can't be null or whitespace.");

            shaderSrc = glSource;
            hostInputs = inputs;
            length = inputs[0].Length;

            SetEGLContextClientVersion(3);
            SetRenderer(this);
            RenderMode = Rendermode.WhenDirty;
            PreserveEGLContextOnPause = true;
        }

        public Task WaitUntilReadyAsync() => _readyTcs.Task;

        public void OnSurfaceCreated(IGL10 gl, Javax.Microedition.Khronos.Egl.EGLConfig config)
        {
            try
            {
                InitCompute();
                initialized = true;
                _readyTcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _readyTcs.TrySetException(ex);
                throw;
            }
        }

        public void OnSurfaceChanged(IGL10 gl, int width, int height) { }

        public override void SurfaceDestroyed(global::Android.Views.ISurfaceHolder holder)
        {
            initialized = false;
            _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                QueueEvent(DeleteGlResources);
            }
            catch
            {

            }

            program = 0;
            for (int i = 0; i < inputBuffers.Length; i++) inputBuffers[i] = 0;
            outputBuffer = 0;

            base.SurfaceDestroyed(holder);
        }

        public void OnDrawFrame(IGL10 gl)
        {
            if (!initialized || _tcs == null) return;

            try
            {
                if (program == 0)
                {
                    InitCompute();
                    if (!initialized) return;
                }

                if (length <= 0)
                {
                    _tcs.TrySetResult(Array.Empty<float>());
                    return;
                }

                var result = RunComputeAndReadback();
                _tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
            finally
            {
                _tcs = null;
            }
        }


        public Task<float[]> RunComputeAsync()
        {
            if (!initialized)
                throw new InvalidOperationException("GL not initialized yet");

            _tcs = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            RequestRender();
            return _tcs.Task;
        }

        private void InitCompute()
        {
            try
            {
                Logger.LogDiagnostic($"[{TAG}] GL_VERSION: {GLES31.GlGetString(GLES31.GlVersion)}");
                Logger.LogDiagnostic($"[{TAG}] [{TAG}] GL_SHADING_LANGUAGE_VERSION: {GLES31.GlGetString(GLES31.GlShadingLanguageVersion)}");

                if (length <= 0)
                {
                    initialized = true;
                    return;
                }

                DeleteGlResources();

                int shader = GLES31.GlCreateShader(GLES31.GlComputeShader);
                if (shader == 0)
                {
                    throw new Exception("Failed to create shader");
                }

                Logger.LogDiagnostic($"[{TAG}] Compiling shader:\n{shaderSrc}");

                GLES31.GlShaderSource(shader, shaderSrc);
                GLES31.GlCompileShader(shader);

                int[] status = new int[1];
                GLES31.GlGetShaderiv(shader, GLES31.GlCompileStatus, status, 0);

                string shaderLog = GLES31.GlGetShaderInfoLog(shader);
                Logger.LogDiagnostic($"[{TAG}] Shader compilation log: {shaderLog}");

                if (status[0] == 0)
                {
                    GLES31.GlDeleteShader(shader);
                    throw new Exception($"[{TAG}] Shader compile error: {shaderLog}\nSource:\n{shaderSrc}");
                }

                program = GLES31.GlCreateProgram();
                if (program == 0)
                {
                    GLES31.GlDeleteShader(shader);
                    throw new Exception($"[{TAG}] Failed to create program");
                }

                GLES31.GlAttachShader(program, shader);
                GLES31.GlLinkProgram(program);

                GLES31.GlGetProgramiv(program, GLES31.GlLinkStatus, status, 0);
                if (status[0] == 0)
                {
                    string programLog = GLES31.GlGetProgramInfoLog(program);
                    GLES31.GlDeleteShader(shader);
                    GLES31.GlDeleteProgram(program);
                    program = 0;
                    throw new Exception($"[{TAG}] Program link error: {programLog}");
                }

                GLES31.GlDeleteShader(shader);

                for (int i = 0; i < hostInputs.Length; i++)
                {
                    inputBuffers[i] = CreateSSBOFromArray(hostInputs[i]);
                    GLES31.GlBindBufferBase(GLES31.GlShaderStorageBuffer, i, inputBuffers[i]);
                }

                outputBuffer = CreateEmptySSBO(length);
                GLES31.GlBindBufferBase(GLES31.GlShaderStorageBuffer, 6, outputBuffer);

                initialized = true;
            }
            catch (Exception ex)
            {
                //Log.Error(TAG, $"Error initializing compute shader: {ex}");
                Logger.Log(ex, $"[{TAG}] initializing compute shader", this);
                initialized = false;
                throw;
            }
        }

        private void DeleteGlResources()
        {
            try
            {
                if (program != 0)
                {
                    GLES31.GlDeleteProgram(program);
                    program = 0;
                }

                int nonZeroCount = 0;
                for (int i = 0; i < inputBuffers.Length; i++)
                {
                    if (inputBuffers[i] != 0) nonZeroCount++;
                }
                if (nonZeroCount > 0)
                {
                    var toDelete = new int[nonZeroCount];
                    int idx = 0;
                    for (int i = 0; i < inputBuffers.Length; i++)
                    {
                        if (inputBuffers[i] != 0)
                        {
                            toDelete[idx++] = inputBuffers[i];
                            inputBuffers[i] = 0;
                        }
                    }
                    GLES31.GlDeleteBuffers(toDelete.Length, toDelete, 0);
                }

                if (outputBuffer != 0)
                {
                    GLES31.GlDeleteBuffers(1, new[] { outputBuffer }, 0);
                    outputBuffer = 0;
                }
            }
            catch (Exception ex)
            {
                //Log.Warn(TAG, $"DeleteGlResources warning: {ex.Message}");
                Logger.Log(ex, $"[{TAG}] initializing compute shader", this);
            }
        }

        private int CreateSSBOFromArray(float[] data)
        {
            int[] buf = new int[1];
            GLES31.GlGenBuffers(1, buf, 0);
            int buffer = buf[0];
            GLES31.GlBindBuffer(GLES31.GlShaderStorageBuffer, buffer);

            int byteSize = data.Length * sizeof(float);

            GLES31.GlBufferData(GLES31.GlShaderStorageBuffer, byteSize, null, GLES31.GlStaticDraw);

            var mapped = GLES30.GlMapBufferRange(
                GLES31.GlShaderStorageBuffer,
                0,
                byteSize,
                GLES30.GlMapWriteBit | GLES30.GlMapInvalidateBufferBit
            );

            if (mapped == null)
                throw new Exception("MapBufferRange for write failed");

            try
            {
                var bb = (ByteBuffer)mapped;
                bb.Order(ByteOrder.NativeOrder());
                var fb = bb.AsFloatBuffer();
                fb.Put(data);
                fb.Position(0);
            }
            finally
            {
                GLES30.GlUnmapBuffer(GLES31.GlShaderStorageBuffer);
            }

            GLES31.GlBindBuffer(GLES31.GlShaderStorageBuffer, 0);
            return buffer;
        }

        private int CreateEmptySSBO(int lengthFloats)
        {
            int[] buf = new int[1];
            GLES31.GlGenBuffers(1, buf, 0);
            int buffer = buf[0];
            GLES31.GlBindBuffer(GLES31.GlShaderStorageBuffer, buffer);
            GLES31.GlBufferData(GLES31.GlShaderStorageBuffer, lengthFloats * sizeof(float), null, GLES31.GlDynamicCopy);
            GLES31.GlBindBuffer(GLES31.GlShaderStorageBuffer, 0);
            return buffer;
        }

        private float[] RunComputeAndReadback()
        {
            GLES31.GlUseProgram(program);
            int numGroups = (length + WORKGROUP_SIZE - 1) / WORKGROUP_SIZE;
            if (numGroups <= 0)
                return Array.Empty<float>();

            GLES31.GlDispatchCompute(numGroups, 1, 1);
            GLES31.GlMemoryBarrier(GLES31.GlShaderStorageBarrierBit | GLES31.GlBufferUpdateBarrierBit);

            GLES31.GlBindBuffer(GLES31.GlShaderStorageBuffer, outputBuffer);
            var mapped = GLES30.GlMapBufferRange(GLES31.GlShaderStorageBuffer, 0,
                length * sizeof(float), GLES30.GlMapReadBit);
            if (mapped == null)
                throw new Exception("MapBufferRange failed");

            var bb = (ByteBuffer)mapped;
            bb.Order(ByteOrder.NativeOrder());
            var fb = bb.AsFloatBuffer();
            float[] result = new float[length];
            fb.Get(result);

            GLES30.GlUnmapBuffer(GLES31.GlShaderStorageBuffer);
            GLES31.GlBindBuffer(GLES31.GlShaderStorageBuffer, 0);
            return result;
        }

        public void UpdateInputs(float[][] inputs, string src, string jobId, int worksize)
        {
            QueueEvent(() =>
            {
                TAG = $"GLComputeView[{(jobId ?? "???")}]";
                WORKGROUP_SIZE = worksize > 0 ? worksize : 256;
                hostInputs = inputs;
                length = hostInputs.Length > 0 ? hostInputs[0].Length : 0;
                shaderSrc = src;

                if (!initialized)
                {
                    return;
                }

                DeleteGlResources();

                InitCompute();
            });
        }
    }

    public class NativeGLSurfaceView : Microsoft.Maui.Controls.View
    {
        public static readonly BindableProperty InputsProperty =
            BindableProperty.Create(nameof(Inputs), typeof(float[][]), typeof(NativeGLSurfaceView), null);

        public float[][]? Inputs
        {
            get => (float[][]?)GetValue(InputsProperty);
            set => SetValue(InputsProperty, value);
        }

        public static readonly BindableProperty ShaderSourceProperty =
            BindableProperty.Create(nameof(ShaderSource), typeof(string), typeof(NativeGLSurfaceView), string.Empty);      

        public string ShaderSource
        {
            get => (string)GetValue(ShaderSourceProperty);
            set => SetValue(ShaderSourceProperty, value);
        }

        public static readonly BindableProperty JobIDProperty =
            BindableProperty.Create(nameof(JobID), typeof(string), typeof(NativeGLSurfaceView), "???");  
        public string JobID
        {
            get => (string)GetValue(JobIDProperty);
            set => SetValue(JobIDProperty, value);
        }

        public static readonly BindableProperty WorkGroupSizeProperty =
            BindableProperty.Create(nameof(WorkGroupSize), typeof(int), typeof(NativeGLSurfaceView), 256);

        public int WorkGroupSize
        {
            get => (int)GetValue(WorkGroupSizeProperty);
            set => SetValue(WorkGroupSizeProperty, value);
        }
    }

    public class NativeGLSurfaceViewHandler : ViewHandler<NativeGLSurfaceView, GLComputeView>
    {
        public static void MapInputs(NativeGLSurfaceViewHandler handler, NativeGLSurfaceView view)
        {
            if (handler.PlatformView != null )
            {
                if (string.IsNullOrWhiteSpace(view.ShaderSource))
                    throw new NullReferenceException("glSource can't be null or whitespace.");
                if (view.Inputs == null || view.Inputs.Length == 0 || view.Inputs.Length > 6)
                    throw new ArgumentOutOfRangeException("Must input 1-6 array(s).", nameof(view.Inputs));

                handler.PlatformView.UpdateInputs(view.Inputs, view.ShaderSource, view.JobID, view.WorkGroupSize);
            }
        }

        public static IPropertyMapper<NativeGLSurfaceView, NativeGLSurfaceViewHandler> PropertyMapper = new PropertyMapper<NativeGLSurfaceView, NativeGLSurfaceViewHandler>(ViewHandler.ViewMapper)
        {
            [nameof(NativeGLSurfaceView.Inputs)] = MapInputs,
            [nameof(NativeGLSurfaceView.ShaderSource)] = MapInputs,
            [nameof(NativeGLSurfaceView.JobID)] = MapInputs,
            [nameof(NativeGLSurfaceView.WorkGroupSize)] = MapInputs,
        };

        public NativeGLSurfaceViewHandler() : base(PropertyMapper)
        {
        }

        protected override GLComputeView CreatePlatformView()
        {
            var inputs = VirtualView?.Inputs ?? new float[][] { new float[1] };
            var src = VirtualView?.ShaderSource ?? string.Empty;
            return new GLComputeView(Context, src, inputs);
        }

        protected override void DisconnectHandler(GLComputeView platformView)
        {
            base.DisconnectHandler(platformView);
        }
    }
}
