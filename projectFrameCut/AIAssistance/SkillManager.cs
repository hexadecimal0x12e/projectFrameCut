namespace projectFrameCut.AIAssistance;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using projectFrameCut.Shared;

/// <summary>
/// 表示一个可用的 Skill 元数据。
/// </summary>
public sealed class SkillInfo
{
    /// <summary>Skill 的唯一名称标识符。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Skill 的简短描述，供 LLM 判断是否使用。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>来源路径（调试/溯源用）。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>是否为内置 Skill（来自应用资源包）。</summary>
    public bool IsBuiltin { get; init; }
}

/// <summary>
/// 管理 SKILL.md 的发现、加载和缓存。
/// 扫描两个来源：
///   1) 内置：AIAgent/skills/（打包为 MauiAsset 的应用资源）
///   2) 项目级：&lt;projectPath&gt;/.ai/skills/（用户自定义，同名覆盖内置）
/// </summary>
public sealed class SkillManager
{
    // ────────────────────────────── 常量 ──────────────────────────────

    private const string BuiltinSkillsSubPath = "AIAgent/skills";
    private const string ManifestFileName = "manifest.json";

    // ────────────────────────────── 静态实例缓存 ──────────────────────────────

    private static readonly ConcurrentDictionary<string, SkillManager> Instances = new(StringComparer.OrdinalIgnoreCase);

    // ────────────────────────────── 实例状态 ──────────────────────────────

    private readonly string? _projectPath;
    private string? _globalSkillsDirectory;

    /// <summary>技能列表缓存（扫描后填充）。</summary>
    private List<SkillInfo>? _cachedSkills;

    /// <summary>技能内容缓存（名称 → 全文）。</summary>
    private readonly ConcurrentDictionary<string, string> _contentCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上次扫描时间戳，用于缓存失效。</summary>
    private DateTime _lastScanTime = DateTime.MinValue;

    /// <summary>缓存的生存周期（秒）。</summary>
    private const int CacheLifetimeSeconds = 30;

    private readonly object _scanLock = new();

    // ────────────────────────────── 构造 / 工厂 ──────────────────────────────

    private SkillManager(string? projectPath)
    {
        _projectPath = projectPath;
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            _globalSkillsDirectory = Path.Combine(MauiProgram.DataPath, "My Skills");
        }
    }

    /// <summary>
    /// 获取或创建指定项目的 SkillManager 实例。
    /// projectPath 为 null 时只加载内置 Skill。
    /// </summary>
    public static SkillManager ForProject(string? projectPath)
    {
        string normalizedProjectPath = NormalizeProjectPath(projectPath);
        return Instances.GetOrAdd(normalizedProjectPath, key => new SkillManager(key));
    }

    private static string NormalizeProjectPath(string? projectPath) =>
        string.IsNullOrWhiteSpace(projectPath) ? "<global>" : Path.GetFullPath(projectPath);

    // ────────────────────────────── 公共 API ──────────────────────────────

    /// <summary>
    /// 列出所有可用的 Skill。
    /// 项目级 Skill 的 Name 与内置同名时，项目级版本会覆盖内置版本（插入到列表相同位置）。
    /// </summary>
    public List<SkillInfo> ListAvailableSkills()
    {
        EnsureScanned();
        return _cachedSkills ?? [];
    }

    /// <summary>
    /// 按名称加载 Skill 的完整 Markdown 内容（含 frontmatter）。
    /// 返回 null 表示未找到。
    /// </summary>
    public string? LoadSkillContent(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // 先从缓存读取
        if (_contentCache.TryGetValue(name, out string? cached))
            return cached;

        // 检查目标是否存在
        SkillInfo? info = ListAvailableSkills().FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (info is null)
            return null;

        string? content = ReadSkillFile(info);
        if (content is not null)
        {
            _contentCache[name] = content;
        }
        return content;
    }

    /// <summary>
    /// 检查指定名称的 Skill 是否存在。
    /// </summary>
    public bool SkillExists(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return ListAvailableSkills().Any(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 清空缓存，下次查询时重新扫描。
    /// </summary>
    public void ClearCache()
    {
        lock (_scanLock)
        {
            _cachedSkills = null;
            _contentCache.Clear();
            _lastScanTime = DateTime.MinValue;
        }
    }

    // ────────────────────────────── 扫描 ──────────────────────────────

    private void EnsureScanned()
    {
        // 缓存未过期则跳过
        if (_cachedSkills is not null &&
            (DateTime.UtcNow - _lastScanTime).TotalSeconds < CacheLifetimeSeconds)
            return;

        lock (_scanLock)
        {
            // 双重检查锁定
            if (_cachedSkills is not null &&
                (DateTime.UtcNow - _lastScanTime).TotalSeconds < CacheLifetimeSeconds)
                return;

            try
            {
                _cachedSkills = ScanSkills();
                _lastScanTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "SkillManager.ScanSkills", typeof(SkillManager));
                _cachedSkills ??= [];
            }
        }
    }

    private List<SkillInfo> ScanSkills()
    {
        var builtinSkills = new List<SkillInfo>();
        var projectSkills = new List<SkillInfo>();

        // 1) 从 manifest 加载内置 Skill
        try
        {
            builtinSkills = LoadBuiltinSkillsFromManifest();
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Load built-in skills", typeof(SkillManager));
        }

        // 2) 从项目目录扫描项目级 Skill
        try
        {
            if (!string.IsNullOrWhiteSpace(_globalSkillsDirectory) && Directory.Exists(_globalSkillsDirectory))
            {
                projectSkills = ScanDirectorySkills(_globalSkillsDirectory, isBuiltin: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Load project skills", typeof(SkillManager));
        }

        // 合并：项目级 Skill 覆盖同名内置 Skill
        var projectSkillNames = new HashSet<string>(projectSkills.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

        var merged = new List<SkillInfo>(builtinSkills.Count + projectSkills.Count);

        foreach (SkillInfo builtin in builtinSkills)
        {
            if (!projectSkillNames.Contains(builtin.Name))
            {
                merged.Add(builtin);
            }
        }

        merged.AddRange(projectSkills);

        return merged;
    }

    // ────────────────────────────── 内置 Skill 加载 ──────────────────────────────

    /// <summary>
    /// 从应用的打包资源中加载内置 Skill manifest。
    /// </summary>
    private List<SkillInfo> LoadBuiltinSkillsFromManifest()
    {
        var results = new List<SkillInfo>();

        // 读取 manifest.json
        string manifestPath = $"{BuiltinSkillsSubPath}/{ManifestFileName}";

        try
        {
            using Stream stream = FileSystem.OpenAppPackageFileAsync(manifestPath).GetAwaiter().GetResult();
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();

            var manifest = JsonSerializer.Deserialize<SkillManifest>(json);
            if (manifest?.Skills is null)
                return results;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var entry in manifest.Skills)
            {
                if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Description))
                    continue;

                string fileName = entry.File ?? $"{entry.Name}.md";
                results.Add(new SkillInfo
                {
                    Name = entry.Name,
                    Description = entry.Description,
                    SourcePath = $"{BuiltinSkillsSubPath}/{fileName}",
                    IsBuiltin = true,
                });
            }
        }
        catch (FileNotFoundException)
        {
            // manifest 不存在时回退：尝试直接从打包资源路径加载已知 skill 列表
            // 这个回退方案在无法列举目录内容时使用
            results.AddRange(LoadBuiltinSkillsByConvention());
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Load built-in skill manifest", typeof(SkillManager));
            results.AddRange(LoadBuiltinSkillsByConvention());
        }

        return results;
    }

    /// <summary>
    /// 回退方案：通过硬编码的约定加载内置 Skill。
    /// 用于 manifest.json 不存在或加载失败时。
    /// </summary>
    private static List<SkillInfo> LoadBuiltinSkillsByConvention()
    {
        // 这些是已知的内置 Skill 文件，通过约定自动发现
        // 当有新的内置 Skill 文件添加时，需要在此处更新
        var knownBuiltin = new (string Name, string Description, string File)[]
        {
            ("subtitle-generation", "指导如何生成视频字幕的样式、格式和时间轴对齐", "subtitle-generation.md"),
            ("color-correction", "指导如何进行视频颜色校正，包括白平衡、对比度、饱和度、色轮等参数的调整", "color-correction.md"),
        };

        return knownBuiltin
            .Select(e => new SkillInfo
            {
                Name = e.Name,
                Description = e.Description,
                SourcePath = $"{BuiltinSkillsSubPath}/{e.File}",
                IsBuiltin = true,
            })
            .ToList();
    }

    // ────────────────────────────── 项目级 Skill 扫描 ──────────────────────────────

    /// <summary>
    /// 扫描文件系统目录，收集 .md 文件作为项目级 Skill。
    /// 同样解析 frontmatter 提取 name 和 description。
    /// </summary>
    private static List<SkillInfo> ScanDirectorySkills(string directory, bool isBuiltin)
    {
        var results = new List<SkillInfo>();

        foreach (string filePath in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(filePath);

            // 跳过 manifest 和 README 等非 skill 文件
            if (string.Equals(fileName, ManifestFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "README.md", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                string content = File.ReadAllText(filePath);
                var (name, description) = ParseFrontMatter(content);

                if (string.IsNullOrWhiteSpace(name))
                {
                    // 没有 frontmatter 时使用文件名（不含扩展名）作为 name
                    name = Path.GetFileNameWithoutExtension(fileName);
                }

                results.Add(new SkillInfo
                {
                    Name = name,
                    Description = description ?? $"项目级 Skill：{name}",
                    SourcePath = filePath,
                    IsBuiltin = isBuiltin,
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SkillManager] Failed to parse skill '{filePath}': {ex.Message}");
            }
        }

        return results;
    }

    // ────────────────────────────── 文件读取 ──────────────────────────────

    /// <summary>
    /// 根据 SkillInfo 读取完整 Markdown 内容。
    /// </summary>
    private string? ReadSkillFile(SkillInfo info)
    {
        try
        {
            if (info.IsBuiltin)
            {
                // 内置 Skill：从应用打包资源读取
                using Stream stream = FileSystem.OpenAppPackageFileAsync(info.SourcePath).GetAwaiter().GetResult();
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            else
            {
                // 项目级 Skill：从文件系统读取
                if (File.Exists(info.SourcePath))
                {
                    return File.ReadAllText(info.SourcePath);
                }
                return null;
            }
        }
        catch (FileNotFoundException)
        {
            Debug.WriteLine($"[SkillManager] Skill file not found: {info.SourcePath}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log(ex, $"Read skill '{info.Name}'", typeof(SkillManager));
            return null;
        }
    }

    // ────────────────────────────── Frontmatter 解析 ──────────────────────────────

    /// <summary>
    /// 从 Markdown 文本中解析 YAML frontmatter。
    /// 只提取 name 和 description 字段。
    /// </summary>
    public static (string? Name, string? Description) ParseFrontMatter(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return (null, null);

        // frontmatter 必须是文件最开头的 --- 块
        if (!markdown.TrimStart().StartsWith("---"))
            return (null, null);

        int endIndex = markdown.IndexOf("---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return (null, null);

        string frontmatterBlock = markdown[3..endIndex].Trim();

        string? name = null;
        string? description = null;

        foreach (string line in frontmatterBlock.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                name = trimmed["name:".Length..].Trim().Trim('"', '\'', ' ');
            }
            else if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                description = trimmed["description:".Length..].Trim().Trim('"', '\'', ' ');
            }
        }

        return (name, description);
    }

    // ────────────────────────────── 序列化类型 ──────────────────────────────

    private sealed class SkillManifest
    {
        [System.Text.Json.Serialization.JsonPropertyName("skills")]
        public List<SkillManifestEntry>? Skills { get; init; }
    }

    private sealed class SkillManifestEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("description")]
        public string? Description { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("file")]
        public string? File { get; init; }
    }
}

// ─────────────────────────────────────────────────────────────────────
// SkillRegistry — 技能加载状态的静态跟踪器
// 供 AITools 和 AssistanceChatView 共享使用
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// 按会话跟踪当前已加载的 Skill 名称列表。
/// 静态类，可跨组件访问。
/// </summary>
public static class SkillRegistry
{
    private static readonly ConcurrentDictionary<string, HashSet<string>> SessionSkills = new(StringComparer.OrdinalIgnoreCase);

    internal static event Action<string>? Changed;

    /// <summary>当前是否有流式请求正在进行（由 AssistanceChatView 设置）。</summary>
    public static bool IsStreaming { get; set; }

    /// <summary>
    /// 当前活动会话 ID。由 AssistanceChatView 在每次构建工具前设置。
    /// </summary>
    public static string? CurrentSessionId { get; set; }

    /// <summary>
    /// 为当前活动会话加载一个 Skill。
    /// </summary>
    public static bool LoadSkill(string skillName)
    {
        string? sessionId = CurrentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(skillName))
            return false;

        var skills = SessionSkills.GetOrAdd(sessionId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        bool added;
        lock (skills)
        {
            added = skills.Add(skillName);
        }

        if (added)
            Changed?.Invoke(sessionId);

        return added;
    }

    /// <summary>
    /// 为当前活动会话卸载一个 Skill。
    /// </summary>
    public static bool UnloadSkill(string skillName)
    {
        string? sessionId = CurrentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(skillName))
            return false;

        if (IsStreaming)
            return false;

        if (!SessionSkills.TryGetValue(sessionId, out var skills))
            return false;

        lock (skills)
        {
            return skills.Remove(skillName);
        }
    }

    /// <summary>
    /// 获取当前活动会话已加载的 Skill 名称列表。
    /// </summary>
    public static IEnumerable<string> GetLoadedSkills()
    {
        string? sessionId = CurrentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || !SessionSkills.TryGetValue(sessionId, out var skills))
            return [];

        lock (skills)
        {
            return skills.ToList();
        }
    }

    /// <summary>
    /// 检查指定 Skill 在当前会话是否已加载。
    /// </summary>
    public static bool IsSkillLoaded(string skillName)
    {
        string? sessionId = CurrentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(skillName))
            return false;

        if (!SessionSkills.TryGetValue(sessionId, out var skills))
            return false;

        lock (skills)
        {
            return skills.Contains(skillName);
        }
    }

    /// <summary>
    /// 清除指定会话的所有已加载 Skill。
    /// </summary>
    public static void ClearSession(string? sessionId = null)
    {
        sessionId ??= CurrentSessionId;
        if (sessionId is not null)
            SessionSkills.TryRemove(sessionId, out _);
    }
}
