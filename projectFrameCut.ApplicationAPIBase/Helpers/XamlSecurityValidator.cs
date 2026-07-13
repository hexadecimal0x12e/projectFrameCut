using System.Text.RegularExpressions;
using System.Xml;
using projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML;

namespace projectFrameCut.ApplicationAPIBase.Helpers;

/// <summary>
/// 对动态加载的 XAML 进行安全检查，防止恶意 XAML 构造导致安全问题。
///
/// 在调用 <c>LoadFromXaml</c> 之前使用两阶段检查：
/// <list type="number">
///   <item><b>预扫描 (PreScan)</b> — 在原始用户输入上进行快速文本级扫描。</item>
///   <item><b>深度验证 (Validate)</b> — 在修复/包裹后的完整 XAML 上使用 XmlReader 逐元素检查。</item>
/// </list>
/// </summary>
internal static class XamlSecurityValidator
{
    // ────────────────────────────── 正则：预扫描 ──────────────────────────────

    /// <summary>x:FactoryMethod — 可用于调用静态工厂方法，潜在 RCE</summary>
    private static readonly Regex FactoryMethodPattern = new(
        @"\bFactoryMethod\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>x:TypeArguments — 可用于实例化任意泛型类型</summary>
    private static readonly Regex TypeArgumentsPattern = new(
        @"\bTypeArguments\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>CLR 命名空间声明 — 可用于访问任意 .NET 类型</summary>
    private static readonly Regex ClrNamespacePattern = new(
        @"xmlns(?::\w+)?\s*=\s*[""']clr-namespace:[^""']*[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>WebView 元素 — 可用于 SSRF / XSS</summary>
    private static readonly Regex WebViewElementPattern = new(
        @"<\s*WebView[\s>\/]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Source 属性中的网络/文件 URI — 可用于 SSRF 或本地文件读取</summary>
    private static readonly Regex NetworkSourceAttrPattern = new(
        @"Source\s*=\s*[""'](https?|file)://[^""']*[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>ResourceDictionary 引用外部 Source</summary>
    private static readonly Regex ResourceDictionarySourcePattern = new(
        @"<\s*ResourceDictionary[^>]*\bSource\s*=\s*[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ──────────────────────────────── 公共 API ────────────────────────────────

    /// <summary>
    /// 对用户输入的原始 XAML 进行快速安全预扫描。
    /// 此检查在 <c>FixIncompleteXml</c> / 模板包裹之前执行，用于尽早拦截明显恶意内容。
    /// </summary>
    /// <param name="rawXaml">用户提供的原始 XAML 代码。</param>
    /// <exception cref="XamlSecurityException">检测到不安全内容时抛出。</exception>
    public static void PreScan(string rawXaml)
    {
        if (string.IsNullOrWhiteSpace(rawXaml))
            return;

        // 1. x:FactoryMethod — 可调用任意静态工厂方法
        if (FactoryMethodPattern.IsMatch(rawXaml))
            throw new XamlSecurityException(
                "x:FactoryMethod is not allowed for security reasons.");

        // 2. x:TypeArguments — 可实例化任意泛型类型
        if (TypeArgumentsPattern.IsMatch(rawXaml))
            throw new XamlSecurityException(
                "x:TypeArguments is not allowed for security reasons.");

        // 3. clr-namespace 声明 — 可引用任意 .NET 程序集中的类型
        if (ClrNamespacePattern.IsMatch(rawXaml))
            throw new XamlSecurityException(
                "Custom CLR namespace declarations are not allowed for security reasons.");

        // 4. WebView — 可直接发起 HTTP 请求 (SSRF/XSS)
        if (WebViewElementPattern.IsMatch(rawXaml))
            throw new XamlSecurityException(
                "WebView element is not allowed for security reasons.");

        // 5. Source 属性中的网络/文件 URI — SSRF 或本地文件读取
        //    仅当用户设置为禁止外部源时才拦截
        if (!Markdown2XAML.SecurityEnableXAMLExternalSource && NetworkSourceAttrPattern.IsMatch(rawXaml))
            throw new XamlSecurityException(
                "Network and file URIs are not allowed in Source attributes for security reasons.");

        // 6. ResourceDictionary 引用外部 Source — 可加载恶意 XAML
        //    仅当用户设置为禁止外部源时才拦截
        if (!Markdown2XAML.SecurityEnableXAMLExternalSource && ResourceDictionarySourcePattern.IsMatch(rawXaml))
            throw new XamlSecurityException(
                "ResourceDictionary with external Source is not allowed for security reasons.");
    }

    /// <summary>
    /// 对即将传递给 <c>LoadFromXaml</c> 的完整 XAML 文档进行深度验证。
    /// 使用 <c>XmlReader</c> 逐元素扫描，精确检测危险模式。
    /// </summary>
    /// <param name="completeXaml">已验证为结构完整的 XAML 文档字符串。</param>
    /// <exception cref="XamlSecurityException">检测到不安全内容时抛出。</exception>
    public static void Validate(string completeXaml)
    {
        if (string.IsNullOrWhiteSpace(completeXaml))
            return;

        var settings = new XmlReaderSettings
        {
            // 禁止 DTD 处理（防止 XXE 攻击）
            DtdProcessing = DtdProcessing.Prohibit,
            // 禁止外部解析器解析任何外部资源
            XmlResolver = null,
            // 限制实体展开防止亿万分实体炸弹 (billion laughs)
            MaxCharactersFromEntities = 1024,
            // 忽略无关节点类型，专注元素内容和属性
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(completeXaml), settings);

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    string localName = reader.LocalName;

                    // ── 元素级别检查 ──

                    // 禁止 WebView
                    if (string.Equals(localName, "WebView", StringComparison.OrdinalIgnoreCase))
                        throw new XamlSecurityException(
                            "WebView is not allowed for security reasons.");

                    // 禁止 ResourceDictionary 引用外部 Source（仅在用户禁止外部源时）
                    if (!Markdown2XAML.SecurityEnableXAMLExternalSource
                        && string.Equals(localName, "ResourceDictionary", StringComparison.OrdinalIgnoreCase))
                    {
                        string? sourceAttr = reader.GetAttribute("Source");
                        if (!string.IsNullOrEmpty(sourceAttr))
                            throw new XamlSecurityException(
                                "ResourceDictionary with external Source is not allowed for security reasons.");
                    }

                    // ── 属性级别检查 ──
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            string attrNs = reader.NamespaceURI;
                            string attrLocal = reader.LocalName;

                            // x:FactoryMethod (namespace-qualified check)
                            if (attrNs == "http://schemas.microsoft.com/winfx/2009/xaml" &&
                                string.Equals(attrLocal, "FactoryMethod", StringComparison.Ordinal))
                                throw new XamlSecurityException(
                                    "x:FactoryMethod is not allowed for security reasons.");

                            // x:TypeArguments (namespace-qualified check)
                            if (attrNs == "http://schemas.microsoft.com/winfx/2009/xaml" &&
                                string.Equals(attrLocal, "TypeArguments", StringComparison.Ordinal))
                                throw new XamlSecurityException(
                                    "x:TypeArguments is not allowed for security reasons.");

                            // Source 中的网络/文件 URI（仅在用户禁止外部源时）
                            if (!Markdown2XAML.SecurityEnableXAMLExternalSource
                                && string.Equals(attrLocal, "Source", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrEmpty(reader.Value))
                            {
                                string val = reader.Value.Trim();
                                if (val.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                    val.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                    val.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                                    throw new XamlSecurityException(
                                        "Network and file URIs in Source attributes are not allowed for security reasons.");
                            }

                        } while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }
                }

                // ── 属性元素语法检查 (如 <Image.Source>http://...</Image.Source>) ──
                if (reader.NodeType == XmlNodeType.Element &&
                    reader.IsStartElement() &&
                    reader.LocalName.IndexOf(".Source", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string text = reader.ReadString()?.Trim() ?? string.Empty;
                    if (!string.IsNullOrEmpty(text) &&
                        (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                         text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)))
                        throw new XamlSecurityException(
                            "Network and file URIs in property element Source are not allowed for security reasons.");
                }
            }
        }
        catch (XamlSecurityException)
        {
            // 安全异常直接向上传播
            throw;
        }
        catch (XmlException)
        {
            // XML 结构异常 — 说明 FixIncompleteXml 产生了无效 XML。
            // LoadFromXaml 同样会失败，但提前拦截更安全。
            throw new XamlSecurityException(
                "Invalid XML structure — cannot safely render this XAML.");
        }
    }
}

/// <summary>
/// 表示 XAML 安全检查失败。
/// </summary>
public class XamlSecurityException : InvalidOperationException
{
    public XamlSecurityException(string message) : base(message) { }
}
