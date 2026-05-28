using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Devices;
using projectFrameCut.DraftStuff;

using projectFrameCut.Render.Benchmark;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using Path = System.IO.Path;
using Rectangle = Microsoft.Maui.Controls.Shapes.Rectangle;

using projectFrameCut.Render.Compose;
using DatePicker = Microsoft.Maui.Controls.DatePicker;
using projectFrameCut.APIClient;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using Color = Microsoft.Maui.Graphics.Color;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Render.ClipsAndTracks;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization.Metadata;
using System.Text;
using FFmpeg.AutoGen;





#if ANDROID
using projectFrameCut.Platforms.Android;

#endif

#if WINDOWS

#endif

namespace projectFrameCut;

public partial class TestPage : ContentPage
{
    public TestPage()
    {
        InitializeComponent();

        Loaded += TestPage_Loaded;

        TaskbarOptionPicker.ItemsSource = Enum.GetValues<TaskbarVisibilityMode>().Select(c => c.ToString()).ToList();

#if WINDOWS
        MultiWindowItem.ContextMenuProviderGetter = new(() => new WindowsContextMenuBuilder());

        ILGPU.Context context = ILGPU.Context.CreateDefault();
        var devices = context.Devices.ToList();
        List<AcceleratorInfo> listAccels = new();
        for (uint i = 0; i < devices.Count; i++)
        {
            var item = devices[(int)i];
            listAccels.Add(new AcceleratorInfo(i, item.Name, item.AcceleratorType.ToString()));
        }
        if (!int.TryParse(SettingsManager.GetSetting("accel_DeviceId", "-1"), out var result) || result < 0 || !(listAccels?.Any(c => c.index == result) ?? false))
        {
            var bestAccel = listAccels?.Select(c => (c, c.Type switch { "Cuda" => 10, "OpenCL" => 5, "CPU" => -10, _ => 1 })).OrderByDescending(c => c.Item2).ThenByDescending(c => c.c.name).FirstOrDefault();
            SettingsManager.WriteSetting("accel_DeviceId", (bestAccel?.c.index ?? 0).ToString());
            Log($"No accelerator defined yet; set to best one {bestAccel?.c.name} ({bestAccel?.c.Type}) by default.");
        }
        var accelDevice = devices.Index().Select(t => new KeyValuePair<int, ILGPU.Runtime.Device>(t.Index, t.Item))
                                .FirstOrDefault((t) => t.Key == (int.TryParse(SettingsManager.GetSetting("accel_DeviceId", "-1"), out var accelIdx) ? accelIdx : -1),
                                new KeyValuePair<int, ILGPU.Runtime.Device>(-1, devices.FirstOrDefault(c => c.AcceleratorType != ILGPU.Runtime.AcceleratorType.CPU, devices.First()))).Value;
        Render.WindowsRender.ILGPUPlugin.accelerators = [accelDevice.CreateAccelerator(context)];

#endif
        TextPicker.PreviewRenderer = TextServices.RenderFontPreviewAsync;

        TextClip.GetFont(true);
        _ = LoadFontPickerAsync();

        TextPicker.SelectedFontChanged += async (s, e) =>
        {
            await DisplayAlertAsync(Title, JsonSerializer.Serialize(e.InnerItem, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, }), "ok");

        };
    }


    private void TestPage_Loaded(object? sender, EventArgs e)
    {
        Border b = new Border
        {
            WidthRequest = 50,
            HeightRequest = 80,
            BackgroundColor = Colors.Yellow
        };

        PanGestureRecognizer g = new();

        g.PanUpdated += G_PanUpdated;

        b.GestureRecognizers.Add(g);

        DragTester.Children.Add(b);
    }

    #region pan gesture test

    private ConcurrentStack<double> DraggingX = new(), DenoisedX = new();
    private double _origX = 0;
    private DenoiseHelper denoise = new();

    private async void G_PanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        var b = sender as Border;
        if (b is null) return;
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                {
                    DraggingX = new();
                    DenoisedX = new();
                    denoise = new();
                    _origX = b.TranslationX;
                    DraggingTestLabel.Text = $"origX:{_origX}";
                    break;
                }
            case GestureStatus.Running:
                {
                    if (DenoiseOptionBox.IsChecked)
                    {
                        var noNoise = denoise.Process(e.TotalX);
                        b.TranslationX = noNoise + _origX;
                        //DraggingTestLabel.Text = $"Dragging X:{e.TotalX}, denoised: {noNoise + _origX}";
                        DraggingX.Push(e.TotalX);
                        DenoisedX.Push(noNoise);
                    }
                    else
                    {
                        //DraggingTestLabel.Text = $"Dragging X:{e.TotalX}";
                        b.TranslationX = e.TotalX + _origX;
                        DraggingX.Push(e.TotalX);
                        DenoisedX.Push(0);

                    }


                    break;
                }
            case GestureStatus.Canceled:
            case GestureStatus.Completed:
                {
                    var src = DraggingX.ToList();
                    var dn = DenoisedX.ToList();
                    src.Reverse();
                    List<double> delta = [src[0]];
                    for (int i = 1; i < src.Count; i++)
                    {
                        if (i + 1 >= src.Count) break;
                        delta.Add(src[i + 1] - src[i]);
                    }
                    List<double> denoiseDelta = [dn[0]];
                    for (int i = 1; i < src.Count; i++)
                    {
                        if (i + 1 >= dn.Count) break;
                        denoiseDelta.Add(dn[i + 1] - dn[i]);
                    }
                    //await DisplayAlert("Info", $"avg delta: {delta.Average()}", "ok");
                    var p = Path.Combine(FileSystem.CacheDirectory, $"dragtest-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.csv");
                    StreamWriter sw = new(p, append: false);
                    sw.WriteLine("i,PositionX,DenoisedX,DeltaX,DenoisedDeltaX");
                    for (int i = 0; i < delta.Count; i++)
                    {
                        sw.WriteLine($"{i},{src[i]},{dn[i]},{delta[i]},{denoiseDelta[i]}");
                    }
                    sw.Dispose();
                    await Share.RequestAsync(new ShareFileRequest
                    {
                        Title = "�����϶���������",
                        File = new ShareFile(p)
                    });
                    break;
                }
        }
    }


    #endregion

    #region openGL test
    private projectFrameCut.Shared.Picture16bpp srcA, srcB;

    private async void OpenGLESStartButton_Clicked(object sender, EventArgs e)
    {
#if ANDROID
        try
        {
            OpenGLESStartButton.IsEnabled = false;
            DeviceDisplay.Current.KeepScreenOn = true;
            await Task.Delay(500); // ȷ��UI����

            Task.WaitAll([
                Task.Run(() =>
                {
                    srcA = new projectFrameCut.Shared.Picture16bpp("/storage/emulated/0/Android/data/com.hexadecimal0x12e.projectframecut/files/@Original_track_a.png");
                }),
                Task.Run(() =>
                {
                    srcB = new projectFrameCut.Shared.Picture16bpp("/storage/emulated/0/Android/data/com.hexadecimal0x12e.projectframecut/files/@Original_track_b.png");
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
                    HeightRequest = 120, // ȷ���з���߶ȴ���Surface
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Start
                };

                // ��ƽ̨��ͼ�����ҳߴ���Чʱ�ٴ�������
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

                                // ����Ϊ���С���ȴ�һ�γߴ�仯��ȷ�������̶߳���/��ȡ��
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

                                var res = (float[])await platformView.RunComputeAsync(projectFrameCut.Render.AndroidOpenGL.Platforms.Android.GLComputeView.OutputElementType.Float32);
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

                outA = await tcsA.Task; // ������UI�̼߳���
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
                    HeightRequest = 120, // ȷ���з���߶ȴ���Surface
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Start
                };

                // ��ƽ̨��ͼ�����ҳߴ���Чʱ�ٴ�������
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

                                // ����Ϊ���С���ȴ�һ�γߴ�仯��ȷ�������̶߳���/��ȡ��
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

                                var res = (float[])await platformView.RunComputeAsync(projectFrameCut.Render.AndroidOpenGL.Platforms.Android.GLComputeView.OutputElementType.Float32);
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

                outR = await tcsR.Task; // ������UI�̼߳���
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
                    HeightRequest = 120, // ȷ���з���߶ȴ���Surface
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Start
                };

                // ��ƽ̨��ͼ�����ҳߴ���Чʱ�ٴ�������
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

                                // ����Ϊ���С���ȴ�һ�γߴ�仯��ȷ�������̶߳���/��ȡ��
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

                                var res = (float[])await platformView.RunComputeAsync(projectFrameCut.Render.AndroidOpenGL.Platforms.Android.GLComputeView.OutputElementType.Float32);
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

                outG = await tcsG.Task; // ������UI�̼߳���
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
                    HeightRequest = 120, // ȷ���з���߶ȴ���Surface
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Start,
                    JobID = "Blue"
                };

                // ��ƽ̨��ͼ�����ҳߴ���Чʱ�ٴ�������
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

                                // ����Ϊ���С���ȴ�һ�γߴ�仯��ȷ�������̶߳���/��ȡ��
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

                                var res = (float[])await platformView.RunComputeAsync(projectFrameCut.Render.AndroidOpenGL.Platforms.Android.GLComputeView.OutputElementType.Float32);
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

                outB = await tcsB.Task; // ������UI�̼߳���
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
            var outPic = new projectFrameCut.Shared.Picture16bpp(srcA.Width, srcA.Height)
            {
                r = uOutR,
                g = uOutG,
                b = uOutB,
                a = outA,
                hasAlphaChannel = true
            };

            var path = $"/storage/emulated/0/Android/data/com.hexadecimal0x12e.projectframecut/files/out-{DateTime.Now:yyyy-MM-dd-hh-mm-ss}.png";
            outPic.SaveAsPng16bpp(path);

            ResultImage.Source = ImageSource.FromFile(path);


            //MemoryStream ms = new();

            //outPic.SaveAsPng8bpp(ms);

            //using (var fs = new FileStream(, FileMode.Create, FileAccess.ReadWrite))
            //{
            //    ms.CopyTo(fs);
            //}

            ////outPic.SaveAsPng16bpp("/storage/emulated/0/Android/data/com.hexadecimal0x12e.projectframecut/files/1.png", new PngEncoder());

            //ResultImage.Source = ImageSource.FromStream(() => ms);

            Debug.WriteLine("Image seted");



        }
        finally
        {
            OpenGLESStartButton.IsEnabled = true;
            DeviceDisplay.Current.KeepScreenOn = false;
        }
#else
        await DisplayAlert("��ʾ", "�˹���Ŀǰ���� Android �Ͽ��á�", "ȷ��");
#endif
    }

    public string ShaderAlphaSrc =
        $$"""
        {{"#"}}version 310 es            
        layout(local_size_x = 256) in;

        // ���룺a, aAlpha, b, bAlpha
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

    public string ShaderColorSrc =
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

    #endregion

    #region render
    private async void HDRTestButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            var f = HDRPicture16bpp.GenerateSolidColor(2560, 1440, 32767, 32767, 32767, 1, 1, 10000);
            var w = new HDRVideoWriter
            {
                OutputPath = Path.Combine(FileSystem.CacheDirectory, $"hdrtest-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.mp4"),
                Width = 2560,
                Height = 1440,
                FramePerSecond = 30,
                CodecName = "libx265",
                PixelFormat = AVPixelFormat.AV_PIX_FMT_YUV420P10LE.ToString()
            };
            w.Initialize();
            TextClip c = new TextClip { Id = "1", Name = "1" };
            TextClipEntry te = new TextClipEntry
            {
                r = 65535,
                g = 65535,
                b = 65535,
                a = 65535,
                fontFamily = "Arial",
                x = 50,
                y = 50,
                fontSize = 120,
            };
            f.Brightness = new float[f.Pixels];
            for (int idx = 0; idx < f.Pixels; idx++)
            {
                int x = idx % f.Width;
                if (f.Width > 1)
                {
                    float center = (f.Width - 1) * 0.5f;
                    float distanceToCenter = MathF.Abs(x - center);
                    float normalized = center > 0 ? distanceToCenter / center : 1f;
                    // Center stays darkest; both sides brighten smoothly.
                    f.Brightness[idx] = MathF.Pow(normalized, 0.6f);
                }
                else
                {
                    f.Brightness[idx] = 1f;
                }
            }
            f.SaveAsPng16bpp(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"), null);

            for (int i = 0; i < 1; i++)
            {
                c.TextEntries = [te with { text = $"Frame {i}" }];
                var textFrame = c.GetFrameRelativeToStartPointOfSource(0U, 2560, 1440, false, 16);
                textFrame.SaveAsPng16bpp(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-textFrame-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"), null);
                var t = textFrame.ToHDRPicture(1, 5000);
                Log(t.GetDiagnosticsInfo());
                t.SaveAsPng16bpp(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-t-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"), null);
                var r = ClassicOverlayMixture.Default.Mix(f, t, PluginManager.CreateComputer(ClassicOverlayMixture.ComputerId), 16);
                r.SaveAsPng16bpp(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-r-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"), null);
                w.Append(r);
                Log($"Wrote frame {i}, r:{r.GetDiagnosticsInfo()}");
            }
            w.Finish();
        }
        catch (Exception ex)
        {
            if (await DisplayAlertAsync(Title, Localized._ExceptionTemplate(ex), "throw", "ok")) throw;
        }
    }

    private async void HDRTestButton2_Clicked(object sender, EventArgs e)
    {
        var f = HDRPicture16bpp.GenerateSolidColor(2560, 1440, 32767, 32767, 32767, 1, 1, 10000);
        f.Brightness = new float[f.Pixels];
        for (int idx = 0; idx < f.Pixels; idx++)
        {
            f.Brightness[idx] = Random.Shared.NextSingle();
        }
        var fThrowBrightness = new Picture16bpp(f)
        {
            r = f.r,
            g = f.g,
            b = f.b,
            a = f.a
        };
        fThrowBrightness.SaveAsPng16bpp(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-throwBrightness-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"));
        var fNormalizeBrigtnessToRGB = f.SaveToSixLaborsImage(16, true).ToPJFCPicture(16);
        fNormalizeBrigtnessToRGB.SaveAsPng16bpp(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-normalizeBrightnessToRGB-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"));
        var fReplaceAlpha = new Picture16bpp(f)
        {
            r = f.r,
            g = f.g,
            b = f.b,
            a = f.Brightness
        };
        fReplaceAlpha.SaveAsPng16bpp(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-replaceAlpha-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"));
        var fReplaceAlphaAndComposeMask = ClassicOverlayMixture.Default.Mix(fThrowBrightness, new Picture16bpp(f)
        {
            r = Enumerable.Repeat((ushort)0, f.Pixels).ToArray(),
            g = Enumerable.Repeat((ushort)0, f.Pixels).ToArray(),
            b = Enumerable.Repeat((ushort)0, f.Pixels).ToArray(),
            a = f.Brightness.Select(c => Math.Clamp(1 - c, 0, 1)).ToArray()
        }, PluginManager.CreateComputer(ClassicOverlayMixture.ComputerId), 16);
        fReplaceAlphaAndComposeMask.SaveAsPng16bpp(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-replaceAlphaAndComposeMask-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"));
        var w = new HDRVideoWriter
        {
            OutputPath = Path.Combine(FileSystem.CacheDirectory, $"hdrtest-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.mp4"),
            Width = 2560,
            Height = 1440,
            FramePerSecond = 30,
            CodecName = "libx265",
            PixelFormat = AVPixelFormat.AV_PIX_FMT_YUV420P10LE.ToString()
        };
        w.Initialize();
        w.Append(f);
        w.Finish();
    }

    private void TestPlaceButton_Clicked(object sender, EventArgs e)
    {
        Picture8bpp src = Picture8bpp.GenerateSolidColor(200, 300, 128, 128, 128, 1);
        PlaceEffect_HwAccel p = new()
        {
            StartX = 50,
            StartY = 120
        };
        var result = p.Render(src, null, 2560, 1440);
        PlaceResizeTestImage.Source = ImageSource.FromStream(() =>
        {
            MemoryStream ms = new();
            result.SaveToSixLaborsImage().SaveAsPng(ms);
            ms.Position = 0;
            return ms;
        });



    }

    private async void TestPlaceAndResizeButton_Clicked(object sender, EventArgs e)
    {
        Picture8bpp src = new Picture8bpp(await FileSystemService.PickFileAsync());
        PlaceEffect_HwAccel p = new()
        {
            StartX = 250,
            StartY = 180
        };
        ResizeEffect_ImageSharp r = new()
        {
            Height = 300,
            Width = 1000,
            PreserveAspectRatio = false
        };
        var resized = r.Render(src, null, 2560, 1440);
        var placed = p.Render(resized, null, 2560, 1440);
        Picture8bpp canvas = Picture8bpp.GenerateSolidColor(2560, 1440, 64, 64, 64, 1);
        var final = ClassicOverlayMixture.Default.Mix(canvas, placed, PluginManager.CreateComputer(ClassicOverlayMixture.ComputerId, false), Shared.IPicture.PicturePixelMode.BytePicture);
        PlaceResizeTestImage.Source = ImageSource.FromStream(() =>
        {
            MemoryStream ms = new();
            final.SaveToSixLaborsImage().SaveAsPng(ms);
            ms.Position = 0;
            return ms;
        });
    }

    private async void TestFFmpegButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            var vidFile = await FileSystemService.PickFileAsync();
            if (string.IsNullOrWhiteSpace(vidFile)) vidFile = await DisplayPromptAsync("info", "input src path");
            if (string.IsNullOrWhiteSpace(vidFile)) return;
            if (!uint.TryParse(await DisplayPromptAsync(Localized._Info, "input frame index", initialValue: "0", keyboard: Keyboard.Numeric) ?? "", out var idx)) return;
            if (await DisplayAlertAsync(Title, "Use HDR?", "yes", "no"))
            {
                var src = new HDRDecoderContext(vidFile);
                src.Initialize();
                var frame = src.GetFrame(idx, false);
                if (frame is not HDRPicture16bpp h)
                {
                    await DisplayAlertAsync(Title, "Failed to decode HDR frame, got non-HDR picture.", "ok");
                    return;
                }
                HDROutputPreview.IsVisible = true;
                NormalizeBrightnessToRGBOutputImage.Source = ImageSource.FromStream(() =>
                {
                    MemoryStream ms = new();
                    h.SaveToSixLaborsImage(16, false, HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB).SaveAsPng(ms);
                    ms.Position = 0;
                    return ms;
                });
                OverlayMaskFromBrightnessOutputImage.Source = ImageSource.FromStream(() =>
                {
                    MemoryStream ms = new();
                    h.SaveToSixLaborsImage(16, false, HDRImageDegradeToSDRMode.OverlayMaskFromBrightness).SaveAsPng(ms);
                    ms.Position = 0;
                    return ms;
                });
                DiscardBrightnessChannelOutputImage.Source = ImageSource.FromStream(() =>
                {
                    MemoryStream ms = new();
                    h.SaveToSixLaborsImage(16, false, HDRImageDegradeToSDRMode.DiscardBrightnessChannel).SaveAsPng(ms);
                    ms.Position = 0;
                    return ms;
                });
                LogDiagnostic(h.GetDiagnosticsInfo());
            }
            else
            {
                var src = PluginManager.CreateVideoSource(vidFile);
                var frame = src.GetFrame(idx, false);
                LogDiagnostic(frame.GetDiagnosticsInfo());
                PlaceResizeTestImage.Source = ImageSource.FromStream(() =>
                {
                    MemoryStream ms = new();
                    frame.SaveToSixLaborsImage().SaveAsPng(ms);
                    ms.Position = 0;
                    return ms;
                });
            }
        }
        catch (Exception ex)
        {
            if (await DisplayAlertAsync(Title, Localized._ExceptionTemplate(ex), "throw", "ok")) throw;
        }
    }

    private void TestMixtureButton_Clicked(object sender, EventArgs e)
    {
        Picture8bpp src = Picture8bpp.GenerateSolidColor(200, 300, 128, 128, 128, 1);
        PlaceEffect_HwAccel p = new()
        {
            StartX = 50,
            StartY = 120
        };
        var result = p.Render(src, null, 2560, 1440);
        Picture8bpp canvas = Picture8bpp.GenerateSolidColor(2560, 1440, 64, 64, 64, 1);
        var final = ClassicOverlayMixture.Default.Mix(canvas, result, PluginManager.CreateComputer(ClassicOverlayMixture.ComputerId, false), Shared.IPicture.PicturePixelMode.BytePicture);
        PlaceResizeTestImage.Source = ImageSource.FromStream(() =>
        {
            MemoryStream ms = new();
            final.SaveToSixLaborsImage().SaveAsPng(ms);
            ms.Position = 0;
            return ms;
        });
    }

    private void ContextMenuTestBtn_Clicked(object sender, EventArgs e)
    {
        void dialog(string msg) => Dispatcher.Dispatch(async () => await DisplayAlertAsync("info", msg, "ok"));
#if WINDOWS
        WindowsContextMenuBuilder b = new();
        b.AddCommand("Command 1", () => dialog("You clicked 1")).AddSeparator().AddCommand("Command 2", () => dialog("You clicked 2")).AddCommand("command 3", async () =>
        {
            await Task.Delay(500);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var window = Microsoft.Maui.Controls.Application.Current?.Windows[0];
                if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                    WinUI.App.SetForegroundWindow(hwnd);
                    WinUI.App.SetFocus(hwnd);
                    WinUI.App.FlashWindow(hwnd, true);
                }
                WinUI.App.MessageBeep(0x00000040);
            });
        });
        b.TryShow(ContextMenuTestBtn);
#endif
    }

    private void MuxVideoTestBtn_Clicked(object sender, EventArgs e)
    {
        VideoAudioMuxer.MuxFromFiles(@"D:\code\playground\projectFrameCut\RenderCache\A Short Project 1_20260101_164404.mp4", @"D:\code\playground\projectFrameCut\RenderCache\A Short Project 1_20260101_164404.wav", @"D:\code\playground\projectFrameCut\output1.mp4", true);

    }

    private async void BenchmarkButton_Clicked(object sender, EventArgs e)
    {

#if ANDROID
        Render.AndroidOpenGL.ComputerHelper.AddPlatformComputeViewHandler = ComputeView.Children.Add;
        Render.AndroidOpenGL.ComputerHelper.Init();
#elif iDevices

#elif WINDOWS
        var context = ILGPU.Context.CreateDefault();
        var devices = context.Devices.ToList();
        if (SettingsManager.IsBoolSettingTrue("accel_enableMultiAccel"))
        {
            var accels = SettingsManager.GetSetting("accel_MultiDeviceID", "all");
            if (accels == "all")
            {
                projectFrameCut.Render.WindowsRender.ILGPUPlugin.accelerators = devices.Where(d => d.AcceleratorType != ILGPU.Runtime.AcceleratorType.CPU).Select(d => d.CreateAccelerator(context)).ToArray();
            }
            else
            {
                var accelList = accels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(s => int.TryParse(s, out var id) ? id : -1)
                            .Where(id => id >= 0)
                            .ToList();
                projectFrameCut.Render.WindowsRender.ILGPUPlugin.accelerators = devices.Index().Where(d => accelList.Contains(d.Index)).Select(d => d.Item.CreateAccelerator(context)).ToArray();
            }

        }
        else
        {
            var accelId = SettingsManager.GetSetting("accel_DeviceId", "");
            if (int.TryParse(accelId, out var accelIdInt)) projectFrameCut.Render.WindowsRender.ILGPUPlugin.accelerators = [devices[accelIdInt].CreateAccelerator(context)];
        }

        if (!projectFrameCut.Render.WindowsRender.ILGPUPlugin.accelerators.ArrayAny()) throw new InvalidDataException("No valid ILGPU accelerators found.");

#endif
        await Benchmarker.Start((d, etr) =>
        {
            string timeStr = "";
            if (etr.TotalSeconds > 0)
            {
                timeStr = (etr.TotalHours >= 1 ? etr.ToString(@"hh\:mm\:ss") : etr.ToString(@"mm\:ss"));
            }
            Dispatcher.Dispatch(async () =>
            {
                BenchmarkButton.Text = Localized.RenderPage_Stat(d, timeStr);

            });
        });
    }
    #endregion

    #region PropertyPanelBuilder test
    PropertyPanelBuilder ppb = new();
    private void AddPPBButton_Clicked(object sender, EventArgs e)
    {
        ppb = new PropertyPanelBuilder()
        {
            DefaultPadding = new Thickness(PPBPaddingSlider.Value),
            WidthOfContent = SettingsManager.GetSettingAs<int>("ui_defaultWidthOfContent", 1) // PPBRatioSlider.Value
        }
        .AddText(new TitleAndDescriptionLineLabel("ppb Test", "a example of PropertyPanelBuilder", 32))
        .AddText("This is a test", fontSize: 16, fontAttributes: FontAttributes.Bold)
        .AddEntry("testEntry", "Test Entry:", "text", "Enter something...", EntrySeter: (entry) =>
        {
            entry.WidthRequest = 200;
        })
        .AddSlider("testSlider", "Test Slider:", 0, 100, 50)
        .AddSeparator(null)
        .AddCheckbox("testCheckbox", "Test Checkbox:", false)
        .AddSwitch("testSwitch", "Test Switch:", true)
        .AddSeparator(null)
        .AddButton("testButton", "Click me!")
        .AddText(new projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders.InfoSingleLineLabel("abcdef","ghijklm"))
        .AddText(new projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders.InfoSingleLineLabel("abcdef222","ghijklm111"))
        .AddCustomChild("pick a date", (c) =>
        {
            var picker = new DatePicker
            {
                WidthRequest = 200,
                Date = DateTime.Now,
            };
            picker.DateSelected += (s, e) => c(e.NewDate.ToString() ?? "unknown");
            return picker;
        }, "testDatePicker", DateTime.Now.ToString("G"))
        .AddSeparator()
        .AddIconTitleDescriptionCard("icon test", "This is an icon title description card.", "This is a longer description for the icon title description card to demonstrate how it looks like in the panel.", "icon_add", 48, 48)
        .AddCustomChild(new Rectangle
        {
            WidthRequest = 100,
            HeightRequest = 500,
            Fill = Colors.Green
        })
        .ListenToChanges(async (s, e) =>
        {
            await DisplayAlertAsync("Property Changed", $"Property '{e.Id}' changed from '{e.OriginValue}' to '{e.Value}'", "OK");
        });
        PpbTestGrid.Content = ppb.Build();


    }

    private void PPBPaddingSlider_DragCompleted(object sender, EventArgs e)
    {
        Dispatcher.Dispatch(() =>
        {
            ppb.DefaultPadding = PPBPaddingSlider.Value;
            PpbTestGrid.Content = ppb.Build();
        });
    }

    private async void ExportPPBDataButton_Clicked(object sender, EventArgs e)
    {
        await DisplayAlert("Info", JsonSerializer.Serialize(ppb.Properties), "ok");
    }
    #endregion

    #region runtime
    private async void TestCrashButton_Clicked(object sender, EventArgs e)
    {
        var type = await DisplayActionSheetAsync("Choose a favor you'd like", "Cancel", null, "Environment.FailFast", "Native(null pointer)", "Managed(NullReferenceException)");
        switch (type)
        {
            case "Native(null pointer)":
#if ANDROID
                throw new Java.Lang.NullPointerException("test crash from native code");
#elif iDevices

#elif WINDOWS
                IntPtr ptr = IntPtr.Zero;
                Marshal.WriteInt32(ptr, 42);
#endif
                break;
            case "Managed(NullReferenceException)":
                throw new NullReferenceException("test crash");
            case "Environment.FailFast":
                Environment.FailFast("test crash");
                break;
        }


    }

    private async void StuckInUIThreadButton_Clicked(object sender, EventArgs e)
    {
        var type = await DisplayActionSheetAsync("Choose a flavor you'd like", "Cancel", null, "Dispatcher.Dispatch", "MainThread.BeginInvokeOnMainThread", "In current context");
        switch (type)
        {
            case "Dispatcher.Dispatch":
                Dispatcher.Dispatch(() =>
                {
                    Thread.Sleep(50000);
                });
                break;
            case "MainThread.BeginInvokeOnMainThread":
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Thread.Sleep(50000);
                });
                break;
            case "In current context":
                Thread.Sleep(50000);
                break;
        }
    }

    private void ReadSettingButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            var std = Preferences.Get("App_Channel", "none");
            DisplayAlertAsync("Setting Value", $"app_channel: {std}", "ok");
            Preferences.Set("App_Channel", "none");
        }
        catch { }
    }

    private async void WinUIDiagTestBtn_Clicked(object sender, EventArgs e)
    {
#if WINDOWS
        Microsoft.UI.Xaml.Controls.ContentDialog diag = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "WinUI ContentDialog Test",
            Content = "This is a test of WinUI ContentDialog in .NET MAUI.",
            CloseButtonText = "Close",
            PrimaryButtonText = "Primary",
            SecondaryButtonText = "Secondary"
        };

        var services = Application.Current?.Handler?.MauiContext?.Services;
        var dialogueHelper = services?.GetService(typeof(projectFrameCut.Platforms.Windows.IDialogueHelper)) as projectFrameCut.Platforms.Windows.IDialogueHelper;
        if (dialogueHelper != null)
        {
            var r = await dialogueHelper.ShowContentDialogue(diag);
            await DisplayAlert(Title, $"You selected {r}", "ok");
        }
#endif
    }


    private void OpenTestWindowButton_Clicked(object sender, EventArgs e)
    {
        View makeWindowContent(int level, MultiWindowItem item)
        {
            return new VerticalStackLayout
            {
                Children =
                {
                    new Label { Text = $"This window is in level {level + 1}\r\nA random number:{Random.Shared.Next()}" },
                    new Button { Text = "Back", Command = new Command(() =>
                    {
                        if (item.CanGoBack)
                        {
                            item.GoBack();
                        }
                    })},
                    new Button { Text = "Front", Command = new Command(() => item.NavigateTo(makeWindowContent(level+1, item)) )},
                    new Button {Text = "Prompt", Command = new Command(async () =>
                    {
                        var result = await item.DisplayAlertAsync("Action", "ok?", "yes", "no");
                        await DisplayAlertAsync(Title, result.ToString(), "ok");
                    })},
                    new Button {Text = "ActionSheet", Command = new Command(async () =>
                    {
                        var result = await item.DisplayActionSheetAsync("Options", "no", "destruct", TextServices.DummyStrings);
                        await DisplayAlertAsync(Title, result?.ToString() ?? "null input, may user cancelled.", "ok");
                    })},
                    new Button {Text = "Input", Command = new Command(async () =>
                    {
                        var result = await item.DisplayPromptAsync("Action", "Input some text", "yes", "no");
                        await DisplayAlertAsync(Title, result?.ToString() ?? "null input, may user cancelled.", "ok");
                    })},
                    new Button {Text = "DraftPage", Command = new Command(async () =>
                    {
                        item.Content = new DraftPage().Content;
                    })}
                }
            };
        }
        var myWindow = new MultiWindowItem
        {
            WidthRequest = 400,
            HeightRequest = 300,
            IsPopOutVisible = true
        };
        myWindow.Content = makeWindowContent(0, myWindow);
        myWindow.Title = $"Test Window {++windowCount}";


        myMultiWindowView.AddWindow(myWindow);
    }

    #endregion

    #region text

    private async Task LoadFontPickerAsync()
    {
        var items = TextHelper.BuildSystemFontItems(preferredLocale: null);
        var ordered = await items.OrderByPronounceAsync(c => c.DisplayName);
        TextPicker.FontsSource = ordered;
        TextPicker.Title = $"Fonts ({ordered.Count()})";
    }

    private async void TestOrderButton_Clicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputEditor.Text)) return;
        var lines = InputEditor.Text.Split(["\r", "\n", "\r\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var loc = string.IsNullOrWhiteSpace(LocateInputer.Text) ? Localized._LocaleId_ : LocateInputer.Text.Trim();
        var ordered = lines.OrderBy(async a => await TextServices.GetPronounceForOrdering(a, loc)).GroupBy(TextHelper.DetectTextLanguage).OrderByDescending(g => g.Count()).SelectMany(c => c).ToList();
        InputEditor.Text = string.Join(Environment.NewLine, ordered);
        TestOrderButton.Text = "Order done";

    }

    private async void TestFontPropReaderButton_Clicked(object sender, EventArgs e)
    {
        var info = TextHelper.ReadFontFileInfo(@"C:\Windows\Fonts\msyhbd.ttc");
        await DisplayAlertAsync(Title, JsonSerializer.Serialize(info, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), "ok");
    }
    #endregion

    #region misc

    private void MetalRenderStartButton_Clicked(object sender, EventArgs e)
    {

    }

    private void TaskbarOptionPicker_SelectedIndexChanged(object sender, EventArgs e)
    {
        myMultiWindowView.TaskbarVisibility = (TaskbarVisibilityMode)TaskbarOptionPicker.SelectedIndex;
    }

    private void StoreModeToggleSwitch_Toggled(object sender, ToggledEventArgs e)
    {
        SettingsManager.WriteSetting("StoreModeOverride", e.Value.ToString());
    }

    private void ResetStoreModeButton_Clicked(object sender, EventArgs e)
    {
        SettingsManager.WriteSetting("StoreModeOverride", "disable");

    }

    private async void LoginTestButton_Clicked(object sender, EventArgs e)
    {
        AuthService.Logout();

        if (AuthService.IsLoggedIn)
        {
            // �ѵ�¼����ʾ�û���Ϣ��ǳ�
            var user = await AuthService.GetCurrentUserAsync();
            await DisplayAlertAsync("�ѵ�¼", $"��ǰ�û�: {user.UserName}", "ȷ��");
        }
        else
        {
            // δ��¼���򿪵�¼ҳ��
            await Navigation.PushAsync(new LoginPage());
        }
    }

    private int windowCount = 0;




    #endregion




}
