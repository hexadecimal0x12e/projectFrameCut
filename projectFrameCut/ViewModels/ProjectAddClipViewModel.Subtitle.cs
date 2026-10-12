using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.Windows.Input;

namespace projectFrameCut.ViewModels;

public partial class ProjectAddClipViewModel
{
    public ICommand NewSubtitleCommand { get; private set; } = null!;
    public ICommand ImportSubtitleCommand { get; private set; } = null!;

    private void InitializeSubtitleCommands()
    {
        NewSubtitleCommand = new Command(() => AddSubtitle(new(), Localized.Subtitle_Title));
        ImportSubtitleCommand = new Command(async () =>
        {
            if (_draftPage.IsReadonly) return;
            try
            {
                var imported = await SubtitleServices.PickAsync(_draftPage);
                if (imported is not null) AddSubtitle(imported.Value.Result.Data, imported.Value.Name);
            }
            catch (Exception ex)
            {
                Log(ex, "Import subtitle clip", _draftPage);
                await _draftPage.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
            }
        });
    }

    private void AddSubtitle(SubtitleData data, string name)
    {
        if (_draftPage.IsReadonly) return;
        data.Validate();
        uint duration = data.Cues.Count == 0
            ? Math.Max(1u, SettingsManager.GetSettingAs<uint>("Edit_DefaultInfLengthClipLength", 300, 300))
            : Math.Max(1u, SubtitleData.FrameAt(data.Cues.Max(c => c.EndMilliseconds), _draftPage.ProjectInfo.TargetFrameRate));
        data.Style.FontSize = Math.Max(12, _draftPage.ProjectInfo.RelativeHeight * 48f / 1080);
        data.Style.StrokeThickness = Math.Max(1, _draftPage.ProjectInfo.RelativeHeight * 2f / 1080);
        data.BottomMargin = Math.Max(12, _draftPage.ProjectInfo.RelativeHeight * 60f / 1080);
        BeginTimelineClipPlacement((track, start) =>
        {
            var clip = _draftPage.CreateAndAddClip(start, _draftPage.FrameToPixel(duration), track,
                labelText: name, background: new SolidColorBrush(Colors.SlateGray), resolveOverlap: true);
            clip.ClipType = ClipMode.SubtitleClip;
            clip.FromPlugin = InternalPluginBase.InternalPluginBaseID;
            clip.isInfiniteLength = true;
            clip.sourceSecondPerFrame = 1f / _draftPage.ProjectInfo.TargetFrameRate;
            clip.TargetWidth = _draftPage.ProjectInfo.RelativeWidth;
            clip.TargetHeight = _draftPage.ProjectInfo.RelativeHeight;
            clip.ExtraData[SubtitleData.MetadataKey] = data.Clone();
            return clip;
        }, useSubTrack: true, name: name);
        ClipAdded?.Invoke(this, EventArgs.Empty);
    }
}
