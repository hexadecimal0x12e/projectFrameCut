using projectFrameCut.APIClient.Models;
using projectFrameCut.ApplicationAPIBase.PropertyPanelBuilders;
using projectFrameCut.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Setting.SettingPages
{
    public class RemoteFeedSettingPage : ContentPage
    {
        private PropertyPanelBuilder rootPPB;
        private RemoteServerService serverService;
        private ObservableCollection<RemoteServerItemView> serverItems;
        private VerticalStackLayout serverListContainer;
        private RemoteServer? currentEditingServer;

        public RemoteFeedSettingPage()
        {
            serverService = RemoteServerService.Instance;
            serverItems = new ObservableCollection<RemoteServerItemView>();
            Title = "远程服务器管理";
            BuildUI();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            RefreshServerList();
        }

        private void BuildUI()
        {
            rootPPB = new();

            // 添加标题和描述
            rootPPB.AddText("远程服务器管理", fontSize: 18, fontAttributes: FontAttributes.Bold);
            rootPPB.AddText("添加、编辑和管理远程服务器，支持在每个服务器上分别登录");

            // 添加分隔符
            rootPPB.AddSeparator();

            // 添加"添加新服务器"按钮
            var addServerBtn = new Button
            {
                Text = "➕ 添加新服务器",
                BackgroundColor = Colors.Green,
                TextColor = Colors.White,
                Padding = new Thickness(10, 8),
                Margin = new Thickness(0, 10, 0, 0)
            };
            addServerBtn.Clicked += async (s, e) => await ShowAddServerDialogAsync();
            rootPPB.AddCustomChild(addServerBtn);

            // 服务器列表容器
            serverListContainer = new VerticalStackLayout
            {
                Spacing = 8,
                Padding = new Thickness(0, 16, 0, 0)
            };

            rootPPB.AddCustomChild(serverListContainer);

            Content = rootPPB.BuildWithScrollView();
        }

        private void RefreshServerList()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                serverListContainer.Clear();
                serverItems.Clear();

                var servers = serverService.GetAllServers();
                if (servers.Count == 0)
                {
                    var noDataLabel = new Label
                    {
                        Text = "暂无服务器配置",
                        TextColor = Colors.Gray,
                        HorizontalTextAlignment = TextAlignment.Center,
                        Margin = new Thickness(0, 20, 0, 0)
                    };
                    serverListContainer.Add(noDataLabel);
                    return;
                }

                foreach (var server in servers)
                {
                    var serverView = CreateServerItemView(server);
                    serverListContainer.Add(serverView);
                    serverItems.Add(new RemoteServerItemView { Server = server });
                }
            });
        }

        private View CreateServerItemView(RemoteServer server)
        {
            var frame = new Frame
            {
                BorderColor = Colors.LightGray,
                CornerRadius = 8,
                Padding = new Thickness(12),
                HasShadow = true,
                Margin = new Thickness(0, 4)
            };

            var mainLayout = new VerticalStackLayout { Spacing = 8 };

            // 服务器头部（名称和状态）
            var headerLayout = new HorizontalStackLayout 
            { 
                Spacing = 8,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var nameLabel = new Label
            {
                Text = server.Name,
                FontSize = 16,
                FontAttributes = FontAttributes.Bold,
                HorizontalOptions = LayoutOptions.StartAndExpand
            };

            var statusLabel = new Label
            {
                Text = server.IsLoggedIn ? "✓ 已登录" : "未登录",
                TextColor = server.IsLoggedIn ? Colors.Green : Colors.Orange,
                FontSize = 12,
                FontAttributes = FontAttributes.Bold
            };

            headerLayout.Add(nameLabel);
            headerLayout.Add(statusLabel);
            mainLayout.Add(headerLayout);

            // 服务器URL
            var urlLabel = new Label
            {
                Text = $"地址: {server.Url}",
                FontSize = 12,
                TextColor = Colors.Gray
            };
            mainLayout.Add(urlLabel);

            // 用户信息（如果已登录）
            if (server.IsLoggedIn && server.LoggedInUser != null)
            {
                var userLabel = new Label
                {
                    Text = $"用户: {server.LoggedInUser.UserName} ({server.LoggedInUser.Email})",
                    FontSize = 12,
                    TextColor = Colors.DarkBlue
                };
                mainLayout.Add(userLabel);

                var loginTimeLabel = new Label
                {
                    Text = $"登录时间: {server.LastLoginAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""}",
                    FontSize = 10,
                    TextColor = Colors.Gray
                };
                mainLayout.Add(loginTimeLabel);
            }

            // 按钮行
            var buttonLayout = new HorizontalStackLayout
            {
                Spacing = 6,
                Margin = new Thickness(0, 8, 0, 0)
            };

            // 编辑按钮
            var editBtn = new Button
            {
                Text = "编辑",
                BackgroundColor = Colors.Blue,
                TextColor = Colors.White,
                Padding = new Thickness(16, 6),
                FontSize = 12,
                HorizontalOptions = LayoutOptions.StartAndExpand
            };
            editBtn.Clicked += async (s, e) => await ShowEditServerDialogAsync(server);
            buttonLayout.Add(editBtn);

            // 登录/登出按钮
            var authBtn = new Button
            {
                Text = server.IsLoggedIn ? "登出" : "登录",
                BackgroundColor = server.IsLoggedIn ? Colors.Red : Colors.Green,
                TextColor = Colors.White,
                Padding = new Thickness(16, 6),
                FontSize = 12,
                HorizontalOptions = LayoutOptions.StartAndExpand
            };
            authBtn.Clicked += async (s, e) => 
            {
                if (server.IsLoggedIn)
                {
                    await LogoutAsync(server);
                }
                else
                {
                    await ShowLoginDialogAsync(server);
                }
            };
            buttonLayout.Add(authBtn);

            // 删除按钮
            var deleteBtn = new Button
            {
                Text = "删除",
                BackgroundColor = Colors.DarkRed,
                TextColor = Colors.White,
                Padding = new Thickness(16, 6),
                FontSize = 12,
                HorizontalOptions = LayoutOptions.StartAndExpand
            };
            deleteBtn.Clicked += async (s, e) => await DeleteServerAsync(server);
            buttonLayout.Add(deleteBtn);

            mainLayout.Add(buttonLayout);

            frame.Content = mainLayout;
            return frame;
        }

        private async Task ShowAddServerDialogAsync()
        {
            currentEditingServer = new RemoteServer();
            await ShowServerConfigDialog("添加新服务器", currentEditingServer);
        }

        private async Task ShowEditServerDialogAsync(RemoteServer server)
        {
            currentEditingServer = server.Clone();
            await ShowServerConfigDialog("编辑服务器", currentEditingServer);
        }

        private async Task ShowServerConfigDialog(string title, RemoteServer server)
        {
            var pageBuilder = new PropertyPanelBuilder();

            string nameId = "serverName";
            string urlId = "serverUrl";
            string descId = "serverDesc";
            string enabledId = "serverEnabled";

            pageBuilder.AddText(title, fontSize: 16, fontAttributes: FontAttributes.Bold);
            pageBuilder.AddEntry(nameId, "服务器名称", 
                server.Name, "例如: 生产服务器", mode: EntryUpdateEventCallMode.OnUnfocused);
            pageBuilder.AddEntry(urlId, "服务器地址", 
                server.Url, "例如: https://api.example.com", mode: EntryUpdateEventCallMode.OnUnfocused);
            pageBuilder.AddEntry(descId, "服务器描述", 
                server.Description, "可选的描述信息", mode: EntryUpdateEventCallMode.OnUnfocused);
            pageBuilder.AddSwitch(enabledId, "是否启用", 
                server.IsEnabled);

            var stackLayout = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(16) };
            stackLayout.Add(pageBuilder.Build());

            // 按钮
            var buttonLayout = new HorizontalStackLayout { Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };

            var testBtn = new Button
            {
                Text = "测试连接",
                BackgroundColor = Colors.Orange,
                TextColor = Colors.White,
                HorizontalOptions = LayoutOptions.StartAndExpand
            };
            testBtn.Clicked += async (s, e) => await TestServerConnectionAsync(pageBuilder.Properties[urlId] as string);
            buttonLayout.Add(testBtn);

            var saveBtn = new Button
            {
                Text = "保存",
                BackgroundColor = Colors.Green,
                TextColor = Colors.White,
                HorizontalOptions = LayoutOptions.StartAndExpand
            };
            saveBtn.Clicked += async (s, e) =>
            {
                server.Name = pageBuilder.Properties[nameId] as string ?? "";
                server.Url = pageBuilder.Properties[urlId] as string ?? "";
                server.Description = pageBuilder.Properties[descId] as string ?? "";
                server.IsEnabled = (bool)(pageBuilder.Properties[enabledId] ?? false);
                server.LastUpdatedAt = DateTime.UtcNow;

                if (await serverService.SaveServerAsync(server))
                {
                    await Shell.Current.CurrentPage.DisplayAlert("成功", "服务器配置已保存", "确定");
                    RefreshServerList();
                    await Shell.Current.GoToAsync("..");
                }
                else
                {
                    await Shell.Current.CurrentPage.DisplayAlert("错误", "服务器名称和地址不能为空", "确定");
                }
            };
            buttonLayout.Add(saveBtn);

            stackLayout.Add(buttonLayout);

            var contentPage = new ContentPage
            {
                Title = title,
                Content = new ScrollView { Content = stackLayout }
            };

            await Shell.Current.Navigation.PushAsync(contentPage);
        }

        private async Task ShowLoginDialogAsync(RemoteServer server)
        {
            string? selectedLoginMethod = null;

            var action = await Shell.Current.CurrentPage.DisplayActionSheet(
                $"选择登录方式 - {server.Name}",
                "取消",
                null,
                "用户名/密码", "Google", "GitHub", "Microsoft");

            if (action == "取消" || action == null)
                return;

            switch (action)
            {
                case "用户名/密码":
                    await ShowUserPasswordLoginDialog(server);
                    break;
                case "Google":
                    await LoginWithOAuthAsync(server, "google");
                    break;
                case "GitHub":
                    await LoginWithOAuthAsync(server, "github");
                    break;
                case "Microsoft":
                    await LoginWithOAuthAsync(server, "microsoft");
                    break;
            }
        }

        private async Task ShowUserPasswordLoginDialog(RemoteServer server)
        {
            var pageBuilder = new PropertyPanelBuilder();

            string userNameId = "username";
            string passwordId = "password";

            pageBuilder.AddText($"登录到 {server.Name}", fontSize: 14, fontAttributes: FontAttributes.Bold);
            pageBuilder.AddEntry(userNameId, "用户名/邮箱", 
                "", "请输入用户名或邮箱", mode: EntryUpdateEventCallMode.OnUnfocused);
            
            var passwordEntry = new Entry
            {
                Placeholder = "请输入密码",
                IsPassword = true,
                HorizontalOptions = LayoutOptions.Fill
            };
            var passwordGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = 100 },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                }
            };
            passwordGrid.Add(new Label { Text = "密码", FontSize = 14, VerticalOptions = LayoutOptions.Center }, 0, 0);
            passwordGrid.Add(passwordEntry, 1, 0);
            pageBuilder.AddCustomChild(passwordGrid);

            var stackLayout = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(16) };
            stackLayout.Add(pageBuilder.Build());

            var buttonLayout = new HorizontalStackLayout { Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };

            var loginBtn = new Button
            {
                Text = "登录",
                BackgroundColor = Colors.Green,
                TextColor = Colors.White,
                HorizontalOptions = LayoutOptions.StartAndExpand
            };
            loginBtn.Clicked += async (s, e) =>
            {
                var userName = pageBuilder.Properties[userNameId] as string ?? "";
                var password = passwordEntry.Text ?? "";

                if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
                {
                    await Shell.Current.CurrentPage.DisplayAlert("错误", "用户名和密码不能为空", "确定");
                    return;
                }

                await LoginWithUserPasswordAsync(server, userName, password);
                await Shell.Current.Navigation.PopAsync();
            };
            buttonLayout.Add(loginBtn);

            stackLayout.Add(buttonLayout);

            var contentPage = new ContentPage
            {
                Title = $"登录 - {server.Name}",
                Content = new ScrollView { Content = stackLayout }
            };

            await Shell.Current.Navigation.PushAsync(contentPage);
        }

        private async Task LoginWithUserPasswordAsync(RemoteServer server, string userName, string password)
        {
            try
            {
                await Shell.Current.CurrentPage.DisplayAlert("提示", "正在登录...", "确定");

                var result = await serverService.LoginAsync(server.Id, userName, password);

                if (result.Success)
                {
                    await Shell.Current.CurrentPage.DisplayAlert("成功", 
                        $"已成功登录，用户: {result.User?.UserName}", "确定");
                    RefreshServerList();
                }
                else
                {
                    await Shell.Current.CurrentPage.DisplayAlert("登录失败", result.Message, "确定");
                }
            }
            catch (Exception ex)
            {
                await Shell.Current.CurrentPage.DisplayAlert("错误", $"登录异常: {ex.Message}", "确定");
            }
        }

        private async Task LoginWithOAuthAsync(RemoteServer server, string provider)
        {
            try
            {
                await Shell.Current.CurrentPage.DisplayAlert("提示", $"正在使用 {provider} 登录...", "确定");

                var result = await serverService.LoginWithOAuthAsync(server.Id, provider);

                if (result.Success)
                {
                    await Shell.Current.CurrentPage.DisplayAlert("成功", 
                        $"已成功登录，用户: {result.User?.UserName}", "确定");
                    RefreshServerList();
                }
                else
                {
                    await Shell.Current.CurrentPage.DisplayAlert("登录失败", result.Message, "确定");
                }
            }
            catch (Exception ex)
            {
                await Shell.Current.CurrentPage.DisplayAlert("错误", $"登录异常: {ex.Message}", "确定");
            }
        }

        private async Task LogoutAsync(RemoteServer server)
        {
            var confirmed = await Shell.Current.CurrentPage.DisplayAlert("确认", 
                $"确定要从 {server.Name} 登出吗？", "确定", "取消");

            if (!confirmed)
                return;

            try
            {
                await serverService.LogoutAsync(server.Id);
                await Shell.Current.CurrentPage.DisplayAlert("成功", "已登出", "确定");
                RefreshServerList();
            }
            catch (Exception ex)
            {
                await Shell.Current.CurrentPage.DisplayAlert("错误", $"登出异常: {ex.Message}", "确定");
            }
        }

        private async Task DeleteServerAsync(RemoteServer server)
        {
            var confirmed = await Shell.Current.CurrentPage.DisplayAlert("确认删除", 
                $"确定要删除服务器 '{server.Name}' 吗？此操作无法撤销。", "删除", "取消");

            if (!confirmed)
                return;

            try
            {
                await serverService.DeleteServerAsync(server.Id);
                await Shell.Current.CurrentPage.DisplayAlert("成功", "服务器已删除", "确定");
                RefreshServerList();
            }
            catch (Exception ex)
            {
                await Shell.Current.CurrentPage.DisplayAlert("错误", $"删除失败: {ex.Message}", "确定");
            }
        }

        private async Task TestServerConnectionAsync(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                await Shell.Current.CurrentPage.DisplayAlert("错误", "请先输入服务器地址", "确定");
                return;
            }

            try
            {
                var isConnected = await serverService.VerifyServerConnectionAsync(url);
                if (isConnected)
                {
                    await Shell.Current.CurrentPage.DisplayAlert("成功", "服务器连接正常", "确定");
                }
                else
                {
                    await Shell.Current.CurrentPage.DisplayAlert("失败", "无法连接到服务器", "确定");
                }
            }
            catch (Exception ex)
            {
                await Shell.Current.CurrentPage.DisplayAlert("错误", $"测试失败: {ex.Message}", "确定");
            }
        }
    }

    /// <summary>
    /// 服务器项视图模型（用于列表绑定）
    /// </summary>
    public class RemoteServerItemView
    {
        public RemoteServer Server { get; set; } = new();
    }
}
