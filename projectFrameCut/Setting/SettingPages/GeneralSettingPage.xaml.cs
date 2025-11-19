using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Maui.Storage;
using projectFrameCut.PropertyPanel;

namespace projectFrameCut.Setting.SettingPages;

public partial class GeneralSettingPage : ContentPage
{
    public PropertyPanel.PropertyPanelBuilder rootPPB;

    public GeneralSettingPage()
	{
		Title = Localized.MainSettingsPage_Tab_General;
		BuildPPB();
    }

	public void BuildPPB()
	{
		Content = new VerticalStackLayout();
        rootPPB = new();
        rootPPB
            .AddText(new PropertyPanel.TitleAndDescriptionLineLabel("Language and localization", "select the language for the software", 18, 0))
            .AddPicker("locate", "Language", ["zh-CN", "en-US"], "zh-CN", null)
            .AddSeparator()
            .AddText("UserData")
            .AddButton("userDataSelectButton", "Select UserData path", MauiProgram.DataPath)



            .ListenToChanges(SettingInvoker);
        Content = rootPPB.Build();
    }

    private async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
    {
		switch (args.Id)
		{
			case "userDataSelectButton":
				{
					try
					{
						// 使用文件选择器让用户选择一个文件，然后使用该文件的父目录作为“选中的文件夹”。
						var result = await FilePicker.Default.PickAsync();
						if (result == null)
						{
							// 用户取消了选择
							await DisplayAlert("取消", "未选择任何文件，操作已取消。", "确定");
						}
						else
						{
							string? fullPath = result.FullPath;
							string selectedFolder = string.Empty;

							if (!string.IsNullOrEmpty(fullPath))
							{
								selectedFolder = Path.GetDirectoryName(fullPath) ?? fullPath;
							}
							else
							{
								// 如果 FullPath 不可用，告知用户并记录空值（可以在未来扩展平台特定实现）
								await DisplayAlert("提示", "无法获取所选文件的完整路径，部分平台可能不支持直接获取文件路径。", "确定");
							}

							// 将目录写入设置（使用按钮 id 作为设置键，保持与现有代码一致）
							SettingsManager.WriteSetting(args.Id, selectedFolder);


							// 可选：向用户展示选择结果
							await DisplayAlert("已选择文件夹", string.IsNullOrEmpty(selectedFolder) ? "未能获取路径" : selectedFolder, "确定");
						}
					}
					catch (Exception ex)
					{
						// 处理异常并通知用户
						await DisplayAlert("错误", $"选择文件夹时发生错误：{ex.Message}", "确定");
					}

					break;
				}

        }

		SettingsManager.WriteSetting(args.Id, args.Value?.ToString() ?? "");

		BuildPPB();
    }
}