using projectFrameCut.Render.RenderAPIBase.Project;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Template
{
    public static class TemplateStore
    {
        static TemplateStore()
        {
            Templates = new Dictionary<Guid, ITemplateStructure>();
        }
       
        public static Dictionary<Guid, ITemplateStructure> Templates { get; private set; }

        public static ITemplateStructure? GetTemplate(Guid TemplateID)
        {
            return Templates.TryGetValue(TemplateID, out var template) ? template : null;
        }
    }
}
