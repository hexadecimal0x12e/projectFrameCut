using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel; 
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;

namespace projectFrameCut;

public partial class TestPage : ContentPage
{
    public TestPage()
    {
        InitializeComponent();
    }

    private projectFrameCut.Shared.Picture srcA, srcB;

    private async void OpenGLESStartButton_Clicked(object sender, EventArgs e)
    {
#if ANDROID
        try
        {
            OpenGLESStartButton.IsEnabled = false;
            DeviceDisplay.Current.KeepScreenOn = true;
            await Task.Delay(500); // 确保UI更新

            Task.WaitAll([
                Task.Run(() => 
                {
                    srcA = new projectFrameCut.Shared.Picture("/storage/emulated/0/Android/data/com.hexadecimal0x12e.projectframecut/files/@Original_track_a.png");
                }),
                Task.Run(() => 
                {
                    srcB = new projectFrameCut.Shared.Picture("/storage/emulated/0/Android/data/com.hexadecimal0x12e.projectframecut/files/@Original_track_b.png");
                })
            ]);


            ushort[] uOutR = Array.Empty<ushort>(), uOutG = Array.Empty<ushort>(), uOutB = Array.Empty<ushort>();
            Task RConvertor, GConvertor, BConvertor;
            float[] outA = Array.Empty<float>();
            {

                var tcsA = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);

                var alphaGlView = new projectFrameCut.Render.AndroidOpenGL.Platforms.Android.NativeGLSurfaceView()
                {
                    ShaderSource = ShaderAlphaSrc,
                    Inputs = new float[][]
                    {
                    srcA.a,
                    srcB.a
                    },
                    HeightRequest = 120, // 确保有非零高度创建Surface
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Start
                };

                // 当平台视图就绪且尺寸有效时再触发计算
                alphaGlView.HandlerChanged += async (s, e2) =>
                {
                    try
                    {
                        if (alphaGlView.Handler is projectFrameCut.Render.AndroidOpenGL.Platforms.Android.NativeGLSurfaceViewHandler handler)
                        {
                            var platformView = handler.PlatformView;
                            if (platformView != null)
                            {
                                await platformView.WaitUntilReadyAsync();

                                // 若仍为零大小，等待一次尺寸变化（确保在主线程订阅/读取）
                                if (alphaGlView.Width <= 0 || alphaGlView.Height <= 0)
                                {
                                    var tcsSize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                                    await MainThread.InvokeOnMainThreadAsync(() =>
                                    {
                                        if (alphaGlView.Width > 0 && alphaGlView.Height > 0)
                                        {
                                            tcsSize.TrySetResult();
                                            return;
                                        }
                                        void OnSizeChanged(object? _, EventArgs __)
                                        {
                                            if (alphaGlView.Width > 0 && alphaGlView.Height > 0)
                                            {
                                                alphaGlView.SizeChanged -= OnSizeChanged;
                                                tcsSize.TrySetResult();
                                            }
                                        }
                                        alphaGlView.SizeChanged += OnSizeChanged;
                                    });
                                    await tcsSize.Task;
                                }

                                var res = await platformView.RunComputeAsync();
                                tcsA.TrySetResult(res);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"{ex.GetType().Name} Error during alpha computation: {ex.Message}");
                        tcsA.TrySetException(ex);
                    }
                };

                ComputeView.Children.Clear();
                ComputeView.Add(alphaGlView);

                outA = await tcsA.Task; // 保持在UI线程继续
                Debug.WriteLine($"Alpha computation completed, avg :{outA.Average()} first 5 distincted result:{string.Join(',', outA.Distinct().Take(5))}");

            } //A

            float[] outR = Array.Empty<float>();

            {
                var tcsR = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);

                var RGlView = new projectFrameCut.Render.AndroidOpenGL.Platforms.Android.NativeGLSurfaceView()
                {
                    ShaderSource = ShaderColorSrc,
                    Inputs = new float[][]
                    {
                        srcA.a,
                        srcA.r.Select(Convert.ToSingle).ToArray(),
                        srcB.a,
                        srcB.r.Select(Convert.ToSingle).ToArray()
                    },
                    HeightRequest = 120, // 确保有非零高度创建Surface
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Start
                };

                // 当平台视图就绪且尺寸有效时再触发计算
                RGlView.HandlerChanged += async (s, e2) =>
                {
                    try
                    {
                        if (RGlView.Handler is projectFrameCut.Render.AndroidOpenGL.Platforms.Android.NativeGLSurfaceViewHandler handler)
                        {
                            var platformView = handler.PlatformView;
                            if (platformView != null)
                            {
                                await platformView.WaitUntilReadyAsync();

                                // 若仍为零大小，等待一次尺寸变化（确保在主线程订阅/读取）
                                if (RGlView.Width <= 0 || RGlView.Height <= 0)
                                {
                                    var tcsSize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                                    await MainThread.InvokeOnMainThreadAsync(() =>
                                    {
                                        if (RGlView.Width > 0 && RGlView.Height > 0)
                                        {
                                            tcsSize.TrySetResult();
                                            return;
                                        }
                                        void OnSizeChanged(object? _, EventArgs __)
                                        {
                                            if (RGlView.Width > 0 && RGlView.Height > 0)
                                            {
                                                RGlView.SizeChanged -= OnSizeChanged;
                                                tcsSize.TrySetResult();
                                            }
                                        }
                                        RGlView.SizeChanged += OnSizeChanged;
                                    });
                                    await tcsSize.Task;
                                }

                                var res = await platformView.RunComputeAsync();
                                tcsR.TrySetResult(res);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"{ex.GetType().Name} Error during alpha computation: {ex.Message}");
                        tcsR.TrySetException(ex);
                    }
                };

                ComputeView.Children.Clear();
                ComputeView.Add(RGlView);

                outR = await tcsR.Task; // 保持在UI线程继续
                RConvertor = new(() =>
                {
                    uOutR = outR.Select(Convert.ToUInt16).ToArray();
                });
                RConvertor.Start();
                Debug.WriteLine($"Red computation completed, avg :{outR.Average()} first 5 distincted result:{string.Join(',', outR.Distinct().Take(5))}");

            } //R


            float[] outG = Array.Empty<float>();

            {
                var tcsG = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);

                var GGlView = new projectFrameCut.Render.AndroidOpenGL.Platforms.Android.NativeGLSurfaceView()
                {
                    ShaderSource = ShaderColorSrc,
                    Inputs = new float[][]
                    {
                        srcA.a,
                        srcA.g.Select(Convert.ToSingle).ToArray(),
                        srcB.a,
                        srcB.g.Select(Convert.ToSingle).ToArray()
                    },
                    HeightRequest = 120, // 确保有非零高度创建Surface
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Start
                };

                // 当平台视图就绪且尺寸有效时再触发计算
                GGlView.HandlerChanged += async (s, e2) =>
                {
                    try
                    {
                        if (GGlView.Handler is projectFrameCut.Render.AndroidOpenGL.Platforms.Android.NativeGLSurfaceViewHandler handler)
                        {
                            var platformView = handler.PlatformView;
                            if (platformView != null)
                            {
                                await platformView.WaitUntilReadyAsync();

                                // 若仍为零大小，等待一次尺寸变化（确保在主线程订阅/读取）
                                if (GGlView.Width <= 0 || GGlView.Height <= 0)
                                {
                                    var tcsSize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                                    await MainThread.InvokeOnMainThreadAsync(() =>
                                    {
                                        if (GGlView.Width > 0 && GGlView.Height > 0)
                                        {
                                            tcsSize.TrySetResult();
                                            return;
                                        }
                                        void OnSizeChanged(object? _, EventArgs __)
                                        {
                                            if (GGlView.Width > 0 && GGlView.Height > 0)
                                            {
                                                GGlView.SizeChanged -= OnSizeChanged;
                                                tcsSize.TrySetResult();
                                            }
                                        }
                                        GGlView.SizeChanged += OnSizeChanged;
                                    });
                                    await tcsSize.Task;
                                }

                                var res = await platformView.RunComputeAsync();
                                tcsG.TrySetResult(res);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"{ex.GetType().Name} Error during alpha computation: {ex.Message}");
                        tcsG.TrySetException(ex);
                    }
                };

                ComputeView.Children.Clear();
                ComputeView.Add(GGlView);

                outG = await tcsG.Task; // 保持在UI线程继续
                GConvertor = new(() =>
                {
                    uOutG = outG.Select(Convert.ToUInt16).ToArray();
                });
                GConvertor.Start();
                Debug.WriteLine($"Green computation completed, avg :{outG.Average()} first 5 distincted result:{string.Join(',', outG.Distinct().Take(5))}");

            } //G

            float[] outB = Array.Empty<float>();

            {
                var tcsB = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);

                var BGLView = new projectFrameCut.Render.AndroidOpenGL.Platforms.Android.NativeGLSurfaceView()
                {
                    ShaderSource = ShaderColorSrc,
                    Inputs = new float[][]
                    {
                        srcA.a,
                        srcA.b.Select(Convert.ToSingle).ToArray(),
                        srcB.a,
                        srcB.b.Select(Convert.ToSingle).ToArray()
                    },
                    HeightRequest = 120, // 确保有非零高度创建Surface
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Start,
                    JobID = "Blue"
                };

                // 当平台视图就绪且尺寸有效时再触发计算
                BGLView.HandlerChanged += async (s, e2) =>
                {
                    try
                    {
                        if (BGLView.Handler is projectFrameCut.Render.AndroidOpenGL.Platforms.Android.NativeGLSurfaceViewHandler handler)
                        {
                            var platformView = handler.PlatformView;
                            if (platformView != null)
                            {
                                await platformView.WaitUntilReadyAsync();

                                // 若仍为零大小，等待一次尺寸变化（确保在主线程订阅/读取）
                                if (BGLView.Width <= 0 || BGLView.Height <= 0)
                                {
                                    var tcsSize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                                    await MainThread.InvokeOnMainThreadAsync(() =>
                                    {
                                        if (BGLView.Width > 0 && BGLView.Height > 0)
                                        {
                                            tcsSize.TrySetResult();
                                            return;
                                        }
                                        void OnSizeChanged(object? _, EventArgs __)
                                        {
                                            if (BGLView.Width > 0 && BGLView.Height > 0)
                                            {
                                                BGLView.SizeChanged -= OnSizeChanged;
                                                tcsSize.TrySetResult();
                                            }
                                        }
                                        BGLView.SizeChanged += OnSizeChanged;
                                    });
                                    await tcsSize.Task;
                                }

                                var res = await platformView.RunComputeAsync();
                                tcsB.TrySetResult(res);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"{ex.GetType().Name} Error during alpha computation: {ex.Message}");
                        tcsB.TrySetException(ex);
                    }
                };

                ComputeView.Children.Clear();
                ComputeView.Add(BGLView);

                outB = await tcsB.Task; // 保持在UI线程继续
                BConvertor = new(() =>
                {
                    uOutB = outB.Select(Convert.ToUInt16).ToArray();
                });
                BConvertor.Start();
                Debug.WriteLine($"Blue computation completed, avg :{outB.Average()} first 5 distincted result:{string.Join(',', outB.Distinct().Take(5))}");

            } //B
            Debug.WriteLine("Waiting for convertor done...");
            Task.WaitAll(RConvertor, GConvertor, BConvertor);
            Debug.WriteLine("Writing result...");
            var outPic = new projectFrameCut.Shared.Picture(srcA.Width, srcA.Height)
            {
                r = uOutR,
                g = uOutG,
                b = uOutB,
                a = outA,
                hasAlphaChannel = true
            };

            var path = $"/storage/emulated/0/Android/data/com.hexadecimal0x12e.projectframecut/files/out-{DateTime.Now:yyyy-MM-dd-hh-mm-ss}.png";
            outPic.SaveAsPng16bpc(path);

            ResultImage.Source = ImageSource.FromFile(path);


            //MemoryStream ms = new();

            //outPic.SaveAsPng8bpc(ms);

            //using (var fs = new FileStream(, FileMode.Create, FileAccess.ReadWrite))
            //{
            //    ms.CopyTo(fs);
            //}

            ////outPic.SaveAsPng16bpc("/storage/emulated/0/Android/data/com.hexadecimal0x12e.projectframecut/files/1.png", new PngEncoder());

            //ResultImage.Source = ImageSource.FromStream(() => ms);

            Debug.WriteLine("Image seted");



        }
        finally
        {
            OpenGLESStartButton.IsEnabled = true;
            DeviceDisplay.Current.KeepScreenOn = false;
        }
#else
        await DisplayAlert("提示", "此功能目前仅在 Android 上可用。", "确定");
#endif
    }

    public const string ShaderAlphaSrc =
        $$"""
        {{"#"}}version 310 es            
        layout(local_size_x = 256) in;

        // 输入：a, aAlpha, b, bAlpha
        layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
        layout(std430, binding = 1) buffer BAlphaBuffer { float bAlpha[]; };

        layout(std430, binding = 6) buffer CAlphaBuffer { float cAlpha[]; };

        void main() {
            uint i = gl_GlobalInvocationID.x;

            float aA = aAlpha[i];
            float bA = bAlpha[i];

            if (aA == 1.0) {
                cAlpha[i] = 1.0;
            } else if (aA <= 0.05) {
                cAlpha[i] = bA;
            } else {
                float outA = aA + bA * (1.0 - aA);
                if (outA < 1e-6) {
                    cAlpha[i] = 0.0;
                } else {
                    cAlpha[i] = outA;
                }
            }
        }
        """;

    private void ILGPURenderStartButton_Clicked(object sender, EventArgs e)
    {

    }

    private void MetalRenderStartButton_Clicked(object sender, EventArgs e)
    {

    }

    private void CPURenderStartButton_Clicked(object sender, EventArgs e)
    {

    }


    public const string ShaderColorSrc =
        $$"""
        {{"#"}}version 310 es            
        layout(local_size_x = 256) in;

        layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
        layout(std430, binding = 1) buffer ABuffer { float a []; };
        
        layout(std430, binding = 2) buffer BAlphaBuffer { float bAlpha []; };
        layout(std430, binding = 3) buffer BBuffer { float b []; };
        
        layout(std430, binding = 6) buffer CAlphaBuffer { float c []; };

        void main()
        {
            uint i = gl_GlobalInvocationID.x;

            float aA = aAlpha[i];
            float bA = bAlpha[i];

            if (aA == 1.0)
            {
                c[i] = a[i];
            }
            else if (aA <= 0.05)
            {
                c[i] = b[i];
            }
            else
            {
                float outA = aA + bA * (1.0 - aA);
                if (outA < 1e-6)
                {
                    c[i] = 0.0;
                }
                else
                {
                    float aC = a[i] * aA / outA;
                    float bC = b[i] * bA * (1.0 - aA) / outA;
                    float outC = aC + bC;
                    outC = clamp(outC, 0.0, 65535.0); // ushort.MaxValue
                    c[i] = outC;
                }
            }
        }
        """;


}
