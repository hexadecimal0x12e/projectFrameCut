using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Devices;

using projectFrameCut.DraftStuff;
using projectFrameCut.PropertyPanel;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using Path = System.IO.Path;
using projectFrameCut.Shared;




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
    private PanDeNoise denoise = new();

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
                        Title = "保存拖动测试数据",
                        File = new ShareFile(p)
                    });
                    break;
                }
        }
    }


    #endregion

    #region openGL test
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
        await DisplayAlert("提示", "此功能目前仅在 Android 上可用。", "确定");
#endif
    }

    public string ShaderAlphaSrc =
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

    #region PropertyPanelBuilder test
    PropertyPanelBuilder ppb = new();
    private void AddPPBButton_Clicked(object sender, EventArgs e)
    {
        ppb = new PropertyPanelBuilder()
        {
            DefaultPadding = new Thickness(PPBPaddingSlider.Value),
            WidthOfContent = PPBRatioSlider.Value
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
        .AddCustomChild(new Button
        {
            Text = "Custom Button",
            WidthRequest = 150
        })
        .AddButton("testButton",  "Click me!")
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
        .AddCustomChild(new Rectangle
        {
            WidthRequest = 100,
            HeightRequest = 500,
            Fill = Colors.Green
        })
        .ListenToChanges(async (s, e) =>
        {
            await DisplayAlert("Property Changed", $"Property '{e.Id}' changed from '{e.OriginValue}' to '{e.Value}'", "OK");
        });
        PpbTestGrid.Content = ppb.Build();


    }

    private void PPBPaddingSlider_ValueChanged(object sender, ValueChangedEventArgs e)
    {
        ppb.DefaultPadding = e.NewValue;
        PpbTestGrid.Content = ppb.Build();
    }

    private async void ExportPPBDataButton_Clicked(object sender, EventArgs e)
    {
        await DisplayAlert("Info", JsonSerializer.Serialize(ppb.Properties), "ok");
    }
    #endregion

    private async void TestCrashButton_Clicked(object sender, EventArgs e)
    {
        var type = await DisplayActionSheetAsync("Choose a favour you'd like", "Cancel", "Environment.FailFast", "Native(null pointer)", "Managed(NullReferenceException)");
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

    #region AsymmetricCrypto

    private async void CreateAsymmetricCryptoBtn_Clicked(object sender, EventArgs e)
    {
        (var pub, var pri) = AsymmetricCrypto.GenerateKeyPairHex();
        Log($"pubKey:{pub}, priKey:{pri}");
        await DisplayAlert("info", pub, "ok");
        await DisplayAlert("info", pri, "ok");

    }

    private async void EncryptFileBtn_Clicked(object sender, EventArgs e)
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        { DevicePlatform.WinUI, new[] { "" } },
                        { DevicePlatform.Android, new[] { "application/octet-stream"} },
#if iDevices
                        {DevicePlatform.iOS, new[] {""} },
                        {DevicePlatform.MacCatalyst, new[] {""} }
#endif
                    }),
        });

        if (result != null)
        {
            var pubHex = await DisplayPromptAsync(Title, "input prikey");
            var pubBase = AsymmetricCrypto.HexToBase64(pubHex ?? "");
            await AsymmetricCrypto.EncryptFileWithPrivateKeyAsync(result.FullPath, Path.Combine(Path.GetDirectoryName(result.FullPath), Path.GetFileName(result.FullPath) + "enc" + Path.GetExtension(result.FullPath)), pubBase, null, default);
            await DisplayAlertAsync("info", "done", "ok");

        }
    }

    private async void DecryptFileBtn_Clicked(object sender, EventArgs e)
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        { DevicePlatform.WinUI, new[] { "" } },
                        { DevicePlatform.Android, new[] { "application/octet-stream"} },
#if iDevices
                        {DevicePlatform.iOS, new[] {""} },
                        {DevicePlatform.MacCatalyst, new[] {""} }
#endif
                    }),
        });

        if (result != null)
        {
            var pubHex = await DisplayPromptAsync(Title, "input pubkey");
            var pubBase = AsymmetricCrypto.HexToBase64(pubHex ?? "");
            await AsymmetricCrypto.DecryptFileWithPublicKeyAsync(result.FullPath, Path.Combine(Path.GetDirectoryName(result.FullPath),Path.GetFileName(result.FullPath) +"dec" + Path.GetExtension(result.FullPath)), pubBase, null, default);
            await DisplayAlertAsync("info", "done", "ok");

        }
    }

    #endregion

    #region misc

    private void MetalRenderStartButton_Clicked(object sender, EventArgs e)
    {

    }


    private async void TestFFmpegButton_Clicked(object sender, EventArgs e)
    {
        string ver = "unknown";
#if !iDevices
        unsafe
        {
            ver = $"internal FFmpeg library: version {FFmpeg.AutoGen.ffmpeg.av_version_info()}, {FFmpeg.AutoGen.ffmpeg.avcodec_license()}\r\nconfiguration:{FFmpeg.AutoGen.ffmpeg.avcodec_configuration()}";
        }
#elif IOS || MACCATALYST
#if IOS26_0_OR_GREATER || MACCATALYST26_0_OR_GREATER
        var codecs = AVFoundation.AVUrlAsset.AudiovisualContentTypes;
        ver = "AudiovisualContentTypes: "+ string.Concat(codecs,",");
#else
        var codecs = AVFoundation.AVUrlAsset.AudiovisualTypes;
        ver = "AudiovisualTypes: "+ string.Concat(codecs,",");
#endif
#endif
        await DisplayAlert("FFmpeg Version", ver, "OK");

    }

    #endregion




}
