using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.Project
{
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
    /// Defines a single template variable.
    /// </summary>
    public class TemplateVariableDefinition
    {
        /// <summary>
        /// Variable value kind.
        /// </summary>
        public TemplateVariableType Type { get; set; } = TemplateVariableType.String;

        /// <summary>
        /// Optional default value for the variable.
        /// </summary>
        public string? DefaultValue { get; set; }
    }

    /// <summary>
    /// Represents a template package containing project and draft information.
    /// </summary>
    public class JSONBasedTemplateStructure
    {
        /// <summary>
        /// Template format version.
        /// </summary>
        public int TemplateVersion { get; set; } = 1;

        /// <summary>
        /// The project part in template form.
        /// </summary>
        public ProjectJSONStructure Project { get; set; } = new ProjectJSONStructure();

        /// <summary>
        /// The draft part in template form.
        /// </summary>
        public DraftStructureJSON Draft { get; set; } = new DraftStructureJSON();

        /// <summary>
        /// Default values of placeholders. Key uses placeholder body without brackets.
        /// </summary>
        public Dictionary<string, string?> Variables { get; set; } = new Dictionary<string, string?>();

        /// <summary>
        /// Typed variable definitions. Key uses placeholder body without brackets.
        /// </summary>
        public Dictionary<string, TemplateVariableDefinition> VariableDefinitions { get; set; } = new Dictionary<string, TemplateVariableDefinition>();
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

    /// <summary>
    /// Controls placeholder fill behavior.
    /// </summary>
    public class DraftTemplateFillOptions
    {
        /// <summary>
        /// When true, throw if a placeholder has no provided value and no default value.
        /// </summary>
        public bool ThrowIfMissingValue { get; set; } = true;
    }

    /// <summary>
    /// Materialized result from a template.
    /// </summary>
    public class MaterializedProjectDraft
    {
        public ProjectJSONStructure Project { get; set; } = new ProjectJSONStructure();
        public DraftStructureJSON Draft { get; set; } = new DraftStructureJSON();
    }
}
