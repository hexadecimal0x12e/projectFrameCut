namespace projectFrameCut.Render.RenderAPIBase.Project
{
    public interface ITemplateStructure
    {
        /// <summary>
        /// Gets the version number of the template associated with this instance.
        /// </summary>
        int TemplateVersion { get; }

        /// <summary>
        /// Gets the type of template represented by this instance.
        /// </summary>
        TemplateType TemplateType { get; }

        /// <summary>
        /// Get the scope of the template, which indicates the applicable range of the template. It can be project-level, clip-level, track-level, or any level.
        /// </summary>
        TemplateScope Scope { get; set; }

        /// <summary>
        /// Name of this template, used for display and template management. It does not affect the content of the template.
        /// </summary>
        string TemplateName { get; set; }

        /// <summary>
        /// The unique identifier of the template, used for template management and association. It does not affect the content of the template.
        /// </summary>
        Guid TemplateID { get; set; }

        /// <summary>
        /// Default values of placeholders. Key uses placeholder body without brackets.
        /// </summary>
        public Dictionary<string, string?> Variables { get; set; }

        /// <summary>
        /// Typed variable definitions. Key uses placeholder body without brackets.
        /// </summary>
        public Dictionary<string, TemplateVariableDefinition> VariableDefinitions { get; set; }
    }

    /// <summary>
    /// Defines what fields should be replaced with placeholders while exporting a template.
    /// </summary>
    public class DraftTemplateBuildOptions
    {
        /// <summary>
        /// Placeholder prefix, default is "{{".
        /// </summary>
        public string PlaceholderPrefix { get; set; } = "{{";

        /// <summary>
        /// Placeholder suffix, default is "}}".
        /// </summary>
        public string PlaceholderSuffix { get; set; } = "}}";

        /// <summary>
        /// Clip fields to convert into placeholders.
        /// </summary>
        public List<string> ClipFieldsToExtract { get; set; } = new List<string>
        {
            "Name",
            "FilePath"
        };

        /// <summary>
        /// Soundtrack fields to convert into placeholders.
        /// </summary>
        public List<string> TrackFieldsToExtract { get; set; } = new List<string>
        {
            "Name",
            "FilePath"
        };
    }

    public enum TemplateType
    {
        JSON = 0,
        Assembly = 1,
        Underfined = -1
    }

    public enum TemplateScope
    {
        Any = -1,
        Project = 0,
        Clips = 1,
        Tracks = 2
    }
}

