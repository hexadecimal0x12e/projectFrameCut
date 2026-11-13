using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.ApplicationPluginBase.Effect;

public class KeyFramedEffectUI(IEffectProvider inner) : EffectProviderUI(inner), IKeyFramedEffectUIProvider
{
    public override PropertyPanelBuilder CreateUI(IEffectProvider source)
    {
        var panel = new PropertyPanelBuilder();
        panel.AddText(new SingleLineLabel(
            EffectProviderHelper.L("Effect_ProgressPlacer_Desc", "Configure keyframes in the Keyframe tab."), 14));
        return panel;
    }

    public virtual PropertyPanelBuilder CreateStepUI(IKeyFrameStep step)
    {
        var panel = new PropertyPanelBuilder();
        EffectProviderUIHelper.BuildUI(Inner, panel, null, fields: step.Fields);
        return panel;
    }

    public bool HandleStepUIChange(IKeyFrameStep step, PropertyPanelPropertyChangedEventArgs args)
    {
        if (args.Id == "StepEditorRefresh") return true;
        string id = EffectProviderUIHelper.FieldId(Inner, args.Id);
        if (!step.Fields.ContainsKey(id)) return false;
        step.SetValues(new Dictionary<string, object> { [id] = args.Value! });
        return true;
    }

    protected void AddStepEditor(PropertyPanelBuilder panel, Func<(View Content, Action Save)> create)
    {
        panel.AddButton(EffectProviderHelper.L("Effect_ProgressPlacer_OpenEditor", "Open editor"), async (_, _) =>
        {
            try
            {
                if (App.GetCurrentPage() is not DraftPage page) return;
                var editor = create();
                var content = new VerticalStackLayout { Spacing = 10, Padding = 20 };
                content.Children.Add(editor.Content);
                var buttons = new HorizontalStackLayout { Spacing = 10, HorizontalOptions = LayoutOptions.End };
                var cancel = new Button { Text = Localized._Cancel };
                cancel.Clicked += async (_, _) => await page.HidePopup();
                var save = new Button { Text = Localized._Save };
                save.Clicked += async (_, _) =>
                {
                    try
                    {
                        editor.Save();
                        PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(panel, "StepEditorRefresh", null);
                        await page.HidePopup();
                    }
                    catch (Exception ex) { Log(ex, $"Save {Inner.TypeName} keyframe", this); }
                };
                buttons.Children.Add(cancel);
                buttons.Children.Add(save);
                content.Children.Add(buttons);
                await page.ShowAPopup(content, mode: "dialog");
            }
            catch (Exception ex) { Log(ex, $"Open {Inner.TypeName} keyframe editor", this); }
        });
    }
}
