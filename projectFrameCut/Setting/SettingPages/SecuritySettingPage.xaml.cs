using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using projectFrameCut.Setting;
using static projectFrameCut.Setting.SettingManager.SettingsManager;

namespace projectFrameCut.Setting.SettingPages
{
    public class SecuritySettingPage : ContentPage
    {
        public SecuritySettingPage()
        {
            Title = Localized.MainSettingsPage_Tab_Security;
            BuildPPB();

        }
        public PropertyPanelBuilder? rootPPB;

        public void BuildPPB()
        {
            Content = new VerticalStackLayout();
            rootPPB = new();
            rootPPB
                //.AddText(new SingleLineLabel(SettingLocalizedResources.Security_General, 25))
                // not applicable for the oss branch

                .AddText(new SingleLineLabel(SettingLocalizedResources.Security_RemoteContent, 25))
                .AddCheckbox("Security_RemoteContent_EnableHttpDecoder", SettingLocalizedResources.Security_RemoteContent_EnableHttpDecoder, IsBoolSettingTrueOrDefault("Security_RemoteContent_EnableHttpDecoder", true))
                .AddCheckbox("Security_RemoteContent_EnableRemoteContent", SettingLocalizedResources.Security_RemoteContent_EnableRemoteContent, IsBoolSettingTrueOrDefault("Security_RemoteContent_EnableRemoteContent", true))
                .AddSeparator()

                .AddText(new SingleLineLabel(SettingLocalizedResources.Security_AICapabilities, 25))
                .AddCheckbox("Security_AICapabilities_AllowToolCall", SettingLocalizedResources.Security_AICapabilities_AllowToolCall, IsBoolSettingTrueOrDefault("Security_AICapabilities_AllowToolCall", true))
                .AddCheckbox("Security_AICapabilities_AllowModifyProject", SettingLocalizedResources.Security_AICapabilities_AllowModifyProject, IsBoolSettingTrueOrDefault("Security_AICapabilities_AllowModifyProject", true))
                .AddSeparator()

                .AddText(new SingleLineLabel(SettingLocalizedResources.Security_RichText, 25))
                .AddCheckbox("Security_RichText_EnableRendering", SettingLocalizedResources.Security_RichText_EnableRendering, IsBoolSettingTrueOrDefault("Security_RichText_EnableRendering", true))
                .AddCheckbox("Security_RichText_EnableDisplayingImage", SettingLocalizedResources.Security_RichText_EnableDisplayingImage, IsBoolSettingTrueOrDefault("Security_RichText_EnableDisplayingImage", true))
                .AddCheckbox("Security_RichText_EnableDisplayingHtml", SettingLocalizedResources.Security_RichText_EnableDisplayingHtml, IsBoolSettingTrueOrDefault("Security_RichText_EnableDisplayingHtml", true))
                .AddCheckbox("Security_RichText_EnableDisplayingXAML", SettingLocalizedResources.Security_RichText_EnableDisplayingXAML, IsBoolSettingTrueOrDefault("Security_RichText_EnableDisplayingXAML", true))
                .AddCheckbox("Security_RichText_EnableXAMLExternalSource", SettingLocalizedResources.Security_RichText_EnableXAMLExternalSource, IsBoolSettingTrueOrDefault("Security_RichText_EnableXAMLExternalSource", false));

            rootPPB.ListenToChanges(SettingInvoker);
            Content = rootPPB.BuildWithScrollView();
        }

        private async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
        {
            try
            {
                if (args.Value != null)
                {
                    WriteSetting(args.Id, args.Value?.ToString() ?? "");
                }

            }
            catch (Exception ex)
            {
                // 处理异常并通知用户
                await DisplayAlertAsync(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
            }
        }
    }
}
