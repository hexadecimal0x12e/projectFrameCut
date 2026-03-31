using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.Project
{
    internal interface IAssemblyBasedTemplate : ITemplateStructure
    {
        public DraftStructureJSON Build();
    }
}
