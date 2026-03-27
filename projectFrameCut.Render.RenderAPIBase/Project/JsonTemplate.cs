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
    public class JSONBasedTemplateStructure : ITemplateStructure
    {
        public int TemplateVersion { get; set; } = 1;
        public TemplateType TemplateType  => TemplateType.JSON;
        public TemplateScope Scope { get; set; }
        public Guid TemplateID { get; set; } = Guid.NewGuid();
        public string TemplateName { get; set; } = "Template";

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
