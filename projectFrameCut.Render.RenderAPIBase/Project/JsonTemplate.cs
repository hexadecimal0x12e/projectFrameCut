using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.Project
{

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

        public bool HaveAsset {get; set; } = false;

        public Dictionary<string, string>? AssetHashTable { get; set; } = null;
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
