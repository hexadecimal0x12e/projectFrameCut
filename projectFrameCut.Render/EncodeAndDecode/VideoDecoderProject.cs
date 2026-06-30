using projectFrameCut.Render.Benchmark;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public class DecoderContextPJFCProject : IVideoSource
    {
        public static Func<ConcurrentDictionary<string, AssetItem>>? GlobalAssetGetter = null;

        public static Func<string, string?>? RemotePathResolver = null;

        public string TypeName => "DecoderContextPJFCProject";

        public int? ResultBitPerPixel { get; private set; } = null;

        public string[] PreferredExtension => [".pjfc"];

        public uint Index { get; set; }

        public long TotalFrames => timeline is null ? -1 : Math.Max(timeline?.Duration ?? 0, timeline?.AudioDuration ?? 0);

        public double Fps => project?.TargetFrameRate ?? 0;

        public int Width => project?.RelativeWidth ?? 0;

        public int Height => project?.RelativeHeight ?? 0;

        public bool Disposed { get; private set; } = false;

        public bool EnableLock { get; set; }
        public bool StrictMode { get; set; }
        public bool EnableMemoryCache { get; set; }
        public bool EnableDiskCache { get; set; }

        public string ProjectRoot { get; private set; } = "";

        ProjectJSONStructure project = new();
        DraftStructureJSON timeline = new();

        public IVideoSource CreateNew(string newSource) => new DecoderContextPJFCProject(newSource);

        Renderer renderer;
        public IPicture.PicturePixelMode TargetPPB = IPicture.PicturePixelMode.BytePicture;

        private bool inited = false;

        public DecoderContextPJFCProject()
        {

        }

        public DecoderContextPJFCProject(string? newSource)
        {
            if (newSource is null) return;

            if (newSource.EndsWith("@8") || newSource.EndsWith("@10") || newSource.EndsWith("@16"))
            {
                var parts = newSource.Split('@', 2);
                if (parts.Length != 2) throw new ArgumentException($"The source {newSource} is not a valid .pjfc project file with bit depth suffix.");
                newSource = parts[0];
                switch (parts[1])
                {
                    case "8":
                        TargetPPB = IPicture.PicturePixelMode.BytePicture;
                        break;
                    case "16":
                        TargetPPB = IPicture.PicturePixelMode.UShortPicture;
                        break;
                    default:
                        throw new ArgumentException($"The bit depth suffix {parts[1]} in source {newSource} is not recognized. It should be one of @8 or @16.");
                }
            }
            if (File.Exists(newSource) && Path.GetExtension(newSource).Equals(".pjfc", StringComparison.OrdinalIgnoreCase))
            {
                ProjectRoot = Path.GetDirectoryName(newSource) ?? throw new InvalidDataException("Cannot find project root. Try provide full path of project.");
                Initialize();
            }
            else
            {
                throw new ArgumentException($"The source {newSource} is not a valid .pjfc project file.");
            }
        }

        public void Dispose()
        {
            renderer.ClearCaches();
        }

        public IPicture GetFrame(uint targetFrame, bool hasAlpha = false)
        {
            if (renderer is null) throw new ArgumentNullException("Render is not inited yet.");
            var cts = new CancellationTokenSource();
            return renderer.RenderSpecificFrame(targetFrame, cts.Token) ?? throw new InvalidOperationException("Render does not return valid data.");

        }

        public void Initialize()
        {
            if (string.IsNullOrWhiteSpace(ProjectRoot) || !Path.Exists(ProjectRoot) || inited) return;
            try
            {
                ConcurrentDictionary<string, AssetItem> assets = GlobalAssetGetter?.Invoke() ?? new();
                if (File.Exists(Path.Combine(ProjectRoot, "project.pjfc")))
                {
                    project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(ProjectRoot, "project.pjfc")), savingOpts) ?? new();
                }
                else
                {
                    throw new FileNotFoundException("project.pjfc not found in project directory.");
                }

                if (File.Exists(Path.Combine(ProjectRoot, "timeline.json")))
                {
                    timeline = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(ProjectRoot, "timeline.json")), savingOpts) ?? new();
                }
                else
                {
                    throw new FileNotFoundException("timeline.json not found in project directory.");
                }
                if (File.Exists(Path.Combine(ProjectRoot, "assets.json")))
                {
                    var projAssets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(ProjectRoot, "assets.json")), savingOpts) ?? new();
                    ConcurrentDictionary<string, AssetItem> assetDict = new ConcurrentDictionary<string, AssetItem>(projAssets.ToDictionary((AssetItem a) => a.AssetId ?? $"unknown+{Random.Shared.Next()}", (AssetItem a) => a));
                    assets = new ConcurrentDictionary<string, AssetItem>(assets.Concat(assetDict));
                }
                else
                {
                    throw new FileNotFoundException("assets.json not found in project directory.");
                }
                var origWorkdir = Environment.CurrentDirectory;
                Environment.CurrentDirectory = ProjectRoot;
                renderer = new Renderer
                {
                    builder = new VideoBuilder(new BlackholeVideoWriter()) { StrictMode = false, AllowDuplicatedFrameWrite = true, EnablePreview = false, DisposeFrameAfterEachWrite = false, DoGCAfterEachWrite = false, BlockWrite = true },
                    Clips = JSONToIClips(timeline, assets, TargetPPB),
                    StartFrame = 0,
                    Use16Bit = TargetPPB == IPicture.PicturePixelMode.UShortPicture,
                    UseHDR = project.Properties.TryGetValue("EnableHDR", out var enableHDR) && bool.TryParse(enableHDR, out var enableHDRBool) && enableHDRBool,
                    ProjectRelativeHeight = project.RelativeHeight,
                    ProjectRelativeWidth = project.RelativeWidth,
                    Duration = Math.Max(timeline.Duration, timeline.AudioDuration),
                    MaxThreads = 1,
                    MaximumHDRBrightness = project.Properties.TryGetValue("HdrMaximumBrightness", out var maxHdrBrightness) && int.TryParse(maxHdrBrightness, out var maxHdrBrightnessInt) ? maxHdrBrightnessInt : 1000,
                    SDRClipsBrightnessInHDRMode =
                        project.Properties.TryGetValue("SdrClipBrightness", out var sdrBrightnessInHdr) && int.TryParse(sdrBrightnessInHdr, out var sdrBrightnessInHdrInt)
                            ? sdrBrightnessInHdrInt
                            : (project.Properties.TryGetValue("sdrClipBrightness", out var legacySdrBrightnessInHdr) && int.TryParse(legacySdrBrightnessInHdr, out var legacySdrBrightnessInHdrInt)
                                ? legacySdrBrightnessInHdrInt
                                : 203),
                    TargetHeight = project.RelativeHeight,
                    TargetWidth = project.RelativeWidth
                };

                renderer.PrepareRender(CancellationToken.None);
                renderer.InitializeRenderCaches();
                Environment.CurrentDirectory = origWorkdir;
                inited = true;
            }
            catch (Exception ex)
            {
                Log(ex, $"init project-source videosource in {ProjectRoot}", this);
            }

        }

        private IClip[] JSONToIClips(DraftStructureJSON json, IDictionary<string, AssetItem> assets, IPicture.PicturePixelMode bpp)
        {
            var elements = (JsonSerializer.SerializeToElement(json).Deserialize<DraftStructureJSON>()?.Clips) ?? throw new NullReferenceException("Failed to cast ClipDraftDTOs to IClips."); //I don't want to write a lot of code to clone attributes from dto to IClip, it's too hard and may cause a lot of mystery bugs.

            var clipsList = new List<IClip>();

            foreach (var clip in elements.Cast<JsonElement>())
            {
                if (clip.TryGetProperty("ClipType", out var clipTypeProp)
                    && clipTypeProp.ValueKind == JsonValueKind.Number
                    && clipTypeProp.TryGetInt32(out var clipTypeValue)
                    && (ClipMode)clipTypeValue == ClipMode.MarkingClip)
                {
                    continue;
                }

                var clipInstance = PluginManager.CreateClip(clip);
                if (clipInstance.FilePath?.StartsWith('$') ?? false)
                {
                    try
                    {
                        var id = clipInstance.FilePath.Substring(1);
                        if (!assets.TryGetValue(id, out var value))
                        {
                            throw new FileNotFoundException($"Asset for clip {clipInstance.Name} ({clipInstance.TypeName}) '{id}' is not exist.");
                        }
                        clipInstance.FilePath = value.Path;
                    }
                    catch (InvalidOperationException)
                    {
                        //safe to ignore
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
                else if (string.IsNullOrEmpty(clipInstance.FilePath) && clip.TryGetProperty("FilePath", out var fp) && clipInstance.NeedFilePath)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(fp.GetString())) throw new InvalidDataException($"Clip {clipInstance.Name} ({clipInstance.TypeName}) has empty FilePath, which is not valid for clips that need file path.");
                        if (Path.IsPathRooted(fp.GetString()))
                        {
                            clipInstance.FilePath = fp.GetString();
                        }
                        else
                        {
                            clipInstance.FilePath = Path.Combine(ProjectRoot, fp.GetString());
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        //safe to ignore
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }


                clipInstance.ReInit(bpp);
                clipsList.Add(clipInstance);

            }
            return clipsList.ToArray();
        }

        static readonly JsonSerializerOptions savingOpts = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    }
}
