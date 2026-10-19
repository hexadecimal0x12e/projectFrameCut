using projectFrameCut.Shared;
using Microsoft.Extensions.AI;
using projectFrameCut.AIAssistance;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.Text;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace projectFrameCut.ApplicationPluginBase.Text;

public sealed class LlmTranslateTextStyleUIProvider : TextClipStyleUIProvider
{
    public override PropertyPanelBuilder CreateUI(ITextClipStyleProvider source)
    {
        var panel = base.CreateUI(source);
        panel.AddEntry("TranslationTargetLanguage", "Target Language", source.GetParameters()["TranslationTargetLanguage"], "zh-CN");
        panel.AddCheckbox("TranslationAutoGenerate", "Auto Generate",
            !string.Equals(source.GetParameters()["TranslationAutoGenerate"], "false", StringComparison.OrdinalIgnoreCase));
        panel.AddButton("GenerateTranslation", "Generate Translation");
        return panel;
    }

    public override (int newWidth, int newHeight) HandlePropertyPanelChange(ITextClipStyleProvider source, PropertyPanelPropertyChangedEventArgs args)
    {
        if (args.Id is "TranslationTargetLanguage" or "TranslationAutoGenerate")
            source.GetParameters()[args.Id] = args.Value?.ToString() ?? "";
        var size = base.HandlePropertyPanelChange(source, args);
        if (args.Id == "GenerateTranslation" || args.Id is "Text" or "TranslationTargetLanguage"
            && !string.Equals(source.GetParameters()["TranslationAutoGenerate"], "false", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string language = source.GetParameters()["TranslationTargetLanguage"];
                string text = source.BasicText;
                var client = AssistanceChatView.CreateChatClient();
                if (client is not null && !string.IsNullOrWhiteSpace(text))
                {
                    string translation = TaskHelper.SyncWait(async () =>
                    {
                        var response = await client.GetResponseAsync(new List<AIChatMessage>
                        {
                            new(ChatRole.System, $"You are a professional translator. Translate user text to {language}. Return only translated text without explanations or quotes."),
                            new(ChatRole.User, text),
                        });
                        return response.Text?.Trim() ?? "";
                    }, timeoutMs: 30000, DefaultValue: source.GetParameters()["TranslationText"]);
                    source.HandleSettableFieldsChange(source.SettableFields["TranslationText"], translation, out _);
                    source.MetaData["TranslationSourceCache"] = text;
                    var rect = source.GetViewRect(1920, 1080);
                    return (rect.TargetWidth, rect.TargetHeight);
                }
            }
            catch (Exception ex) { Logger.Log(ex, "Generate text style translation", typeof(LlmTranslateTextStyleUIProvider)); }
        }
        return size;
    }
}
