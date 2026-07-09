using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.RenderAPIBase.Project
{
    /// <summary>
    /// 表示一个带 PowerShell 脚本的模板。
    /// 在现有的 JSON+填空模板基础上，附加一个 PowerShell 脚本。
    /// 应用模板时，占位符替换完成后，脚本通过 <c>ScriptCore</c> 执行，
    /// 利用用户填入的变量值和内置 Cmdlet 完成动态项目配置。
    /// </summary>
    public class ScriptBasedTemplateStructure : ITemplateStructure
    {
        // ---- ITemplateStructure 标准属性 ----

        public int TemplateVersion { get; set; } = 1;

        public TemplateType TemplateType => TemplateType.Script;

        public bool HaveAsset { get; set; }

        public TemplateScope Scope { get; set; }

        public string TemplateName { get; set; } = "Script Template";

        public Guid TemplateID { get; set; } = Guid.NewGuid();

        public int CreatedInAPIVersion { get; set; } = -1;

        public Dictionary<string, string?> Variables { get; set; } = new();

        public Dictionary<string, TemplateVariableDefinition> VariableDefinitions { get; set; } = new();

        public Dictionary<string, string>? AssetHashTable { get; set; }

        public string ScriptHash { get; set; } = "";

        /// <summary>
        /// 片段数量。用于列表展示；从 .pjfcTemplate 加载完整数据时
        /// 应使用 <see cref="Draft"/> 中的实际片段数。
        /// </summary>
        public int ClipCount { get; set; }

        /// <summary>
        /// 音轨数量。用于列表展示。
        /// </summary>
        public int TrackCount { get; set; }

        // ---- 与原 JSON 模板共享的数据结构 ----

        /// <summary>
        /// 模板的项目元数据部分（可含 {{placeholder}} 占位符）。
        /// 脚本执行前会先进行标准占位符替换，然后脚本可在此基础上做动态调整。
        /// </summary>
        public ProjectJSONStructure Project { get; set; } = new();

        /// <summary>
        /// 模板的时间线数据部分（可含 {{placeholder}} 占位符）。
        /// </summary>
        public DraftStructureJSON Draft { get; set; } = new();

        /// <summary>
        /// Markdown 格式的 Readme/介绍文档。从 .pjfcTemplate 包中的 metadata.json 读取。
        /// </summary>
        public string? Readme { get; set; }
    }
}
