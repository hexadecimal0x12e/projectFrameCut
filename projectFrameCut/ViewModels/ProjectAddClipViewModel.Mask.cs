using projectFrameCut.DraftStuff;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.Sources;
using projectFrameCut.Shared;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace projectFrameCut.ViewModels;

public partial class ProjectAddClipViewModel
{
    public ObservableCollection<MaskSourceItemViewModel> MaskSourcesToAdd { get; } = new();
    private readonly Dictionary<string, MaskSourceItemViewModel> maskItems = new();
    private int maskLoadVersion;

    public async Task LoadMaskSources()
    {
        if (!MainThread.IsMainThread)
        {
            await MainThread.InvokeOnMainThreadAsync(LoadMaskSources);
            return;
        }
        int version = ++maskLoadVersion;
        try
        {
            var sources = MaskSources.GetAvailable().Where(s => s is not RemoteRpcMaskSource).ToDictionary(s => s.TypeName);
            if (version != maskLoadVersion) return;
            var cached = new Dictionary<string, MaskSourceItemViewModel>(maskItems);
            var items = await Task.Run(() => sources.Values.Select(source => cached.GetValueOrDefault(source.TypeName)
                ?? new MaskSourceItemViewModel(this, source)).ToArray());
            if (version != maskLoadVersion) return;
            maskItems.Clear();
            MaskSourcesToAdd.Clear();
            foreach (var item in items)
            {
                maskItems[item.TypeName] = item;
                if (string.IsNullOrWhiteSpace(SearchText) || item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                    || item.TypeName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)) MaskSourcesToAdd.Add(item);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "Load mask sources", this);
        }
    }

    internal void AddMaskSource(string sourceType, string name)
    {
        BeginTimelineClipPlacement((track, start) =>
        {
            var clip = _draftPage.CreateAndAddClip(start, _draftPage.FrameToPixel(_draftPage.ProjectInfo.TargetFrameRate * 3),
                track, null, name, new SolidColorBrush(Colors.Teal), resolveOverlap: true);
            clip.ClipType = ClipMode.MaskClip;
            clip.TypeName = nameof(MaskClip);
            clip.FromPlugin = InternalPluginBase.InternalPluginBaseID;
            clip.isInfiniteLength = true;
            clip.maxFrameCount = 0;
            clip.sourceSecondPerFrame = 1f / Math.Max(1u, _draftPage.ProjectInfo.TargetFrameRate);
            int divisor = sourceType == "Passthrough" ? 1 : 2;
            clip.TargetWidth = Math.Max(1, _draftPage.ProjectInfo.RelativeWidth / divisor);
            clip.TargetHeight = Math.Max(1, _draftPage.ProjectInfo.RelativeHeight / divisor);
            clip.TargetX = (_draftPage.ProjectInfo.RelativeWidth - clip.TargetWidth) / 2;
            clip.TargetY = (_draftPage.ProjectInfo.RelativeHeight - clip.TargetHeight) / 2;
            clip.AllowFreeScaleResize = true;
            clip.ExtraData[MaskClip.SourceTypeKey] = sourceType;
            ClipInfoBuilder.EnsureClipArgumentProvider(clip, _draftPage);
            Log($"Created mask {clip.Id}, source {sourceType}.");
            return clip;
        }, name: name);
        ClipAdded?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class MaskSourceItemViewModel
{
    public string TypeName { get; }
    public string Name { get; }
    public ImageSource? PreviewSource { get; }
    public ICommand AddCommand { get; }

    public MaskSourceItemViewModel(ProjectAddClipViewModel parent, MaskSource source)
    {
        TypeName = source.TypeName;
        Name = PluginManager.GetLocalizationItem("MaskSource_" + source.TypeName, source.TypeName);
        AddCommand = new Command(() => parent.AddMaskSource(source.TypeName, Name));
        try
        {
            using var sample = new Picture8bpp(160, 96);
            for (int y = 0; y < sample.Height; y++)
            {
                for (int x = 0; x < sample.Width; x++)
                {
                    int i = y * sample.Width + x;
                    bool stripe = (x + y) / 18 % 2 == 0;
                    sample.r[i] = (byte)(stripe ? 232 : 52);
                    sample.g[i] = (byte)(64 + x * 160 / (sample.Width - 1));
                    sample.b[i] = (byte)(stripe ? 80 : 220);
                }
            }
            var preview = source.Process(sample, new Dictionary<string, object>(), 1, CancellationToken.None);
            using var ownedPreview = ReferenceEquals(preview, sample) ? null : preview;
            using var stream = new MemoryStream();
            preview.SaveToPng(stream);
            var data = stream.ToArray();
            PreviewSource = ImageSource.FromStream(() => new MemoryStream(data, writable: false));
            LogDiagnostic($"Generated mask source thumbnail for {source.TypeName}.");
        }
        catch (Exception ex)
        {
            Log(ex, $"Generate mask source thumbnail for {source.TypeName}", this);
        }
    }
}
