using projectFrameCut.Render.RenderAPIBase.Project;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.TemplateSystem
{
    /// <summary>
    /// Helper methods for exporting and materializing JSON-based draft templates.
    /// </summary>
    public static class JSONBasedTemplateHelper
    {
        private static readonly JsonSerializerOptions _defaultSerializerOptions = new()
        {
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        public static JSONBasedTemplateStructure ExportTemplate(
            ProjectJSONStructure project,
            DraftStructureJSON draft,
            DraftTemplateBuildOptions? options = null)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (draft == null) throw new ArgumentNullException(nameof(draft));

            options ??= new DraftTemplateBuildOptions();

            var clonedProject = DeepClone(project);
            var clonedDraft = DeepClone(draft);

            var vars = new Dictionary<string, string?>();
            clonedDraft.Clips = BuildTemplatedElements(clonedDraft.Clips, "clip", options.ClipFieldsToExtract, vars, options);
            clonedDraft.SoundTracks = BuildTemplatedSoundtrackElements(clonedDraft.SoundTracks, "track", options.TrackFieldsToExtract, vars, options);

            return new JSONBasedTemplateStructure
            {
                Project = clonedProject,
                Draft = clonedDraft,
                Variables = vars
            };
        }

        public static string SerializeTemplate(JSONBasedTemplateStructure template, JsonSerializerOptions? options = null)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            return JsonSerializer.Serialize(template, options ?? new JsonSerializerOptions { WriteIndented = true });
        }

        public static JSONBasedTemplateStructure DeserializeTemplate(string templateJson, JsonSerializerOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(templateJson)) throw new ArgumentException("Template json cannot be empty.", nameof(templateJson));
            return JsonSerializer.Deserialize<JSONBasedTemplateStructure>(templateJson, options)
                ?? throw new JsonException("Failed to deserialize JSONBasedTemplateStructure.");
        }

        public static MaterializedProjectDraft FillTemplate(
            JSONBasedTemplateStructure template,
            IReadOnlyDictionary<string, string?> values,
            DraftTemplateFillOptions? options = null)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (values == null) throw new ArgumentNullException(nameof(values));

            options ??= new DraftTemplateFillOptions();

            var proj = DeepClone(template.Project);
            var draft = DeepClone(template.Draft);

            draft.Clips = FillElements(draft.Clips, values, template.Variables, options);
            draft.SoundTracks = FillSoundtrackElements(draft.SoundTracks, values, template.Variables, options);

            return new MaterializedProjectDraft
            {
                Project = proj,
                Draft = draft
            };
        }

        public static ProjectJSONStructure FillTemplateToProject(
            JSONBasedTemplateStructure template,
            IReadOnlyDictionary<string, string?> values,
            DraftTemplateFillOptions? options = null)
        {
            return FillTemplate(template, values, options).Project;
        }

        private static ClipDraftDTO[] BuildTemplatedElements(
            ClipDraftDTO[] source,
            string elementPrefix,
            IEnumerable<string> fields,
            IDictionary<string, string?> vars,
            DraftTemplateBuildOptions options)
        {
            var result = new List<ClipDraftDTO>();
            int index = 0;

            foreach (var element in source ?? [])
            {
                var node = JsonSerializer.SerializeToNode(element) as JsonObject;
                if (node == null)
                {
                    result.Add(element);
                    index++;
                    continue;
                }

                foreach (var field in fields)
                {
                    if (string.IsNullOrWhiteSpace(field)) continue;
                    if (!node.TryGetPropertyValue(field, out var current)) continue;

                    var variableKey = $"{elementPrefix}{index}.{field}";
                    vars[variableKey] = JsonNodeToVariableString(current);
                    node[field] = BuildPlaceholder(variableKey, options);
                }

                var modified = node.Deserialize<ClipDraftDTO>(_defaultSerializerOptions);
                result.Add(modified ?? element);
                index++;
            }

            return [.. result];
        }

        private static SoundtrackDTO[] BuildTemplatedSoundtrackElements(
            SoundtrackDTO[] source,
            string elementPrefix,
            IEnumerable<string> fields,
            IDictionary<string, string?> vars,
            DraftTemplateBuildOptions options)
        {
            var result = new List<SoundtrackDTO>();
            int index = 0;

            foreach (var element in source ?? [])
            {
                var node = JsonSerializer.SerializeToNode(element) as JsonObject;
                if (node == null)
                {
                    result.Add(element);
                    index++;
                    continue;
                }

                foreach (var field in fields)
                {
                    if (string.IsNullOrWhiteSpace(field)) continue;
                    if (!node.TryGetPropertyValue(field, out var current)) continue;

                    var variableKey = $"{elementPrefix}{index}.{field}";
                    vars[variableKey] = JsonNodeToVariableString(current);
                    node[field] = BuildPlaceholder(variableKey, options);
                }

                var modified = node.Deserialize<SoundtrackDTO>(_defaultSerializerOptions);
                result.Add(modified ?? element);
                index++;
            }

            return [.. result];
        }

        private static ClipDraftDTO[] FillElements(
            ClipDraftDTO[] source,
            IReadOnlyDictionary<string, string?> values,
            IReadOnlyDictionary<string, string?> defaults,
            DraftTemplateFillOptions options)
        {
            var result = new List<ClipDraftDTO>();

            foreach (var element in source ?? [])
            {
                var node = JsonSerializer.SerializeToNode(element);
                ReplacePlaceholders(node, values, defaults, options);
                var filled = node.Deserialize<ClipDraftDTO>(_defaultSerializerOptions);
                result.Add(filled ?? element);
            }

            return [.. result];
        }

        private static SoundtrackDTO[] FillSoundtrackElements(
            SoundtrackDTO[] source,
            IReadOnlyDictionary<string, string?> values,
            IReadOnlyDictionary<string, string?> defaults,
            DraftTemplateFillOptions options)
        {
            var result = new List<SoundtrackDTO>();

            foreach (var element in source ?? [])
            {
                var node = JsonSerializer.SerializeToNode(element);
                ReplacePlaceholders(node, values, defaults, options);
                var filled = node.Deserialize<SoundtrackDTO>(_defaultSerializerOptions);
                result.Add(filled ?? element);
            }

            return [.. result];
        }

        private static void ReplacePlaceholders(
            JsonNode? node,
            IReadOnlyDictionary<string, string?> values,
            IReadOnlyDictionary<string, string?> defaults,
            DraftTemplateFillOptions options)
        {
            if (node is JsonObject obj)
            {
                var keys = obj.Select(kv => kv.Key).ToArray();
                foreach (var key in keys)
                {
                    var current = obj[key];
                    if (current is JsonValue val && TryGetPlaceholderKey(val, out var placeholderKey))
                    {
                        if (!TryResolveVariable(placeholderKey, values, defaults, out var resolved))
                        {
                            if (options.ThrowIfMissingValue)
                            {
                                throw new KeyNotFoundException($"Missing template variable: {placeholderKey}");
                            }
                            continue;
                        }

                        obj[key] = ConvertResolvedValue(resolved);
                    }
                    else
                    {
                        ReplacePlaceholders(current, values, defaults, options);
                    }
                }
                return;
            }

            if (node is JsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    var current = arr[i];
                    if (current is JsonValue val && TryGetPlaceholderKey(val, out var placeholderKey))
                    {
                        if (!TryResolveVariable(placeholderKey, values, defaults, out var resolved))
                        {
                            if (options.ThrowIfMissingValue)
                            {
                                throw new KeyNotFoundException($"Missing template variable: {placeholderKey}");
                            }
                            continue;
                        }

                        arr[i] = ConvertResolvedValue(resolved);
                    }
                    else
                    {
                        ReplacePlaceholders(current, values, defaults, options);
                    }
                }
            }
        }

        private static bool TryResolveVariable(
            string key,
            IReadOnlyDictionary<string, string?> values,
            IReadOnlyDictionary<string, string?> defaults,
            out string? resolved)
        {
            if (values.TryGetValue(key, out resolved)) return true;
            if (values.TryGetValue(BuildRawPlaceholderKey(key), out resolved)) return true;
            if (defaults.TryGetValue(key, out resolved)) return true;
            return false;
        }

        private static JsonNode? ConvertResolvedValue(string? value)
        {
            if (value is null) return null;
            if (value.StartsWith("json:", StringComparison.OrdinalIgnoreCase))
            {
                return JsonNode.Parse(value.Substring(5));
            }

            if (bool.TryParse(value, out var b)) return JsonValue.Create(b);
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return JsonValue.Create(i);
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);

            return JsonValue.Create(value);
        }

        private static string BuildPlaceholder(string variableKey, DraftTemplateBuildOptions options)
        {
            return $"{options.PlaceholderPrefix}{variableKey}{options.PlaceholderSuffix}";
        }

        private static bool TryGetPlaceholderKey(JsonValue value, out string key)
        {
            key = string.Empty;
            if (!value.TryGetValue<string>(out var str) || string.IsNullOrWhiteSpace(str)) return false;

            str = str.Trim();
            if (!str.StartsWith("{{", StringComparison.Ordinal) || !str.EndsWith("}}", StringComparison.Ordinal)) return false;

            key = str.Substring(2, str.Length - 4).Trim();
            return !string.IsNullOrWhiteSpace(key);
        }

        private static string BuildRawPlaceholderKey(string key)
        {
            return $"{{{{{key}}}}}";
        }

        private static string? JsonNodeToVariableString(JsonNode? node)
        {
            if (node is null) return null;
            if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
            if (node is JsonValue v2 && v2.TryGetValue<bool>(out var b)) return b ? "true" : "false";
            return node.ToJsonString();
        }

        private static T DeepClone<T>(T source)
        {
            var serialized = JsonSerializer.Serialize(source);
            return JsonSerializer.Deserialize<T>(serialized)
                ?? throw new JsonException($"Failed to clone object type {typeof(T).Name}.");
        }
    }



}
