using projectFrameCut.Render.RenderAPIBase.Project;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Template
{
    public static class TemplateStore
    {
        public static Dictionary<Guid, ITemplateStructure> Templates { get; } = new Dictionary<Guid, ITemplateStructure>();

        public static ITemplateStructure? GetTemplate(Guid TemplateID)
        {
            return Templates.TryGetValue(TemplateID, out var template) ? template : null;
        }
    }
}
