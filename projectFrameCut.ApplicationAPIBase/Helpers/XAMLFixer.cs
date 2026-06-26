using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;

namespace projectFrameCut.ApplicationAPIBase.Helpers
{
    public static class XAMLFixer
    {
        public static View FixXamlAndGenerateView(string xaml)
        {
            string fixedXaml =
                $"""
                <?xml version="1.0" encoding="utf-8" ?>
                <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
                             xmlns:toolkit="http://schemas.microsoft.com/dotnet/2022/maui/toolkit"
                             xmlns:v="clr-namespace:projectFrameCut.ApplicationAPIBase.MarkdownToXAML"
                             x:Class="projectFrameCut.ApplicationAPIBase.Helpers.DynamicGenerateView">
                    {FixIncompleteXml(xaml)}
                </ContentView>
                """;
            
            var view = new ContentView();
            view.LoadFromXaml(fixedXaml);
            return view;
        }

        public static string FixIncompleteXml(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            var openTags = new Stack<string>();   // 记录未闭合的开始标签
            var output = new StringBuilder();     // 输出结果
            int i = 0;
            int len = input.Length;

            while (i < len)
            {
                if (input[i] == '<')
                {
                    int tagStart = i;            // 当前标记的起始位置
                    i++;                         // 跳过 '<'

                    if (i >= len)
                    {
                        // 输入以 '<' 结尾，属于不完整的标签，直接忽略
                        break;
                    }

                    char next = input[i];

                    // ----- 结束标签 -----
                    if (next == '/')
                    {
                        i++; // 跳过 '/'
                        int nameStart = i;

                        while (i < len && input[i] != '>')
                            i++;

                        if (i >= len)
                        {
                            // 不完整的结束标签，忽略并跳出
                            break;
                        }

                        string tagName = input.Substring(nameStart, i - nameStart).Trim();
                        i++; // 跳过 '>'

                        // 将结束标签原样写入输出
                        output.Append(input, tagStart, i - tagStart);

                        // 如果与栈顶匹配，则弹出；否则保留（假定输入结构正确）
                        if (openTags.Count > 0 && openTags.Peek() == tagName)
                            openTags.Pop();
                    }
                    // ----- 注释 / CDATA / DOCTYPE -----
                    else if (next == '!')
                    {
                        // 注释
                        if (i + 2 < len && input[i + 1] == '-' && input[i + 2] == '-')
                        {
                            i += 3; // 跳过 '!--'
                            while (i < len - 2 && !(input[i] == '-' && input[i + 1] == '-' && input[i + 2] == '>'))
                                i++;
                            if (i >= len - 2)
                                break;     // 注释未闭合，忽略并跳出
                            i += 3;        // 跳过 '-->'
                        }
                        // CDATA
                        else if (i + 6 < len && input.Substring(i + 1, 6) == "CDATA[")
                        {
                            i += 7; // 跳过 '![CDATA['
                            while (i < len - 2 && !(input[i] == ']' && input[i + 1] == ']' && input[i + 2] == '>'))
                                i++;
                            if (i >= len - 2)
                                break;     // CDATA 未闭合，忽略并跳出
                            i += 3;        // 跳过 ']]>'
                        }
                        // DOCTYPE 或其他 <! 声明
                        else
                        {
                            i++; // 跳过 '!'
                            while (i < len && input[i] != '>')
                                i++;
                            if (i >= len)
                                break;
                            i++; // 跳过 '>'
                        }
                        output.Append(input, tagStart, i - tagStart);
                    }
                    // ----- 处理指令 -----
                    else if (next == '?')
                    {
                        i++;
                        while (i < len - 1 && !(input[i] == '?' && input[i + 1] == '>'))
                            i++;
                        if (i >= len - 1)
                            break;
                        i += 2; // 跳过 '?>'
                        output.Append(input, tagStart, i - tagStart);
                    }
                    // ----- 开始标签 -----
                    else
                    {
                        // 读取标签名（允许字母、数字、冒号、下划线、连字符、点号）
                        int nameStart = i;
                        while (i < len && (char.IsLetterOrDigit(input[i]) || input[i] == ':' ||
                                           input[i] == '_' || input[i] == '-' || input[i] == '.'))
                            i++;

                        if (i == nameStart)
                        {
                            // 无效的标签名，将 '<' 当作文本
                            output.Append('<');
                            continue;
                        }

                        string tagName = input.Substring(nameStart, i - nameStart);

                        // 扫描属性直到遇到 '>' 或 '/>'
                        bool selfClosing = false;
                        bool tagClosed = false;

                        while (i < len)
                        {
                            char ch = input[i];

                            if (ch == '>')
                            {
                                i++;
                                tagClosed = true;
                                break;
                            }

                            if (ch == '/' && i + 1 < len && input[i + 1] == '>')
                            {
                                selfClosing = true;
                                i += 2;
                                tagClosed = true;
                                break;
                            }

                            // 跳过引号内的属性值
                            if (ch == '"' || ch == '\'')
                            {
                                char quote = ch;
                                i++;
                                while (i < len && input[i] != quote)
                                    i++;
                                if (i < len) i++; // 跳过结束引号
                            }
                            else
                            {
                                i++;
                            }
                        }

                        if (!tagClosed)
                        {
                            // 标签未闭合，说明输入在此处截断，忽略该标签并跳出
                            break;
                        }

                        // 将完整的标签写入输出
                        output.Append(input, tagStart, i - tagStart);

                        if (!selfClosing)
                        {
                            openTags.Push(tagName);
                        }
                    }
                }
                else
                {
                    // 普通文本
                    output.Append(input[i]);
                    i++;
                }
            }

            // 补全所有未闭合的标签
            while (openTags.Count > 0)
            {
                string tag = openTags.Pop();
                output.Append("</");
                output.Append(tag);
                output.Append('>');
            }

            return output.ToString();
        }
    }
}
