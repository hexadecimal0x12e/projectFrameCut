using CommunityToolkit.Maui.Storage;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Layouts;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Shared;
using System.Windows.Input;

namespace projectFrameCut;

public partial class DraftPage
{
    public ICommand ExportCurrentFrameCommand { get; private set; } = null!;
    public ICommand ExportSelectedClipFrameCommand { get; private set; } = null!;
    private CancellationTokenSource? _frameExportCts;

    private async Task ExportCurrentFrameAsync(bool selectedClipOnly)
    {
        if (AlreadyDisappeared || _frameExportCts is not null) return;
        uint frame = (uint)Math.Max(0, _currentFrame);
        Guid? clipId = null;
        if (selectedClipOnly)
        {
            if (_selectedClipIds.Count != 1 || _selected is null)
            {
                SetStateWarn(Localized.DraftPage_SelectExactlyOneToContinue);
                return;
            }
            if (_selected.ClipType is ClipMode.AudioClip or ClipMode.MarkingClip)
            {
                SetStateWarn(Localized.DraftPage_ExportFrame_NoVisual);
                return;
            }
            if (!_selected.IsClipFallInRange(frame, this))
            {
                SetStateWarn(Localized.DraftPage_ExportFrame_OutsideClip);
                return;
            }
            clipId = _selected.Id;
        }
        else if (frame >= ProjectDuration)
        {
            SetStateWarn(Localized.DraftPage_ExportFrame_OutsideProject);
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _frameExportCts = cts;
        try
        {
            if (isPlaying || previewer.HasDynamicPreview) await PauseLivePreview();
            if (AlreadyDisappeared) return;
            SetStateBusy(Localized.DraftPage_RenderOneFrame((int)frame, TimeSpan.FromSeconds(frame * SecondsPerFrame)));
            RenderArtifact artifact;
            string path;
            using (new PreviewBusyScope(this))
            {
                var result = await previewer.RenderPngPreviewAsync(frame, ProjectInfo.RelativeWidth,
                    ProjectInfo.RelativeHeight, clipId, cts.Token);
                artifact = result.Artifact;
                var directory = Path.Combine(FileSystem.CacheDirectory, "sharing-root", "FrameExports", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, clipId is { } id ? $"clip-{id:N}-frame-{frame}.png" : $"frame-{frame}.png");
                using var source = File.OpenRead(result.Path);
                using var destination = File.Create(path);
                await source.CopyToAsync(destination, cts.Token);
            }
            if (AlreadyDisappeared) return;
            Log($"Exported frame {frame}, clip={clipId}, size={artifact.Width}x{artifact.Height}, cacheHit={artifact.CacheHit}.");
            SetStateOK(Localized._Done);
            await ShowExportedFramePopupAsync(path, frame, artifact, selectedClipOnly);
        }
        catch (OperationCanceledException) when (AlreadyDisappeared) { }
        catch (Exception ex)
        {
            Log(ex, $"Export frame {frame}, clip={clipId}", this);
            if (!AlreadyDisappeared)
            {
                SetStateFail(Localized.DraftPage_RenderFail(frame, ex));
                await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(frame, ex), Localized._OK);
            }
        }
        finally
        {
            _frameExportCts = null;
        }
    }

    private async Task ShowExportedFramePopupAsync(string path, uint frame, RenderArtifact artifact, bool selectedClipOnly)
    {
        var title = selectedClipOnly ? Localized.DraftPage_MenuBar_Clip_ExportSelectedClipFrame
            : Localized.DraftPage_MenuBar_Clip_ExportCurrentFrame;
        var feedback = new Label { HorizontalTextAlignment = TextAlignment.Center, IsVisible = false };
        var actions = new FlexLayout { JustifyContent = FlexJustify.Center, Wrap = FlexWrap.Wrap };
        void AddAction(string text, Func<Task> action)
        {
            var button = new Button { Text = text, Margin = new Thickness(4) };
            button.Clicked += async (_, _) =>
            {
                actions.IsEnabled = false;
                feedback.IsVisible = false;
                try { await action(); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log(ex, $"Frame export action: {text}", this);
                    feedback.Text = Localized.DraftPage_ExportFrame_ActionFailed(ex);
                    feedback.IsVisible = true;
                }
                finally { actions.IsEnabled = true; }
            };
            actions.Add(button);
        }

        AddAction(Localized._Save, async () =>
        {
            using var stream = File.OpenRead(path);
            var result = await FileSaver.Default.SaveAsync(Path.GetFileName(path), stream);
            if (result.IsCancelled) return;
            result.EnsureSuccess();
            feedback.Text = Localized._Done;
            feedback.IsVisible = true;
        });
        AddAction(Localized.DraftPage_ExportFrame_Share, async () =>
            await Share.RequestAsync(new ShareFileRequest
            {
                Title = title,
                File = new ShareFile(path, "image/png"),
                PresentationSourceBounds = new Rect(Width / 2, Height / 2, 1, 1)
            }));
        AddAction(Localized.DraftPage_ExportFrame_CopyToClipboard, async () =>
        {
            await CopyExportedFrameToClipboardAsync(path);
            feedback.Text = Localized._Done;
            feedback.IsVisible = true;
        });

        var close = new Button { Text = "×", Padding = 0, WidthRequest = 36, HeightRequest = 36 };
        SemanticProperties.SetDescription(close, Localized.DraftPage_MenuBar_Edit_ClosePopup);
        close.Clicked += async (_, _) => await HidePopup(true);
        var header = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        header.Add(new Label { Text = title, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center });
        header.Add(close, 1);
        var content = new Grid
        {
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto) },
            RowSpacing = 8,
            Padding = 4
        };
        content.Add(header);
        content.Add(new Image { Source = ImageSource.FromFile(path), Aspect = Aspect.AspectFit }, 0, 1);
        content.Add(new Label
        {
            Text = Localized.DraftPage_ExportFrame_Info(frame, artifact.Width, artifact.Height),
            HorizontalTextAlignment = TextAlignment.Center
        }, 0, 2);
        content.Add(actions, 0, 3);
        content.Add(feedback, 0, 4);
        await ShowAPopup(content, mode: "dialog", disableScrollWrapping: true);
    }

    private static async Task CopyExportedFrameToClipboardAsync(string path)
    {
#if WINDOWS
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(
            await Windows.Storage.StorageFile.GetFileFromPathAsync(path)));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
#elif ANDROID
        var context = Android.App.Application.Context;
        using var file = new Java.IO.File(path);
        using var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, context.PackageName + ".fileProvider", file);
        var clipboard = (Android.Content.ClipboardManager?)context.GetSystemService(Android.Content.Context.ClipboardService)
            ?? throw new InvalidOperationException("The clipboard is unavailable.");
        clipboard.PrimaryClip = Android.Content.ClipData.NewUri(context.ContentResolver, Path.GetFileName(path), uri);
#elif iDevices
        using var data = Foundation.NSData.FromFile(path);
        UIKit.UIPasteboard.General.SetData(data, "public.png");
#elif LINUX
        using var texture = Gdk.Texture.NewFromFilename(path);
        var display = Gdk.Display.GetDefault() ?? throw new InvalidOperationException("The display is unavailable.");
        display.GetClipboard().SetTexture(texture);
#else
        throw new PlatformNotSupportedException("Image clipboard is unavailable on this platform.");
#endif
    }
}
