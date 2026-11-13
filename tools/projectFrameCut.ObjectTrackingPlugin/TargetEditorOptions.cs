#if WINDOWS
namespace projectFrameCut.ObjectTrackingPlugin;

internal sealed record TargetEditorOptions(string PipeName, Guid ClipId, Guid ProviderId, string FieldId, int ParentProcessId)
{
    public static TargetEditorOptions Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var arg in args.Skip(1))
        {
            int separator = arg.IndexOf('=');
            if (separator < 0) throw new ArgumentException("Editor arguments must use --name=value.");
            var key = arg[..separator];
            if (key is not ("--pipe" or "--clipId" or "--providerId" or "--fieldId" or "--parentPid"))
                throw new ArgumentException($"Unknown editor argument '{key}'.");
            if (!options.TryAdd(key, arg[(separator + 1)..]))
                throw new ArgumentException($"Duplicate editor argument '{key}'.");
        }

        if (!options.TryGetValue("--pipe", out var pipe) || string.IsNullOrWhiteSpace(pipe))
            throw new ArgumentException("The editor requires --pipe.");
        if (!Guid.TryParse(options.GetValueOrDefault("--clipId"), out var clipId) || clipId == Guid.Empty)
            throw new ArgumentException("The editor requires a valid --clipId.");
        if (!Guid.TryParse(options.GetValueOrDefault("--providerId"), out var providerId) || providerId == Guid.Empty)
            throw new ArgumentException("The editor requires a valid --providerId.");
        if (options.GetValueOrDefault("--fieldId") != "Target")
            throw new ArgumentException("This editor only supports the Target port.");
        if (!int.TryParse(options.GetValueOrDefault("--parentPid"), out var parentId) || parentId <= 0)
            throw new ArgumentException("The editor requires a valid --parentPid.");
        return new(pipe, clipId, providerId, "Target", parentId);
    }
}
#endif
