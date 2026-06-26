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
        public TemplateType TemplateType => TemplateType.JSON;
        public TemplateScope Scope { get; set; }
        public Guid TemplateID { get; set; } = Guid.NewGuid();
        public string TemplateName { get; set; } = "Template";
        public int CreatedInAPIVersion { get; set; } = -1;

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

        public bool HaveAsset { get; set; } = false;

        public Dictionary<string, string>? AssetHashTable { get; set; } = null;

    }

    /// <summary>
    /// A simple metadata structure for templates, containing basic information about the template such as its source, scope, name, creator, genre, creation time, revision number, tags, and readme.
    /// </summary>
    public record TemplateMetadataStructure
    {
        /// <summary>
        /// Indicates which template this template was derived from, if any. 
        /// It should be same to <see cref="ITemplateStructure.TemplateID"/>
        /// </summary>
        public Guid SourceTemplateID { get; set; } = Guid.Empty;

        /// <summary>
        /// Get the scope of the template, which indicates the applicable range of the template. 
        /// Should be same to <see cref="ITemplateStructure.Scope"/>
        /// </summary>
        public TemplateScope Scope { get; set; }

        /// <summary>
        /// Name of this template, used for display and template management. It does not affect the content of the template.
        /// Should be same to <see cref="ITemplateStructure.TemplateName"/>
        /// </summary>
        public string TemplateName { get; set; } = string.Empty;

        /// <summary>
        /// The unique identifier of the user who created the template.
        /// </summary>
        public Guid CreatedByUser { get; set; } = Guid.Empty;

        /// <summary>
        /// The username of the user who created the template.
        /// </summary>
        public string CreatedByUserName { get; set; } = string.Empty;

        /// <summary>
        /// The subtitle of the template.
        /// </summary>
        public string Subtitle { get; set; } = string.Empty;

        /// <summary>
        /// The UTC timestamp when the template was created.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Indicates the revision number of the template. This can be used to track changes and updates to the template over time.
        /// </summary>
        public int Revision { get; set; } = 1;

        /// <summary>
        /// User-defined tags for categorizing and searching templates.
        /// </summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>
        /// Markdown-formatted readme/introduction for this template.
        /// </summary>
        public string? Readme { get; set; }
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
