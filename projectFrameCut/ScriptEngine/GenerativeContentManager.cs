using projectFrameCut.AIAssistance;
using projectFrameCut.Shared;
using Microsoft.Maui.Storage;
using System.Management.Automation;
using System.Runtime.ExceptionServices;

namespace projectFrameCut.ScriptEngine
{
    internal static class GenerativeContentManager
    {
        internal static string GetWorkspacePath()
        {
            return Path.GetFullPath(Path.Combine(FileSystem.CacheDirectory, "ScriptWorkspace"));
        }

        internal static string ResolveOutputPath(string? fileName, string extension)
        {
            var name = string.IsNullOrWhiteSpace(fileName)
                ? $"AIGenerated-{Guid.NewGuid():N}{extension}"
                : fileName;

            if (Path.IsPathRooted(name) ||
                name != Path.GetFileName(name) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("FileName must be a file name inside the script workspace.");
            }

            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                name += extension;

            var workspace = GetWorkspacePath();
            Directory.CreateDirectory(workspace);
            var outputPath = Path.GetFullPath(Path.Combine(workspace, name));
            var workspacePrefix = workspace.EndsWith(Path.DirectorySeparatorChar)
                ? workspace
                : workspace + Path.DirectorySeparatorChar;

            if (!outputPath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output path must remain inside the script workspace.");

            return outputPath;
        }

        internal static T RunSynchronously<T>(Func<Task<T>> operation)
        {
            // PSCmdlet.ProcessRecord is synchronous. Run the async provider call on
            // the thread pool so it never waits on the caller's UI synchronization context.
            var task = Task.Run(operation);
            try
            {
                task.Wait();
                return task.Result;
            }
            catch (AggregateException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
                throw;
            }
        }

        internal static byte[] Download(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("The AI provider returned an invalid download URL.");
            }

            using var client = new HttpClient();
            return RunSynchronously(async () =>
                await client.GetByteArrayAsync(uri).ConfigureAwait(false));
        }
    }

    [Cmdlet(VerbsCommon.New, "AIGeneratedImage", SupportsShouldProcess = true)]
    public sealed class NewAIGeneratedImageCommand : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string? Prompt { get; set; }

        [Parameter]
        public string? NegativePrompt { get; set; }

        [Parameter]
        public ImageStyle Style { get; set; } = ImageStyle.Natural;

        [Parameter]
        public ImageQuality Quality { get; set; } = ImageQuality.Standard;

        [Parameter]
        [ValidateRange(1, 16384)]
        public int Width { get; set; } = 1024;

        [Parameter]
        [ValidateRange(1, 16384)]
        public int Height { get; set; } = 1024;

        [Parameter]
        public string? FileName { get; set; }

        protected override void ProcessRecord()
        {
            if (string.IsNullOrWhiteSpace(Prompt))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException("Prompt is required."),
                    "InvalidArgument", ErrorCategory.InvalidArgument, null));
                return;
            }

            try
            {
                var outputPath = GenerativeContentManager.ResolveOutputPath(FileName, ".png");
                if (!ShouldProcess(outputPath, "Generate AI image"))
                    return;

                var result = GenerativeContentManager.RunSynchronously(() =>
                    AIHelper.GenerateImageAsync(Prompt, new ImageGenerationOptions
                {
                    Width = Width,
                    Height = Height,
                    Style = Style,
                    Quality = Quality,
                    NegativePrompt = NegativePrompt
                }));

                if (!result.Success || string.IsNullOrWhiteSpace(result.ImageUrl))
                {
                    WriteError(new ErrorRecord(
                        new InvalidOperationException(result.ErrorMessage ?? "AI image generation failed."),
                        "AIGenerationFailed", ErrorCategory.InvalidOperation, Prompt));
                    return;
                }

                File.WriteAllBytes(outputPath,
                    GenerativeContentManager.Download(result.ImageUrl));
                WriteObject(new PSObject(new
                {
                    Type = "Image",
                    Path = outputPath,
                    Prompt,
                    Width,
                    Height,
                    Style = Style.ToString(),
                    Quality = Quality.ToString()
                }));
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "AIGeneratedImageFailed", ErrorCategory.NotSpecified, Prompt));
            }
        }
    }

    [Cmdlet(VerbsCommon.New, "AIGeneratedVideo", SupportsShouldProcess = true)]
    public sealed class NewAIGeneratedVideoCommand : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string? Prompt { get; set; }

        [Parameter]
        [ValidateRange(1, 16384)]
        public int Width { get; set; } = 1280;

        [Parameter]
        [ValidateRange(1, 16384)]
        public int Height { get; set; } = 720;

        [Parameter]
        [ValidateRange(1, 600)]
        public int Duration { get; set; } = 15;

        [Parameter]
        public bool GenerateAudio { get; set; } = true;

        [Parameter]
        public string? FileName { get; set; }

        protected override void ProcessRecord()
        {
            if (string.IsNullOrWhiteSpace(Prompt))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException("Prompt is required."),
                    "InvalidArgument", ErrorCategory.InvalidArgument, null));
                return;
            }

            try
            {
                var outputPath = GenerativeContentManager.ResolveOutputPath(FileName, ".mp4");
                if (!ShouldProcess(outputPath, "Generate AI video"))
                    return;

                var result = GenerativeContentManager.RunSynchronously(() =>
                    AIHelper.GenerateVideoAsync(Prompt, new VideoGenerationOptions
                {
                    Width = Width,
                    Height = Height,
                    Duration = Duration,
                    GenerateAudio = GenerateAudio
                }));

                if (!result.Success || string.IsNullOrWhiteSpace(result.VideoUrl))
                {
                    WriteError(new ErrorRecord(
                        new InvalidOperationException(result.ErrorMessage ?? "AI video generation failed."),
                        "AIGenerationFailed", ErrorCategory.InvalidOperation, Prompt));
                    return;
                }

                File.WriteAllBytes(outputPath,
                    GenerativeContentManager.Download(result.VideoUrl));
                WriteObject(new PSObject(new
                {
                    Type = "Video",
                    Path = outputPath,
                    Prompt,
                    Width,
                    Height,
                    Duration,
                    GenerateAudio,
                    TaskId = result.TaskId
                }));
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "AIGeneratedVideoFailed", ErrorCategory.NotSpecified, Prompt));
            }
        }
    }
}
