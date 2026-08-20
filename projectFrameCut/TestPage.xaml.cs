using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using FFmpeg.AutoGen;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Devices;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.DraftStuff;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Processing.Converting;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.FontHelper;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.Benchmark;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.HwAccelEngine;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Services;
using projectFrameCut.Services.AIComponent;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Color = Microsoft.Maui.Graphics.Color;
using DatePicker = Microsoft.Maui.Controls.DatePicker;
using Path = System.IO.Path;
using Rectangle = Microsoft.Maui.Controls.Shapes.Rectangle;
using projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML;
using projectFrameCut.Render.ClipsAndTracks.Text;
#if !DISABLE_POWERSHELL_SDK
using System.Management.Automation;
#endif



#if ANDROID
using projectFrameCut.Platforms.Android;
using projectFrameCut.Render.HwAccelEngine.Platforms.Android;

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
#endif
#if WINDOWS || LINUX
        // AcceleratorsManager was initialized during plugin load.
        if (projectFrameCut.Render.HwAccelEngine.AcceleratorsManager.DefaultAccelerator is null)
        {
            Log("WARNING: No ILGPU accelerator found on this device. GPU-accelerated operations will fall back to software.");
        }
#endif
    }


    #region Windows System AI IPC test

    private IAIComponentClient? _aiComponentClient;
    private bool _aiComponentOperationRunning;

    private IAIComponentClient GetAIComponentClient()
    {
        if (_aiComponentClient is not null)
        {
            return _aiComponentClient;
        }

        _aiComponentClient = Handler?.MauiContext?.Services.GetService<IAIComponentClient>()
            ?? App.Current?.Handler?.MauiContext?.Services.GetService<IAIComponentClient>()
            ?? new AIComponentUnavailableClient();
        return _aiComponentClient;
    }

    private void UpdateAIComponentButtons()
    {
        var client = GetAIComponentClient();
        bool canExecute = !_aiComponentOperationRunning && client.IsConnected;

        AIComponentConnectButton.IsEnabled = !_aiComponentOperationRunning && !client.IsConnected;
        AIComponentDisconnectButton.IsEnabled = !_aiComponentOperationRunning && client.IsConnected;
        AIComponentTextButton.IsEnabled = canExecute;
        AIComponentPictureButton.IsEnabled = canExecute;
        AIComponentAudioButton.IsEnabled = canExecute;
    }

    private bool TryGetConnectedAIComponentClient(out IAIComponentClient client)
    {
        client = GetAIComponentClient();
        if (!client.IsSupported)
        {
            AIComponentStatusLabel.Text = "The Windows System AI extension is unavailable on this platform.";
            return false;
        }

        if (!client.IsConnected)
        {
            AIComponentStatusLabel.Text = "Connect the extension first.";
            return false;
        }

        return true;
    }

    private static string FormatAIComponentCapabilities(IReadOnlyList<projectFrameCut.AIComponentContracts.AICapabilityDescriptor> capabilities)
    {
        if (capabilities.Count == 0)
        {
            return "No capabilities reported.";
        }

        return string.Join(
            Environment.NewLine,
            capabilities.Select(capability =>
                $"{capability.Operation}: {capability.Input} -> {capability.Output}"
                + (string.IsNullOrWhiteSpace(capability.Description) ? string.Empty : $" ({capability.Description})")));
    }

    private void SetAIComponentOperationRunning(bool running)
    {
        _aiComponentOperationRunning = running;
        UpdateAIComponentButtons();
    }

    private static Picture8bpp CreateAIComponentTestPicture()
    {
        const int width = 96;
        const int height = 64;
        int pixels = width * height;
        var picture = new Picture8bpp(width, height)
        {
            a = new float[pixels],
            HasAlphaChannel = true,
            Tag = "AIComponent test input"
        };

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                picture.r[index] = (byte)(x * 255 / (width - 1));
                picture.g[index] = (byte)(y * 255 / (height - 1));
                picture.b[index] = (byte)((x + y) * 255 / (width + height - 2));
                picture.a[index] = 0.5f + 0.5f * x / (width - 1);
            }
        }

        return picture;
    }

    private static FloatAudioSamples CreateAIComponentTestAudio()
    {
        const int sampleRate = 16_000;
        const int sampleCount = 1_600;
        var samples = new float[sampleCount];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = MathF.Sin(2 * MathF.PI * 440 * i / sampleRate) * 0.25f;
        }

        return new FloatAudioSamples
        {
            Channels = [samples],
            SampleCount = sampleCount,
            SamplePerSecond = sampleRate
        };
    }

    private async void AIComponentConnectButton_Clicked(object? sender, EventArgs e)
    {
        var client = GetAIComponentClient();
        if (!client.IsSupported)
        {
            AIComponentStatusLabel.Text = "The Windows System AI extension is unavailable on this platform.";
            UpdateAIComponentButtons();
            return;
        }

        SetAIComponentOperationRunning(true);
        AIComponentStatusLabel.Text = "Starting extension and connecting...";
        try
        {
            var capabilities = await client.ConnectAsync();
            AIComponentCapabilitiesEditor.Text = FormatAIComponentCapabilities(capabilities);
            AIComponentStatusLabel.Text = $"Connected. {capabilities.Count} capability(s) available.";
        }
        catch (Exception ex)
        {
            AIComponentStatusLabel.Text = $"Connect failed: {ex.Message}";
            Debug.WriteLine(ex);
        }
        finally
        {
            SetAIComponentOperationRunning(false);
        }
    }

    private async void AIComponentDisconnectButton_Clicked(object? sender, EventArgs e)
    {
        SetAIComponentOperationRunning(true);
        try
        {
            await GetAIComponentClient().DisconnectAsync();
            AIComponentCapabilitiesEditor.Text = string.Empty;
            AIComponentResultLabel.Text = string.Empty;
            AIComponentResultImage.Source = null;
            AIComponentResultImage.IsVisible = false;
            AIComponentStatusLabel.Text = "Disconnected.";
        }
        catch (Exception ex)
        {
            AIComponentStatusLabel.Text = $"Disconnect failed: {ex.Message}";
            Debug.WriteLine(ex);
        }
        finally
        {
            SetAIComponentOperationRunning(false);
        }
    }

    private async void AIComponentTextButton_Clicked(object? sender, EventArgs e)
    {
        if (!TryGetConnectedAIComponentClient(out var client))
        {
            return;
        }

        SetAIComponentOperationRunning(true);
        try
        {
            string input = AIComponentTextEditor.Text ?? string.Empty;
            string output = await client.ExecuteTextAsync("text.echo", input);
            AIComponentResultLabel.Text = $"Text echo ({output.Length} chars): {output}";
            AIComponentStatusLabel.Text = "Text round-trip succeeded.";
        }
        catch (Exception ex)
        {
            AIComponentResultLabel.Text = $"Text test failed: {ex.Message}";
            AIComponentStatusLabel.Text = "Text round-trip failed.";
            Debug.WriteLine(ex);
        }
        finally
        {
            SetAIComponentOperationRunning(false);
        }
    }

    private async void AIComponentPictureButton_Clicked(object? sender, EventArgs e)
    {
        if (!TryGetConnectedAIComponentClient(out var client))
        {
            return;
        }

        SetAIComponentOperationRunning(true);
        try
        {
            using var input = CreateAIComponentTestPicture();
            using var output = await client.ExecutePictureAsync("picture.echo", input);
            AIComponentResultImage.Source = output.ToImageSource();
            AIComponentResultImage.IsVisible = true;
            AIComponentResultLabel.Text = $"Picture echo: {output.Width} x {output.Height}, {output.BitPerPixel} bpp, alpha={output.HasAlphaChannel}";
            AIComponentStatusLabel.Text = "Picture round-trip succeeded.";
        }
        catch (Exception ex)
        {
            AIComponentResultLabel.Text = $"Picture test failed: {ex.Message}";
            AIComponentStatusLabel.Text = "Picture round-trip failed.";
            Debug.WriteLine(ex);
        }
        finally
        {
            SetAIComponentOperationRunning(false);
        }
    }

    private async void AIComponentAudioButton_Clicked(object? sender, EventArgs e)
    {
        if (!TryGetConnectedAIComponentClient(out var client))
        {
            return;
        }

        SetAIComponentOperationRunning(true);
        try
        {
            var input = CreateAIComponentTestAudio();
            var output = await client.ExecuteAudioAsync("audio.echo", input);
            bool same = output.SamplePerSecond == input.SamplePerSecond
                && output.SampleCount == input.SampleCount
                && output.channelCount == input.channelCount
                && input.GetSamples(0).AsSpan(0, input.SampleCount).SequenceEqual(output.GetSamples(0).AsSpan(0, output.SampleCount));
            AIComponentResultLabel.Text = $"Audio echo: {output.SamplePerSecond} Hz, {output.channelCount} channel(s), {output.SampleCount} samples, data match={same}";
            AIComponentStatusLabel.Text = same ? "Audio round-trip succeeded." : "Audio metadata/data mismatch.";
        }
        catch (Exception ex)
        {
            AIComponentResultLabel.Text = $"Audio test failed: {ex.Message}";
            AIComponentStatusLabel.Text = "Audio round-trip failed.";
            Debug.WriteLine(ex);
        }
        finally
        {
            SetAIComponentOperationRunning(false);
        }
    }

    #endregion

    private void TestPage_Loaded(object? sender, EventArgs e)
    {
        var client = GetAIComponentClient();
        AIComponentStatusLabel.Text = client.IsSupported
            ? "Extension is supported. Click Connect extension to start."
            : "The Windows System AI extension is unavailable on this platform.";
        UpdateAIComponentButtons();

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
                        DraggingTestLabel.Text = $"Dragging X:{e.TotalX}, denoised: {noNoise + _origX}";
                        DraggingX.Push(e.TotalX);
                        DenoisedX.Push(noNoise);
                    }
                    else
                    {
                        DraggingTestLabel.Text = $"Dragging X:{e.TotalX}";
                        b.TranslationX = e.TotalX + _origX;
                        DraggingX.Push(e.TotalX);
                        DenoisedX.Push(0);

                    }


                    break;
                }
            case GestureStatus.Canceled:
            case GestureStatus.Completed:
                {
                    DraggingTestLabel.Text = $"Dragging X:{e.TotalX}";
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
    private Picture16bpp srcA, srcB;

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
                    srcA = new Picture16bpp("/storage/emulated/0/Android/data/com.hexadecimal0x12e.projectframecut/files/@Original_track_a.png");
                }),
                Task.Run(() =>
                {
                    srcB = new Picture16bpp("/storage/emulated/0/Android/data/com.hexadecimal0x12e.projectframecut/files/@Original_track_b.png");
                })
            ]);


            ushort[] uOutR = Array.Empty<ushort>(), uOutG = Array.Empty<ushort>(), uOutB = Array.Empty<ushort>();
            Task RConvertor, GConvertor, BConvertor;
            float[] outA = Array.Empty<float>();
            {

                var tcsA = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);

                var alphaGlView = new projectFrameCut.Render.HwAccelEngine.Platforms.Android.NativeGLSurfaceView()
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
                        if (alphaGlView.Handler is projectFrameCut.Render.HwAccelEngine.Platforms.Android.NativeGLSurfaceViewHandler handler)
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

                                var res = (float[])await platformView.RunComputeAsync(projectFrameCut.Render.HwAccelEngine.Platforms.Android.GLComputeView.OutputElementType.Float32);
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

                var RGlView = new projectFrameCut.Render.HwAccelEngine.Platforms.Android.NativeGLSurfaceView()
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
                        if (RGlView.Handler is projectFrameCut.Render.HwAccelEngine.Platforms.Android.NativeGLSurfaceViewHandler handler)
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

                                var res = (float[])await platformView.RunComputeAsync(projectFrameCut.Render.HwAccelEngine.Platforms.Android.GLComputeView.OutputElementType.Float32);
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

                var GGlView = new projectFrameCut.Render.HwAccelEngine.Platforms.Android.NativeGLSurfaceView()
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
                        if (GGlView.Handler is projectFrameCut.Render.HwAccelEngine.Platforms.Android.NativeGLSurfaceViewHandler handler)
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

                                var res = (float[])await platformView.RunComputeAsync(projectFrameCut.Render.HwAccelEngine.Platforms.Android.GLComputeView.OutputElementType.Float32);
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

                var BGLView = new projectFrameCut.Render.HwAccelEngine.Platforms.Android.NativeGLSurfaceView()
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
                        if (BGLView.Handler is projectFrameCut.Render.HwAccelEngine.Platforms.Android.NativeGLSurfaceViewHandler handler)
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

                                var res = (float[])await platformView.RunComputeAsync(projectFrameCut.Render.HwAccelEngine.Platforms.Android.GLComputeView.OutputElementType.Float32);
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
            var outPic = new Picture16bpp(srcA.Width, srcA.Height)
            {
                r = uOutR,
                g = uOutG,
                b = uOutB,
                a = outA,
                HasAlphaChannel = true
            };

            var path = $"/storage/emulated/0/Android/data/com.hexadecimal0x12e.projectframecut/files/out-{DateTime.Now:yyyy-MM-dd-hh-mm-ss}.png";
            outPic.SaveToPng(path);

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
            TextClip c = new TextClip { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Name = "1" };
            TextEntry te = new TextEntry
            {
                FillR = 65535,
                FillG = 65535,
                FillB = 65535,
                FillA = 1f,
                FontName = "Arial",
                X = 50,
                Y = 50,
                FontSize = 120,
                Text = "",
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
            f.SaveToPng(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"));

            for (int i = 0; i < 1; i++)
            {
                c.TextEntries = [te with { Text = $"Frame {i}" }];
                var textFrame = c.GetFrameRelativeToStartPointOfSource(0U, 2560, 1440, 16);
                textFrame.SaveToPng(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-textFrame-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"));
                var t = HDRPicture16bpp.ToHDRPictureBySignal(textFrame, 5000);
                Log(t.GetDiagnosticsInfo());
                t.SaveToPng(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-t-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"));
                var r = ClassicOverlayMixture.Default.Mix(f, t, PluginManager.CreateComputer(ClassicOverlayMixture.ComputerId), 16);
                r.SaveToPng(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-r-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"));
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
        fThrowBrightness.SaveToPng(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-throwBrightness-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"));
        var fNormalizeBrigtnessToRGB = f.DegradeToSDR(HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB);
        fNormalizeBrigtnessToRGB.SaveToPng(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-normalizeBrightnessToRGB-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"));
        var fReplaceAlpha = new Picture16bpp(f)
        {
            r = f.r,
            g = f.g,
            b = f.b,
            a = f.Brightness
        };
        fReplaceAlpha.SaveToPng(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-replaceAlpha-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"));
        var fReplaceAlphaAndComposeMask = ClassicOverlayMixture.Default.Mix(fThrowBrightness, new Picture16bpp(f)
        {
            r = Enumerable.Repeat((ushort)0, f.Pixels).ToArray(),
            g = Enumerable.Repeat((ushort)0, f.Pixels).ToArray(),
            b = Enumerable.Repeat((ushort)0, f.Pixels).ToArray(),
            a = f.Brightness.Select(c => Math.Clamp(1 - c, 0, 1)).ToArray()
        }, PluginManager.CreateComputer(ClassicOverlayMixture.ComputerId), 16);
        fReplaceAlphaAndComposeMask.SaveToPng(Path.Combine(FileSystem.CacheDirectory, $"hdrtest-replaceAlphaAndComposeMask-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"));
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
            result.SaveToPng(ms);
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
        ResizeEffect_IPicture r = new()
        {
            Height = 300,
            Width = 1000,
            PreserveAspectRatio = false
        };
        var resized = r.Render(src, null, 2560, 1440);
        var placed = p.Render(resized, null, 2560, 1440);
        Picture8bpp canvas = Picture8bpp.GenerateSolidColor(2560, 1440, 64, 64, 64, 1);
        var final = ClassicOverlayMixture.Default.Mix(canvas, placed, PluginManager.CreateComputer(ClassicOverlayMixture.ComputerId, false), Drawing.Base.IPicture.PicturePixelMode.BytePicture);
        PlaceResizeTestImage.Source = ImageSource.FromStream(() =>
        {
            MemoryStream ms = new();
            final.SaveToPng(ms);
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
                    h.DegradeToSDR(HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB).SaveToPng(ms);
                    ms.Position = 0;
                    return ms;
                });
                OverlayMaskFromBrightnessOutputImage.Source = ImageSource.FromStream(() =>
                {
                    MemoryStream ms = new();
                    h.DegradeToSDR(HDRImageDegradeToSDRMode.OverlayMaskFromBrightness).SaveToPng(ms);
                    ms.Position = 0;
                    return ms;
                });
                DiscardBrightnessChannelOutputImage.Source = ImageSource.FromStream(() =>
                {
                    MemoryStream ms = new();
                    h.DegradeToSDR(HDRImageDegradeToSDRMode.DiscardBrightnessChannel).SaveToPng(ms);
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
                    frame.SaveToPng(ms);
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
        var final = ClassicOverlayMixture.Default.Mix(canvas, result, PluginManager.CreateComputer(ClassicOverlayMixture.ComputerId, false), Drawing.Base.IPicture.PicturePixelMode.BytePicture);
        PlaceResizeTestImage.Source = ImageSource.FromStream(() =>
        {
            MemoryStream ms = new();
            final.SaveToPng(ms);
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


    private async void ReEncodeButton_Clicked(object sender, EventArgs e)
    {
        var vidFile = await FileSystemService.PickFileAsync();
        if (string.IsNullOrWhiteSpace(vidFile)) vidFile = await DisplayPromptAsync("info", "input src path");
        if (string.IsNullOrWhiteSpace(vidFile)) return;
        var outputPath = Path.Combine(FileSystem.CacheDirectory, $"reencode-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.mp4");
        var src = PluginManager.CreateVideoSource(vidFile);
        var dest = new VideoWriterHWAccel
        {
            CodecName = "libx264",
            FramePerSecond = (int)src.Fps,
            Width = src.Width,
            Height = src.Height,
            PixelFormat = AVPixelFormat.AV_PIX_FMT_YUV420P.ToString(),
            OutputPath = outputPath
        };
        dest.Initialize();
        for(uint i = 0;i < src.TotalFrames; i++)
        {
            Log($"{i} of {src.TotalFrames} done");
            dest.Append(src.GetFrame(i, false));
        }
        dest.Finish();
        dest.Dispose();
        src.Dispose();
    }

    private async void BenchmarkButton_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new BenchmarkPage());
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
        .AddText(new projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders.InfoSingleLineLabel("abcdef", "ghijklm"))
        .AddText(new projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders.InfoSingleLineLabel("abcdef222", "ghijklm111"))
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
        if (PpbTestGrid.Content is null) return;
        ControlTreeHelper h = new(PpbTestGrid.Content);
        var controls = h.GetAllValues();
        ControlIdPicker.ItemsSource = controls.Select(c => c.Key).ToList();
        ControlIdPicker.SelectedIndexChanged += (s, e) =>
        {
            if (ControlIdPicker.SelectedItem is string selectedId && h.GetAllItems().TryGetValue(selectedId, out var control) && control is not null)
            {
                ControlValueEntry.Text = control?.Value?.ToString() ?? "null";
                ControlValueEntry.IsReadOnly = !control.IsWritable;
            }
        };
        //await DisplayAlertAsync("PPB Data", string.Join("\n", h.GetAllItems().Where(c => c.Value.IsWritable).Select(c => $"{c.Key}: {c.Value}")) + "\r\n\r\n---\r\n\r\n" + string.Join("\n", h.GetAllItems().Select(c => $"{c.Key}: {c.Value}")), "OK");
    }

    private void SetValueButton_Clicked(System.Object sender, System.EventArgs e)
    {
        if (PpbTestGrid.Content is null) return;
        var selectedId = ControlIdPicker.SelectedItem as string;
        ControlTreeHelper h = new(PpbTestGrid.Content);
        if (h.GetAllItems().TryGetValue(selectedId ?? "", out var control) && control is not null && control.IsWritable)
        {
            control.Set(ControlValueEntry.Text);
        }

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

    private async void LoadFontButton_Clicked(object sender, EventArgs e)
    {
        TextPicker.PreviewRenderer = TextServices.RenderFontPreviewAsync;

        TextClipFontRegistry.Initialize();
        await LoadFontPickerAsync();

        TextPicker.SelectedFontChanged += async (s, e) =>
        {
            await DisplayAlertAsync(Title, JsonSerializer.Serialize(e.InnerFont, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, }), "ok");

        };

        LoadFontButton.IsEnabled = false;
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
        var info = TextHelper.CreateFontInfo("1", @"C:\Windows\Fonts\msyhbd.ttc");
        foreach (var item in info)
        {
            await DisplayAlertAsync(Title, JsonSerializer.Serialize(item.InnerFont, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), "ok");
        }
    }
    #endregion

    #region scripting
#if !DISABLE_POWERSHELL_SDK
    PowerShell? pwsh = null!;
    private void ExecteCommandButton_Clicked(object sender, EventArgs e)
    {
#if DEBUG
        pwsh ??= PowerShell.Create();
        if (!string.IsNullOrWhiteSpace(ScriptInputEntry.Text))
        {
            try
            {
                pwsh.AddScript(ScriptInputEntry.Text);
                var results = pwsh.Invoke();
                ScriptOutputEditor.Text += results.Select(r => r.ToString()).Aggregate((a, b) => a + Environment.NewLine + b);
            }
            catch (Exception ex)
            {
                Log(ex, "exec pwsh command");
                ScriptOutputEditor.Text += $"{Environment.NewLine}Error: {ex}{Environment.NewLine}";
            }
        }
#else
        ScriptOutputEditor.Text += $"This is a development stage only feature.";

#endif
    }
    private void InvokeNativeFuncButton_Clicked(object sender, EventArgs e)
    {
        SysLog(SysLogPriority.Info, "Test message to test libpsl-native");
    }

        [DllImport("libpsl-native", CharSet = CharSet.Ansi, EntryPoint = "Native_SysLog")] //testing native call of pwsh
    private static extern void SysLog(SysLogPriority priority, string message);

    [Flags]
    private enum SysLogPriority : uint
    {
        // Priorities enum values.

        /// <summary>
        /// System is unusable.
        /// </summary>
        Emergency = 0,

        /// <summary>
        /// Action must be taken immediately.
        /// </summary>
        Alert = 1,

        /// <summary>
        /// Critical conditions.
        /// </summary>
        Critical = 2,

        /// <summary>
        /// Error conditions.
        /// </summary>
        Error = 3,

        /// <summary>
        /// Warning conditions.
        /// </summary>
        Warning = 4,

        /// <summary>
        /// Normal but significant condition.
        /// </summary>
        Notice = 5,

        /// <summary>
        /// Informational.
        /// </summary>
        Info = 6,

        /// <summary>
        /// Debug-level messages.
        /// </summary>
        Debug = 7,

        // Facility enum values.

        /// <summary>
        /// Kernel messages.
        /// </summary>
        Kernel = (0 << 3),

        /// <summary>
        /// Random user-level messages.
        /// </summary>
        User = (1 << 3),

        /// <summary>
        /// Mail system.
        /// </summary>
        Mail = (2 << 3),

        /// <summary>
        /// System daemons.
        /// </summary>
        Daemon = (3 << 3),

        /// <summary>
        /// Authorization messages.
        /// </summary>
        Authorization = (4 << 3),

        /// <summary>
        /// Messages generated internally by syslogd.
        /// </summary>
        Syslog = (5 << 3),

        /// <summary>
        /// Line printer subsystem.
        /// </summary>
        Lpr = (6 << 3),

        /// <summary>
        /// Network news subsystem.
        /// </summary>
        News = (7 << 3),

        /// <summary>
        /// UUCP subsystem.
        /// </summary>
        Uucp = (8 << 3),

        /// <summary>
        /// Clock daemon.
        /// </summary>
        Cron = (9 << 3),

        /// <summary>
        /// Security/authorization messages (private)
        /// </summary>
        Authpriv = (10 << 3),

        /// <summary>
        /// FTP daemon.
        /// </summary>
        Ftp = (11 << 3),

        // Reserved for system use

        /// <summary>
        /// Reserved for local use.
        /// </summary>
        Local0 = (16 << 3),
        /// <summary>
        /// Reserved for local use.
        /// </summary>
        Local1 = (17 << 3),
        /// <summary>
        /// Reserved for local use.
        /// </summary>
        Local2 = (18 << 3),
        /// <summary>
        /// Reserved for local use.
        /// </summary>
        Local3 = (19 << 3),
        /// <summary>
        /// Reserved for local use.
        /// </summary>
        Local4 = (20 << 3),
        /// <summary>
        /// Reserved for local use.
        /// </summary>
        Local5 = (21 << 3),
        /// <summary>
        /// Reserved for local use.
        /// </summary>
        Local6 = (22 << 3),
        /// <summary>
        /// Reserved for local use.
        /// </summary>
        Local7 = (23 << 3),
    }
#else
    private void ExecteCommandButton_Clicked(object sender, EventArgs e)
    {
    
    }
    private void InvokeNativeFuncButton_Clicked(object sender, EventArgs e)
    {

    }
#endif



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

    private async void ShowCMTPopupButton_Clicked(object sender, EventArgs e)
    {
        await this.ShowPopupAsync(new Label
        {
            Text = "This is a very important message!"
        }, new PopupOptions
        {
            CanBeDismissedByTappingOutsideOfPopup = true,
            Shape = new RoundRectangle
            {
                CornerRadius = new CornerRadius(20, 20, 20, 20),
                StrokeThickness = 2,
                Stroke = Colors.LightGray
            }
        });
    }

    bool isNavPaneVisible = true;

    private void ToggleNavPaneButton_Clicked(object sender, EventArgs e)
    {
        if (isNavPaneVisible)
        {
            AppShell.instance?.HideNavView();
        }
        else
        {
            AppShell.instance?.ShowNavView();
        }
        isNavPaneVisible = !isNavPaneVisible;
    }

    private async void RenderContentButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            var view = XAMLFixer.FixXamlAndGenerateView(XAMLInputEditor.Text);
            ResultContentView.Content = view;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", $"Failed to render XAML: {ex}", "OK");
        }
    }

    private async void RenderMarkdownButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            var view = Markdown2XAML.Convert(XAMLInputEditor.Text);
            ResultContentView.Content = view;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", $"Failed to render XAML: {ex}", "OK");
        }
    }
    

    private async void ShowModelPageButton_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new ContentPage { Content = new VerticalStackLayout { Children = { new Label { Text = "This is a modal page." }, new Button { Text = "Pop", Command = new Command(async () => await Navigation.PopModalAsync()) } }, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center } });
    }

    private int windowCount = 0;




    #endregion




}
