using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.Project
{
    internal interface IAssemblyBasedTemplate
    {
        public string Name { get; }
        public string Description { get; }
        public DraftStructureJSON Build(IReadOnlyDictionary<string, DraftStructureJSON> data);
    }
}
