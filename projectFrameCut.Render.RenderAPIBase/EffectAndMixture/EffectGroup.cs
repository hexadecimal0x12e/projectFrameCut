namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public sealed class EffectGroupPort
{
    public Guid ProviderId { get; set; }
    public string FieldId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsInput { get; set; }
}

public sealed class EffectGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<Guid> ProviderIds { get; set; } = [];
    public List<Guid> ChildGroupIds { get; set; } = [];
    public List<EffectGroupPort> Ports { get; set; } = [];
    public bool Collapsed { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class EffectGroupTemplate
{
    public int Version { get; set; } = 1;
    public Guid RootGroupId { get; set; }
    public List<EffectGroup> Groups { get; set; } = [];
    public EffectProviderJSONStructure[] Providers { get; set; } = [];
}
