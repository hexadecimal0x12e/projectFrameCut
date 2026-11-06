using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Effect;

public static class EffectGroupMermaid
{
    public sealed class ParseException(int lineNumber, string message) : InvalidOperationException($"Mermaid line {lineNumber}: {message}")
    {
        public int LineNumber { get; } = lineNumber;
    }

    public sealed class ValidationException(IReadOnlyList<EffectBindingHelper.BindingDiagnostic> diagnostics)
        : InvalidOperationException(string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)))
    {
        public IReadOnlyList<EffectBindingHelper.BindingDiagnostic> Diagnostics { get; } = diagnostics;
    }

    private const string Header = "%% projectFrameCut Effect Binding Graph";
    private const string Identifier = "[A-Za-z_][A-Za-z0-9_-]*";
    private static readonly Regex declaration = new($"^({Identifier})\\s*\\[\"([^\"\\r\\n]*)\"\\]$", RegexOptions.CultureInvariant);
    private static readonly Regex interfaceDeclaration = new($"^({Identifier})\\s*\\(\\[\"([^\"\\r\\n]*)\"\\]\\)$", RegexOptions.CultureInvariant);
    private static readonly Regex bareDeclaration = new($"^({Identifier})$", RegexOptions.CultureInvariant);
    private static readonly Regex typeDeclaration = new($"^({Identifier})\\s*-->\\s*(NodeDef[A-Za-z0-9_-]+)\\s*\\[\"([^\"\\r\\n]*)\"\\]$", RegexOptions.CultureInvariant);
    private static readonly Regex edge = new($"^({Identifier})\\s*(-->|-\\.->|==>)\\s*({Identifier})$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter<EffectStage>() },
    };

    private sealed class GraphInfo
    {
        public required string Root { get; set; }
        public EffectStage? Stage { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Diagnostic { get; set; }
    }

    private abstract class Info
    {
        public required string Id { get; set; }
        [JsonIgnore] public int Line { get; set; }
    }

    private sealed class GroupInfo : Info
    {
        public bool Collapsed { get; set; } = true;
        public double X { get; set; }
        public double Y { get; set; }
        public List<PublicPort>? Ports { get; set; }
    }

    private sealed class PublicPort
    {
        public required string Port { get; set; }
        public required string Name { get; set; }
    }

    private sealed class ProviderInfo : Info
    {
        public string? TypeName { get; set; }
        public string? FromPlugin { get; set; }
        public EffectStage? Stage { get; set; }
        public bool? Enabled { get; set; }
        public Dictionary<string, object>? StaticFields { get; set; }
        public Dictionary<string, object>? MetaData { get; set; }
    }

    private sealed class SourceInfo : Info
    {
        public required string Source { get; set; }
    }

    private sealed class PortInfo : Info
    {
        public required string Node { get; set; }
        public required string Field { get; set; }
        public required bool Input { get; set; }
    }

    private sealed class InterfaceInfo : Info
    {
        public required string Node { get; set; }
        public required bool Input { get; set; }
    }

    private sealed class ValueInfo : Info
    {
        public required string Node { get; set; }
        public required string Field { get; set; }
    }

    private sealed record Node(string? Parent, string Name, bool IsSubgraph, int Line, bool HasName);

    public static EffectGroupTemplate Parse(string text) => Parse(text, validateTemplate: true);

    public static EffectGroupTemplate Parse(string text, bool validateTemplate)
    {
        var metadata = new Dictionary<string, Info>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        var edges = new List<(string From, string To, bool Mapping, bool Static, int Line)>();
        var definitions = new Dictionary<string, (string Alias, string Plugin, string Type, int Line)>(StringComparer.Ordinal);
        var definitionIds = new HashSet<string>(StringComparer.Ordinal);
        var variables = new Dictionary<string, (string Id, int Line)>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        GraphInfo? graph = null;
        bool header = false, version = false, started = false, connecting = false;
        int lineNumber = 0;
        using var reader = new StringReader(text.TrimStart('\uFEFF'));
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line == Header)
            {
                if (header || started) throw Error(lineNumber, "Unexpected graph header.");
                header = true;
                continue;
            }
            if (line.StartsWith("%% Version", StringComparison.Ordinal))
            {
                if (version || started || line != "%% Version 1") throw Error(lineNumber, "Unsupported or repeated graph version.");
                version = true;
                continue;
            }
            if (line.StartsWith("%% @", StringComparison.Ordinal))
            {
                if (connecting) throw Error(lineNumber, "Metadata must precede connections.");
                int space = line.IndexOf(' ', 4);
                if (space < 0) throw Error(lineNumber, "Expected metadata value.");
                string kind = line[4..space], json = line[(space + 1)..];
                if (kind == "variable")
                {
                    if (!stack.TryPeek(out var owner)) throw Error(lineNumber, "Place @variable inside its variable provider subgraph.");
                    if (string.IsNullOrWhiteSpace(json)) throw Error(lineNumber, "Missing variable ID.");
                    if (!variables.TryAdd(owner, (json.Trim(), lineNumber))) throw Error(lineNumber, $"Repeated @variable on '{owner}'.");
                    continue;
                }
                try
                {
                    if (kind == "graph")
                    {
                        if (graph is not null) throw Error(lineNumber, "Repeated graph metadata.");
                        graph = JsonSerializer.Deserialize<GraphInfo>(json, options) ?? throw Error(lineNumber, "Empty graph metadata.");
                        if (graph.Diagnostic) throw Error(lineNumber, "Import an Effect group template; this graph describes a project's diagnostic pipeline.");
                        continue;
                    }
                    Info info = kind switch
                    {
                        "group" => JsonSerializer.Deserialize<GroupInfo>(json, options)!,
                        "provider" => JsonSerializer.Deserialize<ProviderInfo>(json, options)!,
                        "source" => JsonSerializer.Deserialize<SourceInfo>(json, options)!,
                        "port" => JsonSerializer.Deserialize<PortInfo>(json, options)!,
                        "interface" => JsonSerializer.Deserialize<InterfaceInfo>(json, options)!,
                        "value" => JsonSerializer.Deserialize<ValueInfo>(json, options)!,
                        _ => throw Error(lineNumber, $"Unknown metadata '{kind}'."),
                    };
                    if (info is null || string.IsNullOrWhiteSpace(info.Id)) throw Error(lineNumber, "Missing metadata Id.");
                    info.Line = lineNumber;
                    if (!metadata.TryAdd(info.Id, info)) throw Error(lineNumber, $"Repeated metadata '{info.Id}'.");
                }
                catch (JsonException ex) { throw Error(lineNumber, ex.Message); }
                continue;
            }
            if (line.StartsWith("%%", StringComparison.Ordinal)) continue;
            if (line.EndsWith(';')) line = line[..^1].TrimEnd();
            if (!started)
            {
                if (!header || !version || line != "graph TD") throw Error(lineNumber, "Expected Effect Binding Graph Version 1 and graph TD.");
                started = true;
                continue;
            }
            if (line == "end")
            {
                if (stack.Count == 0) throw Error(lineNumber, "Unexpected end.");
                stack.Pop();
                continue;
            }
            var type = typeDeclaration.Match(line);
            if (type.Success)
            {
                if (nodes.Count != 0 || connecting) throw Error(lineNumber, "NodeDef declarations must precede all subgraphs and ports.");
                string signature = Decode(type.Groups[3].Value, lineNumber);
                int separator = signature.LastIndexOf('@');
                if (separator < 0 || separator == signature.Length - 1) throw Error(lineNumber, "Expected FromPlugin@EffectTypeName.");
                string alias = type.Groups[2].Value;
                if (!definitionIds.Add(alias)) throw Error(lineNumber, $"Repeated NodeDef identifier '{alias}'.");
                if (!definitions.TryAdd(type.Groups[1].Value, (alias, signature[..separator], signature[(separator + 1)..], lineNumber)))
                    throw Error(lineNumber, "Repeated provider type declaration.");
                continue;
            }
            var connection = edge.Match(line);
            if (connection.Success)
            {
                if (stack.Count != 0) throw Error(lineNumber, "Connections must follow all subgraph declarations.");
                connecting = true;
                edges.Add((connection.Groups[1].Value, connection.Groups[3].Value, connection.Groups[2].Value == "-.->", connection.Groups[2].Value == "==>", lineNumber));
                continue;
            }
            bool subgraph = line.StartsWith("subgraph ", StringComparison.Ordinal);
            var match = declaration.Match(subgraph ? line[9..].TrimStart() : line);
            if (!subgraph && !match.Success) match = interfaceDeclaration.Match(line);
            if (!match.Success) match = bareDeclaration.Match(subgraph ? line[9..].TrimStart() : line);
            if (!match.Success) throw Error(lineNumber, "Unsupported Mermaid statement.");
            if (connecting) throw Error(lineNumber, "Declarations must precede connections.");
            string id = match.Groups[1].Value;
            if (id == "end") throw Error(lineNumber, "'end' is a reserved identifier.");
            bool hasName = match.Groups.Count > 2;
            if (!nodes.TryAdd(id, new(stack.TryPeek(out var parent) ? parent : null, hasName ? Decode(match.Groups[2].Value, lineNumber) : id, subgraph, lineNumber, hasName)))
                throw Error(lineNumber, $"Repeated declaration '{id}'.");
            if (subgraph) stack.Push(id);
        }
        if (!started) throw Error(lineNumber, "Missing graph declaration.");
        if (stack.Count != 0) throw Error(nodes[stack.Peek()].Line, "Unclosed subgraph.");
        foreach (var (id, definition) in definitions)
        {
            if (nodes.ContainsKey(definition.Alias) || metadata.ContainsKey(definition.Alias)) throw Error(definition.Line, $"NodeDef identifier '{definition.Alias}' conflicts with a graph declaration.");
            if (!nodes.TryGetValue(id, out var node) || !node.IsSubgraph) throw Error(definition.Line, $"NodeDef references an undeclared provider subgraph '{id}'.");
            if (!metadata.TryGetValue(id, out var info))
                metadata.Add(id, info = new ProviderInfo { Id = id, Line = definition.Line });
            if (info is not ProviderInfo provider) throw Error(definition.Line, "NodeDef must reference a provider.");
            if (provider.TypeName is not null && provider.TypeName != definition.Type
                || provider.FromPlugin is not null && provider.FromPlugin != definition.Plugin)
                throw Error(definition.Line, "NodeDef conflicts with provider metadata.");
            provider.TypeName = definition.Type;
            provider.FromPlugin = definition.Plugin;
        }
        foreach (var (id, node) in nodes.Where(n => n.Value.IsSubgraph))
            if (!metadata.ContainsKey(id)) metadata.Add(id, new GroupInfo { Id = id, Line = node.Line });
        foreach (var (id, node) in nodes.Where(n => !n.Value.IsSubgraph).ToArray())
        {
            if (metadata.ContainsKey(id)) continue;
            if (node.Parent is not null && metadata[node.Parent] is ProviderInfo && TryValueId(id, node.Parent, out string valueField))
            {
                metadata.Add(id, new ValueInfo { Id = id, Node = node.Parent, Field = valueField, Line = node.Line });
                continue;
            }
            if (node.Parent is null || !TryPortId(id, node.Parent, out bool input, out string field))
                throw Error(node.Line, $"Expected a port identifier '<Node>-input-<Field>' or '<Node>-output-<Field>' for '{id}'.");
            metadata.Add(id, metadata[node.Parent] is GroupInfo
                ? new InterfaceInfo { Id = id, Node = node.Parent, Input = input, Line = node.Line }
                : new PortInfo { Id = id, Node = node.Parent, Field = field, Input = input, Line = node.Line });
            if (!node.HasName) nodes[id] = node with { Name = field };
        }
        foreach (var port in metadata.Values.OfType<PortInfo>())
            if (nodes.TryGetValue(port.Id, out var node) && !node.HasName) nodes[port.Id] = node with { Name = port.Field };
        if (graph is null)
        {
            var roots = nodes.Where(n => n.Value.Parent is null && metadata[n.Key] is GroupInfo).ToArray();
            if (roots.Length != 1) throw Error(lineNumber, "Expected one root Effect group or explicit @graph metadata.");
            graph = new GraphInfo { Root = roots[0].Key };
        }
        if (graph.Stage is { } stage && !Enum.IsDefined(stage)) throw Error(lineNumber, "Unsupported default effect stage.");
        foreach (var (id, node) in nodes)
        {
            if (!metadata.TryGetValue(id, out var info)) throw Error(node.Line, $"Missing metadata for '{id}'.");
            if (node.IsSubgraph == (info is PortInfo or InterfaceInfo or ValueInfo)) throw Error(node.Line, $"Invalid declaration kind for '{id}'.");
        }
        foreach (var info in metadata.Values)
            if (!nodes.ContainsKey(info.Id)) throw Error(info.Line, $"Missing declaration for '{info.Id}'.");
        foreach (var (id, variable) in variables)
            if (metadata[id] is not ProviderInfo { TypeName: "EffectVariableRead" or "EffectVariableWrite" })
                throw Error(variable.Line, "@variable is only valid inside a variable provider subgraph.");
        if (graph.Root is null || !metadata.TryGetValue(graph.Root, out var root) || root is not GroupInfo || nodes[graph.Root].Parent is not null)
            throw Error(lineNumber, "Root must reference a top-level Effect group.");

        var groups = metadata.Values.OfType<GroupInfo>().ToDictionary(g => g.Id, g => new EffectGroup
        {
            Name = nodes[g.Id].Name, Collapsed = g.Collapsed, X = g.X, Y = g.Y,
        }, StringComparer.Ordinal);
        var providers = metadata.Values.OfType<ProviderInfo>().ToDictionary(p => p.Id, p => new EffectProviderJSONStructure
        {
            Name = nodes[p.Id].Name, TypeName = p.TypeName!, FromPlugin = p.FromPlugin ?? "", Stage = p.Stage ?? graph.Stage ?? EffectStage.Render,
            Enabled = p.Enabled ?? true, StaticFields = p.StaticFields, MetaData = p.MetaData,
            AnchorsBindingState = new() { [EffectProviderAnchorExtensions.InputKey] = IEffectProvider.NoConnectionGUID.ToString() },
        }, StringComparer.Ordinal);
        var variableIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var info in metadata.Values.OfType<ProviderInfo>().Where(p => p.TypeName is "EffectVariableRead" or "EffectVariableWrite"))
        {
            if (!variables.TryGetValue(info.Id, out var variable))
                throw Error(nodes[info.Id].Line, $"Variable provider '{info.Id}' requires '%% @variable <ID>' inside its subgraph.");
            if (!variableIds.TryGetValue(variable.Id, out var id)) variableIds.Add(variable.Id, id = Guid.NewGuid());
            var p = providers[info.Id];
            p.MetaData ??= new();
            p.MetaData[nameof(EffectVariableProvider.VariableId)] = id.ToString();
        }
        var ports = metadata.Values.OfType<PortInfo>().ToDictionary(p => p.Id, StringComparer.Ordinal);
        var values = metadata.Values.OfType<ValueInfo>().ToDictionary(p => p.Id, StringComparer.Ordinal);
        var staticFields = new HashSet<(string Node, string Field)>();
        var template = new EffectGroupTemplate { RootGroupId = groups[graph.Root].Id, Groups = groups.Values.ToList(), Providers = providers.Values.ToArray() };
        foreach (var info in metadata.Values)
        {
            var node = nodes[info.Id];
            switch (info)
            {
                case GroupInfo group:
                {
                    if (info.Id != graph.Root)
                    {
                        if (node.Parent is null || !groups.TryGetValue(node.Parent, out var parentGroup)) throw Error(node.Line, "A child group must belong to an Effect group.");
                        parentGroup.ChildGroupIds.Add(groups[group.Id].Id);
                    }
                    foreach (var port in group.Ports ?? [])
                    {
                        if (port is null || port.Port is null || !ports.TryGetValue(port.Port, out var field)
                            || field.Node is null || !providers.TryGetValue(field.Node, out var p))
                            throw Error(group.Line, "Public interface must reference a provider port.");
                        groups[group.Id].Ports.Add(new() { ProviderId = p.Id, FieldId = field.Field, IsInput = field.Input, Name = port.Name });
                    }
                    break;
                }
                case ProviderInfo provider:
                {
                    if (node.Parent is null || !groups.TryGetValue(node.Parent, out var owner)) throw Error(node.Line, "A provider must belong to an Effect group.");
                    if (string.IsNullOrWhiteSpace(provider.TypeName) || !Enum.IsDefined(providers[provider.Id].Stage!.Value)) throw Error(provider.Line, "Invalid provider type or stage; declare a NodeDef or provider TypeName.");
                    owner.ProviderIds.Add(providers[provider.Id].Id);
                    break;
                }
                case SourceInfo source:
                {
                    if (node.Parent is not null || !ValueProviderFrameContext.IsBuiltInSource(source.Source)) throw Error(source.Line, "Expected a top-level built-in source.");
                    break;
                }
                case PortInfo port:
                {
                    if (string.IsNullOrWhiteSpace(port.Field) || port.Node is null || node.Parent != port.Node
                        || !metadata.TryGetValue(port.Node, out var portOwner) || portOwner is not (ProviderInfo or SourceInfo))
                        throw Error(port.Line, "Port must belong to its declared provider or source.");
                    if (portOwner is SourceInfo && (port.Input || port.Field != EffectProviderAnchorExtensions.OutputKey)) throw Error(port.Line, "A built-in source only declares __Output__.");
                    if (providers.TryGetValue(port.Node, out var providerNode))
                        template.DeclaredPorts.Add(new() { ProviderId = providerNode.Id, FieldId = port.Field, Name = node.Name, IsInput = port.Input });
                    break;
                }
                case InterfaceInfo endpoint:
                {
                    if (endpoint.Node is null || node.Parent != endpoint.Node || !groups.ContainsKey(endpoint.Node))
                        throw Error(endpoint.Line, "An interface must be declared directly inside its Effect group.");
                    if (((GroupInfo)metadata[endpoint.Node]).Ports is { Count: > 0 })
                        throw Error(endpoint.Line, "Declare public ports with interfaces instead of mixing them with group Ports metadata.");
                    break;
                }
                case ValueInfo value:
                {
                    if (value.Node is null || node.Parent != value.Node || !providers.TryGetValue(value.Node, out var valueProvider)
                        || string.IsNullOrWhiteSpace(value.Field) || !node.HasName)
                        throw Error(value.Line, "A static value must have a JSON literal label and belong directly to its provider.");
                    if (!staticFields.Add((value.Node, value.Field))) throw Error(value.Line, "Repeated static field value.");
                    try
                    {
                        var literal = JsonSerializer.Deserialize<JsonElement>(node.Name);
                        valueProvider.StaticFields ??= new();
                        if (valueProvider.StaticFields.TryGetValue(value.Field, out var existing)
                            && !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(existing, options), literal))
                            throw Error(value.Line, $"Static value '{value.Field}' conflicts with provider metadata.");
                        valueProvider.StaticFields[value.Field] = literal;
                    }
                    catch (JsonException ex) { throw Error(value.Line, ex.Message); }
                    break;
                }
            }
        }
        foreach (var sameField in ports.Values.GroupBy(p => (p.Node, p.Field, p.Input)))
            if (sameField.Count() > 1) throw Error(sameField.Last().Line, "Repeated provider field.");
        foreach (var source in metadata.Values.OfType<SourceInfo>())
            if (ports.Values.Count(p => p.Node == source.Id) != 1) throw Error(source.Line, "A built-in source must declare one output.");
        // Interface mappings and static value arrows do not become provider bindings.
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        var valueLinks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (from, to, mapping, isStatic, line) in edges)
        {
            if (isStatic)
            {
                if (!values.TryGetValue(from, out var value) || !ports.TryGetValue(to, out var port)
                    || !port.Input || port.Node != value.Node || port.Field != value.Field)
                    throw Error(line, "A static value arrow must connect a value to its matching provider input.");
                if (!valueLinks.Add(from)) throw Error(line, "Repeated static value arrow.");
                continue;
            }
            if (!mapping) continue;
            if (!metadata.TryGetValue(from, out var source) || !metadata.TryGetValue(to, out var target))
                throw Error(line, "Interface mapping references an undeclared endpoint.");
            InterfaceInfo endpoint;
            Info inner;
            if (source is InterfaceInfo { Input: true } input)
            {
                endpoint = input;
                inner = target;
            }
            else if (target is InterfaceInfo { Input: false } output)
            {
                endpoint = output;
                inner = source;
            }
            else throw Error(line, "Map a group input to an inner input, or an inner output to a group output.");
            bool valid = inner switch
            {
                PortInfo port => port.Input == endpoint.Input && providers.ContainsKey(port.Node) && nodes[port.Node].Parent == endpoint.Node,
                InterfaceInfo child => child.Input == endpoint.Input && nodes[child.Node].Parent == endpoint.Node,
                _ => false,
            };
            if (!valid) throw Error(line, "An interface must map to a matching port of a direct member or child group.");
            if (!mappings.TryAdd(endpoint.Id, inner.Id)) throw Error(line, $"Interface '{endpoint.Id}' already has a mapping.");
        }
        var resolved = new Dictionary<string, PortInfo>(StringComparer.Ordinal);
        PortInfo Resolve(string id, int line)
        {
            if (resolved.TryGetValue(id, out var result)) return result;
            if (ports.TryGetValue(id, out var port)) return port;
            if (!validateTemplate && !metadata.ContainsKey(id))
                foreach (var owner in metadata.Values.Where(p => p is ProviderInfo or SourceInfo).OrderByDescending(p => p.Id.Length))
                    if (TryPortId(id, owner.Id, out bool input, out string field))
                        return resolved[id] = new PortInfo { Id = id, Node = owner.Id, Field = field, Input = input, Line = line };
            if (!metadata.TryGetValue(id, out var info) || info is not InterfaceInfo) throw Error(line, $"Undeclared port '{id}'.");
            if (!mappings.TryGetValue(id, out var inner)) throw Error(info.Line, $"Interface '{id}' has no mapping.");
            return resolved[id] = Resolve(inner, line);
        }
        foreach (var id in nodes.Keys.Where(id => metadata[id] is InterfaceInfo))
        {
            var endpoint = (InterfaceInfo)metadata[id];
            var port = Resolve(id, endpoint.Line);
            groups[endpoint.Node].Ports.Add(new() { ProviderId = providers[port.Node].Id, FieldId = port.Field, IsInput = endpoint.Input, Name = nodes[id].Name });
        }
        var connected = new HashSet<(Guid ProviderId, string Field)>();
        foreach (var (from, to, mapping, isStatic, line) in edges)
        {
            if (mapping || isStatic) continue;
            var output = Resolve(from, line);
            var input = Resolve(to, line);
            if (output.Input || !input.Input || !providers.TryGetValue(input.Node, out var target)) throw Error(line, "Connections must run from an output to a provider input.");
            if (!connected.Add((target.Id, input.Field))) throw Error(line, $"Input '{to}' already has a connection.");
            target.AnchorsBindingState[input.Field] = metadata[output.Node] is SourceInfo source ? source.Source
                : EffectProviderOutputExtensions.CreateOutputSourceId(providers[output.Node].Id, output.Field);
        }
        if (validateTemplate)
        {
            try { EffectGroupHelper.ValidateTemplate(template); }
            catch (InvalidOperationException ex) { throw Error(nodes[graph.Root].Line, ex.Message); }
        }
        return template;
    }

    public static void ValidatePorts(EffectGroupTemplate template, IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        var errors = new List<EffectBindingHelper.BindingDiagnostic>();
        foreach (var p in providers.Values)
        {
            var actual = EffectGroupHelper.DeclaredPorts(p).Select(f => (f.FieldId, f.IsInput)).ToHashSet();
            var declared = template.DeclaredPorts.Where(f => f.ProviderId == p.Id).Select(f => (f.FieldId, f.IsInput)).ToHashSet();
            foreach (var f in actual.Except(declared))
                errors.Add(new(p.Id, "MissingPortDeclaration", $"Missing {(f.IsInput ? "input" : "output")} port '{f.FieldId}' on '{p.Name}' ({p.TypeName}).") { FieldId = f.FieldId });
            foreach (var f in declared.Except(actual))
                errors.Add(new(p.Id, "UnknownPortDeclaration", $"Unknown {(f.IsInput ? "input" : "output")} port '{f.FieldId}' on '{p.Name}' ({p.TypeName}).") { FieldId = f.FieldId });
        }
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    public static string Serialize(EffectGroupTemplate template) => Serialize(template, null);

    private sealed record DiagramContext(IReadOnlyDictionary<string, string> SourceNames, string? OutputName, List<string> FinalSources);

    public static string SerializeDiagram(IReadOnlyDictionary<Guid, IEffectProvider> providers, IReadOnlyList<EffectGroup> groups,
        string name, IReadOnlyDictionary<string, string>? sourceNames = null, string? outputName = null, string? passthroughSource = null)
    {
        var selected = EffectGroupHelper.Copy(groups.Where(g => EffectGroupHelper.Members(g, groups) is { Count: > 0 } ids
            && ids.IsSubsetOf(providers.Keys)).ToList());
        var root = new EffectGroup
        {
            Name = name,
            ProviderIds = providers.Keys.Except(selected.SelectMany(g => g.ProviderIds)).ToList(),
            ChildGroupIds = selected.Select(g => g.Id).Except(selected.SelectMany(g => g.ChildGroupIds)).ToList(),
        };
        selected.Insert(0, root);
        var template = new EffectGroupTemplate
        {
            RootGroupId = root.Id, Groups = selected,
            Providers = providers.Values.Select(EffectBindingHelper.SerializeProvider).ToArray(),
            DeclaredPorts = providers.Values.SelectMany(EffectGroupHelper.DeclaredPorts).ToList(),
        };
        void EnsurePort(Guid id, string field, bool input)
        {
            if (!template.DeclaredPorts.Any(p => p.ProviderId == id && p.FieldId == field && p.IsInput == input))
                template.DeclaredPorts.Add(new() { ProviderId = id, FieldId = field, Name = field, IsInput = input });
        }
        string Source(string value)
        {
            if (!EffectProviderOutputExtensions.TryParseOutputSourceId(value, out var id, out var field) || !providers.TryGetValue(id, out var p)) return value;
            field = p.TryGetOutputField(field, out var output) && !string.IsNullOrWhiteSpace(output.Id) ? output.Id : field ?? "__UnspecifiedOutput__";
            EnsurePort(id, field, false);
            return EffectProviderOutputExtensions.CreateOutputSourceId(id, field);
        }
        var finalSources = providers.Values.Where(p => p.IsFinalOutputSource()).Select(p => Source(p.GetFinalOutputSourceId())).ToList();
        if (finalSources.Count == 0 && passthroughSource is not null) finalSources.Add(Source(passthroughSource));
        foreach (var p in template.Providers)
            foreach (var key in p.AnchorsBindingState.Keys.ToArray())
            {
                if (EffectProviderAnchorExtensions.IsOutputBindingKey(key)) p.AnchorsBindingState.Remove(key);
                else if (p.AnchorsBindingState[key] != IEffectProvider.NoConnectionGUID.ToString())
                {
                    EnsurePort(p.Id, key, true);
                    p.AnchorsBindingState[key] = Source(p.AnchorsBindingState[key]);
                }
            }
        return Serialize(template, new(sourceNames ?? new Dictionary<string, string>(), outputName, finalSources));
    }

    private static string Serialize(EffectGroupTemplate template, DiagramContext? diagram)
    {
        if (diagram is null) EffectGroupHelper.ValidateTemplate(template);
        var sb = new StringBuilder();
        var groups = template.Groups.ToDictionary(g => g.Id);
        var providers = template.Providers.ToDictionary(p => p.Id);
        var groupNames = new Dictionary<Guid, string>();
        var providerNames = new Dictionary<Guid, string>();
        var variableNames = new Dictionary<Guid, string>();
        var portNames = new Dictionary<(Guid Id, string Field, bool Input), string>();
        var staticLinks = new List<(string Value, string Port)>();
        var interfaceNames = new Dictionary<(Guid GroupId, int Index), string>();
        var parentGroups = new Dictionary<Guid, EffectGroup>();
        var memberGroups = new Dictionary<Guid, EffectGroup>();
        var members = template.Groups.ToDictionary(g => g.Id, g => EffectGroupHelper.Members(g, template.Groups));
        int groupIndex = 0, providerIndex = 0;
        void Assign(EffectGroup group)
        {
            groupNames.Add(group.Id, $"group{++groupIndex}");
            for (int i = 0; i < group.Ports.Count; i++)
                interfaceNames.Add((group.Id, i), $"{groupNames[group.Id]}-{(group.Ports[i].IsInput ? "input" : "output")}-{i + 1}");
            foreach (var id in group.ProviderIds)
            {
                providerNames.Add(id, $"node{++providerIndex}");
                memberGroups.Add(id, group);
            }
            foreach (var id in group.ChildGroupIds)
            {
                parentGroups.Add(id, group);
                Assign(groups[id]);
            }
        }
        Assign(groups[template.RootGroupId]);
        foreach (var (id, alias) in providerNames)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in template.DeclaredPorts.Where(p => p.ProviderId == id).OrderByDescending(p => p.IsInput).ThenBy(p => p.FieldId, StringComparer.Ordinal))
            {
                string name = $"{alias}-{(port.IsInput ? "input" : "output")}-{Regex.Replace(port.FieldId, "[^A-Za-z0-9_-]", "_")}";
                string candidate = name;
                int suffix = 1;
                while (!used.Add(candidate)) candidate = $"{name}-{++suffix}";
                if (!portNames.TryAdd((id, port.FieldId, port.IsInput), candidate)) throw new InvalidOperationException("Repeated provider field.");
            }
        }
        bool External(string source) => source != IEffectProvider.NoConnectionGUID.ToString()
            && (!EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out _) || !providers.ContainsKey(id));
        var sources = template.Providers.SelectMany(p => p.AnchorsBindingState.Values)
            .Concat(diagram?.SourceNames.Keys ?? []).Concat(diagram?.FinalSources ?? [])
            .Where(source => diagram is null ? ValueProviderFrameContext.IsBuiltInSource(source) : External(source))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select((source, i) => (source, alias: $"source{i + 1}"))
            .ToDictionary(s => s.source, s => s.alias, StringComparer.Ordinal);
        void Metadata<T>(string indent, string kind, T data) => sb.Append(indent).Append("%% @").Append(kind).Append(' ').Append(JsonSerializer.Serialize(data, options)).Append('\n');
        void Subgraph(string indent, string id, string name) => sb.Append(indent).Append("subgraph ").Append(id).Append("[\"").Append(Encode(name)).Append("\"]\n");
        void Port(string indent, PortInfo info, string name)
        {
            bool inferred = TryPortId(info.Id, info.Node, out bool input, out string field) && input == info.Input && field == info.Field;
            if (!inferred) Metadata(indent, "port", info);
            sb.Append(indent).Append(info.Id);
            if (!inferred || name != info.Field) sb.Append("[\"").Append(Encode(name)).Append("\"]");
            sb.Append('\n');
        }
        sb.Append(Header).Append("\n%% Version 1\n");
        Metadata("", "graph", new GraphInfo { Root = groupNames[template.RootGroupId], Diagnostic = diagram is not null });
        sb.Append("graph TD\n");
        foreach (var (id, alias) in providerNames)
            if (!providers[id].TypeName.Contains('@'))
                sb.Append("    ").Append(alias).Append(" --> NodeDef").Append(alias).Append("[\"")
                    .Append(Encode(providers[id].FromPlugin + "@" + providers[id].TypeName)).Append("\"]\n");
        foreach (var (source, alias) in sources)
        {
            Metadata("    ", "source", new SourceInfo { Id = alias, Source = source });
            Subgraph("    ", alias, diagram?.SourceNames.GetValueOrDefault(source) ?? source);
            Port("        ", new PortInfo { Id = alias + "-output-__Output__", Node = alias, Field = EffectProviderAnchorExtensions.OutputKey, Input = false }, "Output");
            sb.Append("    end\n");
        }
        void WriteGroup(EffectGroup group, string indent)
        {
            string alias = groupNames[group.Id];
            Metadata(indent, "group", new GroupInfo
            {
                Id = alias, Collapsed = group.Collapsed, X = group.X, Y = group.Y,
            });
            Subgraph(indent, alias, group.Name);
            if (diagram?.OutputName is { } outputName && group.Id == template.RootGroupId)
            {
                Metadata(indent + "    ", "interface", new InterfaceInfo { Id = "pipeline-output", Node = alias, Input = false });
                sb.Append(indent).Append("    pipeline-output([\"").Append(Encode(outputName)).Append("\"])\n");
            }
            for (int i = 0; i < group.Ports.Count; i++)
            {
                string endpoint = interfaceNames[(group.Id, i)];
                sb.Append(indent).Append("    ").Append(endpoint).Append("([\"").Append(Encode(group.Ports[i].Name)).Append("\"])\n");
            }
            foreach (var id in group.ProviderIds)
            {
                var p = providers[id];
                string name = providerNames[id];
                var info = new ProviderInfo
                {
                    Id = name,
                    TypeName = p.TypeName.Contains('@') ? p.TypeName : null,
                    FromPlugin = p.TypeName.Contains('@') ? p.FromPlugin : null,
                    Stage = p.Stage == EffectStage.Render ? null : p.Stage ?? throw new InvalidOperationException($"Missing stage on '{p.Name}'."),
                    Enabled = p.Enabled ? null : false,
                    MetaData = p.MetaData is { Count: > 0 } ? new(p.MetaData) : null,
                };
                string? variableName = null;
                if (p.TypeName is "EffectVariableRead" or "EffectVariableWrite")
                {
                    var variableId = Guid.TryParse(p.MetaData?.GetValueOrDefault(nameof(EffectVariableProvider.VariableId))?.ToString(), out var storedId) ? storedId : p.Id;
                    if (!variableNames.TryGetValue(variableId, out variableName))
                        variableNames.Add(variableId, variableName = $"variable{variableNames.Count + 1}");
                    info.MetaData?.Remove(nameof(EffectVariableProvider.VariableId));
                    if (info.MetaData is { Count: 0 }) info.MetaData = null;
                }
                if (info.TypeName is not null || info.Stage is not null || info.Enabled is not null || info.MetaData is not null)
                    Metadata(indent + "    ", "provider", info);
                Subgraph(indent + "    ", name, p.Name);
                if (variableName is not null) sb.Append(indent).Append("        %% @variable ").Append(variableName).Append('\n');
                foreach (var field in template.DeclaredPorts.Where(f => f.ProviderId == id).OrderByDescending(f => f.IsInput).ThenBy(f => f.FieldId, StringComparer.Ordinal))
                    Port(indent + "        ", new PortInfo { Id = portNames[(id, field.FieldId, field.IsInput)], Node = name, Field = field.FieldId, Input = field.IsInput }, field.Name);
                var usedValues = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (field, value) in (p.StaticFields ?? []).OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    string valueId = $"{name}-value-{Regex.Replace(field, "[^A-Za-z0-9_-]", "_")}";
                    string candidate = valueId;
                    int suffix = 1;
                    while (!usedValues.Add(candidate)) candidate = $"{valueId}-{++suffix}";
                    if (!TryValueId(candidate, name, out string inferred) || inferred != field)
                        Metadata(indent + "        ", "value", new ValueInfo { Id = candidate, Node = name, Field = field });
                    sb.Append(indent).Append("        ").Append(candidate).Append("[\"")
                        .Append(Encode(JsonSerializer.Serialize(value, options))).Append("\"]\n");
                    if (portNames.TryGetValue((id, field, true), out var port)) staticLinks.Add((candidate, port));
                }
                sb.Append(indent).Append("    end\n");
            }
            foreach (var id in group.ChildGroupIds) WriteGroup(groups[id], indent + "    ");
            sb.Append(indent).Append("end\n");
        }
        WriteGroup(groups[template.RootGroupId], "    ");
        sb.Append('\n');
        foreach (var (value, port) in staticLinks)
            sb.Append("    ").Append(value).Append(" ==> ").Append(port).Append('\n');
        foreach (var id in groupNames.Keys)
        {
            var group = groups[id];
            for (int i = 0; i < group.Ports.Count; i++)
            {
                var port = group.Ports[i];
                string inner;
                if (group.ProviderIds.Contains(port.ProviderId)) inner = portNames[(port.ProviderId, port.FieldId, port.IsInput)];
                else
                {
                    var child = group.ChildGroupIds.Select(childId => groups[childId]).Single(g => members[g.Id].Contains(port.ProviderId));
                    int index = child.Ports.FindIndex(p => EffectGroupHelper.SamePort(p, port));
                    inner = interfaceNames[(child.Id, index)];
                }
                string endpoint = interfaceNames[(id, i)];
                sb.Append("    ").Append(port.IsInput ? endpoint : inner).Append(" -.-> ").Append(port.IsInput ? inner : endpoint).Append('\n');
            }
        }
        string BoundaryPort(Guid id, string field, bool input, Guid? other)
        {
            var group = memberGroups[id];
            string endpoint = portNames[(id, field, input)];
            while (other is null || !members[group.Id].Contains(other.Value))
            {
                int index = group.Ports.FindIndex(p => p.ProviderId == id && p.FieldId == field && p.IsInput == input);
                if (index < 0)
                {
                    if (diagram is not null) break;
                    throw new InvalidOperationException("Missing public interface for a boundary binding.");
                }
                endpoint = interfaceNames[(group.Id, index)];
                if (!parentGroups.TryGetValue(group.Id, out var parent)) break;
                group = parent;
            }
            return endpoint;
        }
        foreach (var id in providerNames.Keys)
            foreach (var (field, source) in providers[id].AnchorsBindingState.OrderBy(b => b.Key, StringComparer.Ordinal))
            {
                if (source == IEffectProvider.NoConnectionGUID.ToString()) continue;
                string from;
                Guid? other = null;
                if (sources.TryGetValue(source, out var builtin)) from = builtin + "-output-__Output__";
                else
                {
                    if (!EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var sourceId, out var output)) throw new InvalidOperationException("Invalid binding source.");
                    if (output is null)
                    {
                        var outputs = template.DeclaredPorts.Where(p => p.ProviderId == sourceId && !p.IsInput).ToArray();
                        if (outputs.Length != 1) throw new InvalidOperationException("Binding must identify an output port.");
                        output = outputs[0].FieldId;
                    }
                    other = sourceId;
                    from = BoundaryPort(sourceId, output, false, id);
                }
                sb.Append("    ").Append(from).Append(" --> ").Append(BoundaryPort(id, field, true, other)).Append('\n');
            }
        if (diagram?.OutputName is not null)
            foreach (var source in diagram.FinalSources)
            {
                string from = sources.TryGetValue(source, out var builtin) ? builtin + "-output-__Output__"
                    : EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out var field)
                        ? BoundaryPort(id, field!, false, null) : throw new InvalidOperationException("Invalid diagnostic output source.");
                sb.Append("    ").Append(from).Append(" --> pipeline-output\n");
            }
        return sb.ToString();
    }

    private static ParseException Error(int line, string message) => new(line, message);

    private static bool TryValueId(string id, string node, out string field)
    {
        field = "";
        if (!id.StartsWith(node + "-", StringComparison.Ordinal)) return false;
        string suffix = id[(node.Length + 1)..];
        if (!suffix.StartsWith("value-", StringComparison.OrdinalIgnoreCase)) return false;
        field = suffix[6..];
        return !string.IsNullOrWhiteSpace(field);
    }

    private static bool TryPortId(string id, string node, out bool input, out string field)
    {
        input = false;
        field = "";
        if (!id.StartsWith(node + "-", StringComparison.Ordinal)) return false;
        string suffix = id[(node.Length + 1)..];
        if (suffix is EffectProviderAnchorExtensions.InputKey or EffectProviderAnchorExtensions.OutputKey)
        {
            input = suffix == EffectProviderAnchorExtensions.InputKey;
            field = suffix;
            return true;
        }
        int separator = suffix.IndexOf('-');
        if (separator < 0) return false;
        string direction = suffix[..separator];
        input = direction.Equals("input", StringComparison.OrdinalIgnoreCase)
            || direction.Equals("param", StringComparison.OrdinalIgnoreCase)
            || direction.Equals("parameter", StringComparison.OrdinalIgnoreCase);
        if (!input && !direction.Equals("output", StringComparison.OrdinalIgnoreCase)) return false;
        field = suffix[(separator + 1)..];
        return !string.IsNullOrWhiteSpace(field);
    }

    private static string Encode(string value)
    {
        var sb = new StringBuilder();
        foreach (var c in value)
            if (c is '#' or '"' or '&' or '<' or '>' or '[' or ']' or '\\' || char.IsControl(c))
                sb.Append('#').Append(((int)c).ToString(CultureInfo.InvariantCulture)).Append(';');
            else sb.Append(c);
        return sb.ToString();
    }

    private static string Decode(string value, int line) => Regex.Replace(value, "#([0-9]+);", m =>
        int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int c) && c <= char.MaxValue
            ? ((char)c).ToString() : throw Error(line, "Invalid character escape."));
}
