using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.FontHelper;
using projectFrameCut.Drawing.Text.Typology;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Subtitles;
using projectFrameCut.Shared;
using projectFrameCut.Shared.Text;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class SubtitleTests
{
    private sealed class ProbeClip : SubtitleClip
    {
        public IReadOnlyList<TextEntry> Entries(uint sourceFrame) => GetRawEntries(sourceFrame);
    }

    [TestMethod]
    public void ImportsSrtWithoutInterpretingLiteralMarkdown()
    {
        var data = SubtitleImporter.Parse("1\r\n00:00:01,250 --> 00:00:02,500\r\n<b>你好</b> *literal*\r\nsecond &amp; line\r\n", ".srt").Data;
        Assert.AreEqual(1250L, data.Cues[0].StartMilliseconds);
        Assert.AreEqual(2500L, data.Cues[0].EndMilliseconds);
        var entry = data.BuildEntry(data.Cues[0]);
        Assert.AreEqual("你好 *literal*\nsecond & line", entry.Text);
        Assert.AreEqual("Bold", entry.GetRangesAt(0).First().Style.FontStyle);
        Assert.AreEqual("Regular", entry.GetRangesAt(entry.Text.IndexOf("literal", StringComparison.Ordinal)).First().Style.FontStyle);
    }

    [TestMethod]
    public void ImportsWebVttMetadataAndBasicFormatting()
    {
        var result = SubtitleImporter.Parse("\uFEFFWEBVTT\n\nNOTE author\nignored\n\nSTYLE\n::cue { color: red; }\n\ncue-id\n00:01.000 --> 00:02.000 align:right\n<v Speaker><i>hello</i> <u>world</u>\n", "vtt");
        var entry = result.Data.BuildEntry(result.Data.Cues.Single());
        Assert.AreEqual("hello world", entry.Text);
        Assert.AreEqual("Italic", entry.GetRangesAt(0).First().Style.FontStyle);
        Assert.AreEqual(TextDecoration.Underline, entry.GetRangesAt(6).First().Style.Decoration);
        Assert.AreEqual(TextAlignment.Center, result.Data.Style.Alignment);
        Assert.IsNotEmpty(result.Warnings);
    }

    [TestMethod]
    public void ImportsAssAndSsaByTheirDeclaredColumns()
    {
        foreach (string format in new[] { "ass", "ssa" })
        {
            string text = "[V4+ Styles]\nFormat: Name, Fontname, Bold, Italic, Underline, StrikeOut\nStyle: Default,Arial,-1,0,0,0\n" +
                "[Events]\nFormat: Layer, Start, End, Style, Text\nDialogue: 0,0:00:01.25,0:00:02.50,Default,one,{\\i1}two\\N{\\r}three\n";
            var result = SubtitleImporter.Parse(text, format);
            var entry = result.Data.BuildEntry(result.Data.Cues.Single());
            Assert.AreEqual("one,two\nthree", entry.Text);
            Assert.AreEqual("BoldItalic", entry.GetRangesAt(4).First().Style.FontStyle);
            Assert.AreEqual("Bold", entry.GetRangesAt(8).First().Style.FontStyle);
            Assert.AreEqual("HarmonyOS Sans SC", entry.FontName);
            Assert.AreEqual(1250L, result.Data.Cues[0].StartMilliseconds);
        }
    }

    [TestMethod]
    public void DiscardsAssDrawingWithoutDisplayingItsCommands()
    {
        var result = SubtitleImporter.Parse("[Events]\nFormat: Start, End, Text\nDialogue: 0:00:00.00,0:00:02.00,{\\p1}m 0 0 l 1 1{\\p0}text{\\t(0,100,\\i1)}\n", "ass");
        Assert.AreEqual("text", result.Data.BuildEntry(result.Data.Cues.Single()).Text);
        Assert.IsNotEmpty(result.Warnings);
    }

    [TestMethod]
    public void RejectsInvalidTimesAndOverlapsButAllowsAdjacentCues()
    {
        var data = new SubtitleData { Cues = [new() { StartMilliseconds = 1000, EndMilliseconds = 2000 }, new() { StartMilliseconds = 2000, EndMilliseconds = 3000 }] };
        data.Validate();
        data.Cues[1].StartMilliseconds = 1999;
        StringAssert.Contains(Assert.ThrowsExactly<InvalidDataException>(data.Validate).Message, "Overlapping");
        data.Cues[1].StartMilliseconds = 3000;
        Assert.ThrowsExactly<InvalidDataException>(data.Validate);
        Assert.ThrowsExactly<FormatException>(() => SubtitleData.ParseTime("-1"));
        Assert.ThrowsExactly<FormatException>(() => SubtitleData.ParseTime("00:60:00.000"));
        Assert.ThrowsExactly<InvalidDataException>(() => SubtitleImporter.Parse("1\ninvalid\ntext", "srt"));
        Assert.ThrowsExactly<InvalidDataException>(() => SubtitleImporter.Parse("", "srt"));
    }

    [TestMethod]
    public void MarkdownPreviewAndRenderingShareNestedAndEscapedRuns()
    {
        var runs = MarkdownText.Parse("**中文 *斜体* ** ++下划线++ ~~删除~~ `**code**` \\*literal\\*");
        Assert.AreEqual("中文 斜体  下划线 删除 **code** *literal*", string.Concat(runs.Select(r => r.Text)));
        Assert.IsTrue(runs.Single(r => r.Text == "斜体").Style.HasFlag(MarkdownTextStyle.Bold | MarkdownTextStyle.Italic));
        Assert.IsTrue(runs.Single(r => r.Text == "**code**").Style.HasFlag(MarkdownTextStyle.Code));
        Assert.AreEqual("unclosed **text", string.Concat(MarkdownText.Parse("unclosed **text").Select(r => r.Text)));
        runs = MarkdownText.Parse("**外层 *内层***");
        Assert.AreEqual("外层 内层", string.Concat(runs.Select(r => r.Text)));
        Assert.AreEqual(MarkdownTextStyle.Bold | MarkdownTextStyle.Italic, runs[^1].Style);
        Assert.AreEqual("C:\\", MarkdownText.Parse("`C:\\`").Single().Text);
        Assert.AreEqual("a```b", MarkdownText.Parse("``a```b``").Single().Text);
    }

    [TestMethod]
    public void SourceFramesRespectHalfOpenCueIntervalsAndTimelineMapping()
    {
        using var clip = new ProbeClip
        {
            Id = Guid.NewGuid(), Name = "subtitles", StartFrame = 200, RelativeStartFrame = 25, Duration = 200,
            FrameTime = 1f / 25, ExtraData = new() { [ClipDraftDTO.ProjectFrameRateMetaKey] = 50u },
            SubtitleData = new() { Cues = [new() { StartMilliseconds = 1000, EndMilliseconds = 2000, Markdown = "first" }, new() { StartMilliseconds = 2000, EndMilliseconds = 2500, Markdown = "second" }] }
        };
        Assert.IsEmpty(clip.Entries(49));
        Assert.AreEqual("first", clip.Entries(50).Single().Text);
        Assert.AreEqual("second", clip.Entries(100).Single().Text);
        Assert.IsEmpty(clip.Entries(125));
        IClip timing = clip;
        Assert.AreEqual("first", clip.Entries(timing.GetRelativeFrameIndex(225)!.Value).Single().Text);
        timing.SpeedVarianceProviderInstance = new ClassicSpeedVarianceProvider { Ratio = 2 };
        Assert.IsTrue(timing.GetRelativeFrameIndex(225)!.Value < 50);
        Assert.AreEqual("first", clip.Entries(timing.GetRelativeFrameIndex(250)!.Value).Single().Text);
        Assert.AreEqual(2u, SubtitleData.FrameAt(34, 30));
        Assert.AreEqual(30u, SubtitleData.FrameAt(1000, 30));
    }

    [TestMethod]
    public void WholeDraftSubtitlesUseProjectTimeBeyondOriginalClipRange()
    {
        using var clip = new ProbeClip
        {
            Id = Guid.NewGuid(), Name = "subtitles", StartFrame = 200, RelativeStartFrame = 25, Duration = 50,
            ExtendToWholeDraft = true, FrameTime = 1f / 25,
            ExtraData = new() { [ClipDraftDTO.ProjectFrameRateMetaKey] = 50u },
            SpeedVarianceProviderInstance = new ClassicSpeedVarianceProvider { Ratio = 2 },
            SubtitleData = new() { Cues = [new() { StartMilliseconds = 0, EndMilliseconds = 1000, Markdown = "first" }, new() { StartMilliseconds = 2000, EndMilliseconds = 4000, Markdown = "second" }] }
        };
        IClip timing = clip;
        Assert.AreEqual("first", clip.Entries(timing.GetRelativeFrameIndex(0)!.Value).Single().Text);
        Assert.IsEmpty(clip.Entries(timing.GetRelativeFrameIndex(50)!.Value));
        Assert.AreEqual("second", clip.Entries(timing.GetRelativeFrameIndex(100)!.Value).Single().Text);
        Assert.IsEmpty(clip.Entries(timing.GetRelativeFrameIndex(200)!.Value));
        Assert.AreEqual(300u, timing.GetRelativeFrameIndex(300)!.Value);
        clip.Duration = 0;
        Assert.AreEqual(uint.MaxValue, timing.GetRelativeFrameIndex(uint.MaxValue)!.Value);

        clip.Duration = 50;
        clip.ExtendToWholeDraft = false;
        Assert.IsNull(timing.TryGetRelativeFrameIndex(0, null));
        Assert.AreEqual(25u, timing.GetRelativeFrameIndex(200)!.Value);
        Assert.IsNull(timing.TryGetRelativeFrameIndex(200 + timing.GetEffectiveDuration(), null));
        Assert.AreEqual(999u, timing.TryGetRelativeFrameIndex(300, 999)!.Value);
    }

    [TestMethod]
    public void SubtitleDataRoundTripsThroughExistingDraftMetadata()
    {
        var original = new SubtitleData { Cues = [new() { StartMilliseconds = 100, EndMilliseconds = 1100, Markdown = "**中文** 👋" }] };
        var dto = new ClipDraftDTO
        {
            Id = Guid.NewGuid(), Name = "subtitle", ClipType = ClipMode.SubtitleClip,
            FromPlugin = InternalPluginBase.InternalPluginBaseID,
            MetaData = new() { [SubtitleData.MetadataKey] = original, [ClipDraftDTO.ProjectFrameRateMetaKey] = 60u }
        };
        var json = JsonSerializer.SerializeToElement(dto);
        var restored = (SubtitleClip)((projectFrameCut.Render.RenderAPIBase.Plugins.IPluginBase)new InternalPluginBase()).ClipCreator(json);
        restored.ExtraData = JsonSerializer.Deserialize<ClipDraftDTO>(json)!.MetaData!;
        restored.ReInit(8);
        Assert.AreEqual(original.Cues[0].Markdown, restored.SubtitleData.Cues[0].Markdown);
        Assert.AreEqual(original.Cues[0].Id, restored.SubtitleData.Cues[0].Id);
        Assert.IsFalse(restored.NeedFilePath);
        Assert.IsFalse(restored is IImmutableContentClip);
        Assert.AreEqual(ClipMode.SubtitleClip, restored.ClipType);
        restored.Dispose();
    }

    [TestMethod]
    public async Task DetectsEncodingErrorsAndReadsBomFiles()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".srt");
        try
        {
            await File.WriteAllTextAsync(path, "1\n00:00:00,000 --> 00:00:01,000\n中文", Encoding.Unicode);
            Assert.AreEqual("中文", (await SubtitleImporter.ReadAsync(path)).Data.Cues.Single().Markdown);
            await File.WriteAllBytesAsync(path, [0xff, 0x80, 0x80]);
            await Assert.ThrowsExactlyAsync<DecoderFallbackException>(async () => await SubtitleImporter.ReadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void PixelSpaceConversionPreservesAndScalesRichTextRanges()
    {
        var rich = new RichTextEntry
        {
            Text = "ab", FontName = "test", FontSize = 40,
            StyledRanges = [new() { Start = 1, Length = 1, Style = new() { FontSize = 80, StrokeThickness = 4, CharacterSpacing = 2, Decoration = TextDecoration.Underline } }]
        };
        var ctx = TextLayoutContext.FromCanvas(1920, 1080);
        var engine = (RichTextEntry)TextLayoutPipeline.ToEngineSpace(rich, ctx);
        Assert.AreEqual(80f / 1080, engine.StyledRanges[0].Style.FontSize!.Value, .00001f);
        var restored = (RichTextEntry)TextLayoutPipeline.FromEngineSpace(engine, ctx);
        Assert.AreEqual(80f, restored.StyledRanges[0].Style.FontSize!.Value, .0001f);
        Assert.AreEqual(80f, rich.StyledRanges[0].Style.FontSize!.Value);
        Assert.AreEqual(TextDecoration.Underline, restored.StyledRanges[0].Style.Decoration);
    }

    [TestMethod]
    public void WrappingRetainsFormattingAcrossLinesAndEmojiClusters()
    {
        string? fontPath = FindFont();
        if (fontPath is null) Assert.Inconclusive("Packaged test font is unavailable.");
        using var font = FontFace.Load(fontPath!);
        var engine = new NormalTypesettingEngine { FontResolver = (_, _) => font };
        var data = new SubtitleData();
        data.Style.FontSize = .1f;
        data.Style.StrokeThickness = 0;
        var source = data.BuildEntry(new() { Markdown = "plain **粗体👩‍💻继续** ++下划线++" });
        var wrapped = engine.Wrap(source, font, .2f);
        Assert.AreEqual(source.Text, wrapped.Text.Replace("\n", ""));
        Assert.IsTrue(wrapped.Text.Contains('\n'));
        Assert.IsTrue(wrapped.Text.Contains("👩‍💻", StringComparison.Ordinal));
        for (int i = 0, j = 0; i < wrapped.Text.Length; i++)
        {
            if (wrapped.Text[i] == '\n') continue;
            Assert.AreEqual(source.GetRangesAt(j++).FirstOrDefault()?.Style, wrapped.GetRangesAt(i).FirstOrDefault()?.Style);
        }
        Assert.IsTrue(engine.Layout(wrapped, font).Elements.OfType<ShapeCanvasElement>().Any());
        var bounds = engine.MeasureInkBounds(wrapped, font);
        Assert.IsTrue(bounds.MaxY > bounds.MinY);
    }

    [TestMethod]
    public void RegisteredFontNameMatchesPickerMetadata()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "simfang.ttf");
        if (!File.Exists(path)) Assert.Inconclusive("FangSong is unavailable.");
        var meta = FontMetadataReader.ReadFromFile(path);
        string name = meta.UniqueName ?? $"{meta.FamilyName} {meta.SubfamilyName}";
        TextClipFontRegistry.Clear();
        try
        {
            TextClipFontRegistry.AddFont(path);
            Assert.IsTrue(TextClipFontRegistry.TryGetFont(name, out var font));
            Assert.IsNotNull(font);
            foreach (char c in "你好测试内容删除") Assert.IsTrue(font.CanDisplayTheChar(c));
        }
        finally { TextClipFontRegistry.Clear(); }
    }

    [TestMethod]
    public void InlineCodeFallsBackToChineseFontOnFirstInitialization()
    {
        string? chinese = FindFont(), mono = FindFont("SourceCodePro-Regular.ttf");
        if (chinese is null || mono is null) Assert.Inconclusive("Packaged test fonts are unavailable.");
        TextClipFontRegistry.Clear();
        try
        {
            TextClipFontRegistry.AddFont(chinese!);
            TextClipFontRegistry.AddFont(mono!);
            TextClipFontRegistry.Initialize();
            Assert.IsTrue(TextClipFontRegistry.FallbackFonts.Any(f => f.FamilyName == "HarmonyOS Sans SC"));
            var font = TextClipFontRegistry.ResolveFont("Source Code Pro", "Regular");
            Assert.IsNotNull(font);
            Assert.IsFalse(font.CanDisplayTheChar('中'));
            var data = new SubtitleData();
            data.Style.FontName = "Source Code Pro";
            data.Style.FontSize = .1f;
            var entry = data.BuildEntry(new() { Markdown = "`中文`" });
            var picture = new NormalTypesettingEngine
            {
                FallbackFonts = TextClipFontRegistry.FallbackFonts,
                FontResolver = TextClipFontRegistry.ResolveFont
            }.Layout(entry, font);
            Assert.AreEqual(2, picture.Elements.Count);
            Assert.IsTrue(picture.Elements.All(e => e.Draw().Length > 0));
        }
        finally { TextClipFontRegistry.Clear(); }
    }

    private static string? FindFont(string fileName = "HarmonyOS_Sans_SC_Regular.ttf")
    {
        foreach (string root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, "projectFrameCut", "Resources", "Fonts", fileName);
                if (File.Exists(path)) return path;
            }
        return null;
    }
}
