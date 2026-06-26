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
        /// Indicates whether the template has an associated asset, such as a file or resource. If true, it means there is an asset linked to this template; if false, there is no associated asset.
        /// </summary>   
        bool HaveAsset { get; set; }

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

        /// <summary>
        /// Get or set the asset hash table, which maps asset identifiers to their corresponding hash values. 
        /// Enabled when <see cref="HaveAsset"/> is true. 
        /// This allows for tracking and managing assets associated with the template, ensuring that the correct assets are used when materializing the template.
        /// </summary>
        public Dictionary<string, string>? AssetHashTable { get; set; }

        /// <summary>
        /// Indicates the API version in which this template was created. This is used for compatibility checks when loading templates.
        /// </summary>
        public int CreatedInAPIVersion { get; set; } 
    }

    /// <summary>
    /// Defines a single template variable.
    /// </summary>
    public class TemplateVariableDefinition
    {
        /// <summary>
        /// Variable value kind.
        /// </summary>
        public TemplateVariableType Type { get; set; } = TemplateVariableType.String;

        /// <summary>
        /// The kind of asset when <see cref="Type"/> is <see cref="TemplateVariableType.File"/>.
        /// Use <see cref="AssetType.Other"/> for any type of asset, or null for non-asset file variables.
        /// Keep it null if <see cref="Type"/> is not <see cref="TemplateVariableType.File"/>.
        /// </summary>
        public AssetType? TypeOfAsset { get; set; } = null;

        /// <summary>
        /// Optional default value for the variable.
        /// </summary>
        public string? DefaultValue { get; set; }

        /// <summary>
        /// A user-friendly name for the variable, used for display purposes when prompting users to fill in variable values. 
        /// It does not affect the functionality of the template and is purely for improving user experience.
        /// </summary>
        public string? UserFriendlyName { get; set; }

        /// <summary>
        /// A description for the variable, used for display purposes when prompting users to fill in variable values. 
        /// </summary>
        public string? Description { get; set; }    
    }

    /// <summary>
    /// Supported value kinds for a template variable.
    /// </summary>
    public enum TemplateVariableType
    {
        Auto = 0,
        String = 1,
        Number = 2,
        Integer = 3,
        Boolean = 4,
        File = 5,
        Json = 6
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
        Undefined = -1
    }

    public enum TemplateScope
    {
        Any = -1,
        Project = 0,
        Clips = 1,
        Tracks = 2
    }
}

