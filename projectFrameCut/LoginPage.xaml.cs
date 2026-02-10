using projectFrameCut.APIClient;
using projectFrameCut.APIClient.Models;
using System;
using System.Threading.Tasks;

namespace projectFrameCut
{
    public partial class LoginPage : ContentPage
    {
        private bool _isLoginMode = true;

        public LoginPage()
        {
            InitializeComponent();
        }

        #region Tab切换
        private void OnLoginTabClicked(object sender, EventArgs e)
        {
            if (!_isLoginMode)
            {
                _isLoginMode = true;
                UpdateTabUI();
            }
        }

        private void OnRegisterTabClicked(object sender, EventArgs e)
        {
            if (_isLoginMode)
            {
                _isLoginMode = false;
                UpdateTabUI();
            }
        }

        private void UpdateTabUI()
        {
            if (_isLoginMode)
            {
                LoginFormLayout.IsVisible = true;
                RegisterFormLayout.IsVisible = false;
                LoginTabButton.BackgroundColor = Application.Current?.Resources["Primary"] as Color ?? Colors.Blue;
                LoginTabButton.TextColor = Colors.White;
                RegisterTabButton.BackgroundColor = Application.Current?.Resources["Gray200"] as Color ?? Colors.LightGray;
                RegisterTabButton.TextColor = Application.Current?.Resources["Gray900"] as Color ?? Colors.Black;
            }
            else
            {
                LoginFormLayout.IsVisible = false;
                RegisterFormLayout.IsVisible = true;
                RegisterTabButton.BackgroundColor = Application.Current?.Resources["Primary"] as Color ?? Colors.Blue;
                RegisterTabButton.TextColor = Colors.White;
                LoginTabButton.BackgroundColor = Application.Current?.Resources["Gray200"] as Color ?? Colors.LightGray;
                LoginTabButton.TextColor = Application.Current?.Resources["Gray900"] as Color ?? Colors.Black;
            }

            // 清空状态消息
            HideStatusMessage();
            
            // 重新验证按钮状态
            ValidateForm();
        }
        #endregion

        #region 表单验证
        private void OnEntryTextChanged(object sender, TextChangedEventArgs e)
        {
            ValidateForm();
        }

        private void ValidateForm()
        {
            if (_isLoginMode)
            {
                LoginButton.IsEnabled = !string.IsNullOrWhiteSpace(LoginUserNameEntry.Text) &&
                                       !string.IsNullOrWhiteSpace(LoginPasswordEntry.Text);
            }
            else
            {
                RegisterButton.IsEnabled = !string.IsNullOrWhiteSpace(RegisterUserNameEntry.Text) &&
                                          !string.IsNullOrWhiteSpace(RegisterEmailEntry.Text) &&
                                          !string.IsNullOrWhiteSpace(RegisterPasswordEntry.Text) &&
                                          !string.IsNullOrWhiteSpace(RegisterConfirmPasswordEntry.Text);
            }
        }
        #endregion

        #region 登录
        private async void OnLoginClicked(object sender, EventArgs e)
        {
            if (!LoginButton.IsEnabled) return;

            var userName = LoginUserNameEntry.Text?.Trim();
            var password = LoginPasswordEntry.Text;

            if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password))
            {
                ShowStatusMessage("请填写所有必填字段", isError: true);
                return;
            }

            await PerformLoginAsync(userName, password);
        }

        private async Task PerformLoginAsync(string userName, string password)
        {
            SetLoading(true);
            HideStatusMessage();

            try
            {
                var response = await AuthService.LoginAsync(userName, password);
                
                if (response != null)
                {
                    ShowStatusMessage($"登录成功！欢迎 {response.User.UserName}", isError: false);
                    
                    // 延迟后关闭登录页面
                    await Task.Delay(1000);
                    await Navigation.PopAsync();
                }
                else
                {
                    ShowStatusMessage("登录失败，请检查用户名和密码", isError: true);
                }
            }
            catch (UnauthorizedAccessException)
            {
                ShowStatusMessage("用户名/邮箱或密码错误", isError: true);
            }
            catch (Exception ex)
            {
                ShowStatusMessage($"登录失败: {ex.Message}", isError: true);
            }
            finally
            {
                SetLoading(false);
            }
        }
        #endregion

        #region 注册
        private async void OnRegisterClicked(object sender, EventArgs e)
        {
            if (!RegisterButton.IsEnabled) return;

            var userName = RegisterUserNameEntry.Text?.Trim();
            var email = RegisterEmailEntry.Text?.Trim();
            var password = RegisterPasswordEntry.Text;
            var confirmPassword = RegisterConfirmPasswordEntry.Text;

            // 验证输入
            if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(email) || 
                string.IsNullOrEmpty(password) || string.IsNullOrEmpty(confirmPassword))
            {
                ShowStatusMessage("请填写所有必填字段", isError: true);
                return;
            }

            if (password != confirmPassword)
            {
                ShowStatusMessage("两次输入的密码不一致", isError: true);
                return;
            }

            if (password.Length < 6)
            {
                ShowStatusMessage("密码长度至少为6位", isError: true);
                return;
            }

            // 简单的邮箱格式验证
            if (!email.Contains("@") || !email.Contains("."))
            {
                ShowStatusMessage("请输入有效的邮箱地址", isError: true);
                return;
            }

            await PerformRegisterAsync(userName, email, password);
        }

        private async Task PerformRegisterAsync(string userName, string email, string password)
        {
            SetLoading(true);
            HideStatusMessage();

            try
            {
                var response = await AuthService.RegisterAsync(userName, email, password);
                
                if (response != null)
                {
                    ShowStatusMessage("注册成功！正在自动登录...", isError: false);
                    
                    // 注册成功后自动登录
                    await Task.Delay(1000);
                    await PerformLoginAsync(userName, password);
                }
                else
                {
                    ShowStatusMessage("注册失败，用户名或邮箱可能已被使用", isError: true);
                }
            }
            catch (Exception ex)
            {
                ShowStatusMessage($"注册失败: {ex.Message}", isError: true);
            }
            finally
            {
                SetLoading(false);
            }
        }
        #endregion

        #region OAuth登录
        private async void OnGoogleLoginClicked(object sender, EventArgs e)
        {
            await PerformOAuthLoginAsync("Google", async () => await OAuthService.LoginWithGoogleAsync());
        }

        private async void OnGitHubLoginClicked(object sender, EventArgs e)
        {
            await PerformOAuthLoginAsync("GitHub", async () => await OAuthService.LoginWithGitHubAsync());
        }

        private async void OnMicrosoftLoginClicked(object sender, EventArgs e)
        {
            await PerformOAuthLoginAsync("Microsoft", async () => await OAuthService.LoginWithMicrosoftAsync());
        }

        private async Task PerformOAuthLoginAsync(string providerName, Func<Task<OAuthLoginResponse?>> loginFunc)
        {
            SetLoading(true);
            HideStatusMessage();

            try
            {
                var response = await loginFunc();
                
                if (response != null)
                {
                    var welcomeMessage = response.IsNewUser 
                        ? $"欢迎新用户 {response.User.UserName}！" 
                        : $"欢迎回来，{response.User.UserName}！";
                    
                    ShowStatusMessage(welcomeMessage, isError: false);
                    
                    // 延迟后关闭登录页面
                    await Task.Delay(1500);
                    await Navigation.PopAsync();
                }
                else
                {
                    // 用户取消了登录
                    ShowStatusMessage($"{providerName} 登录已取消", isError: false);
                }
            }
            catch (Exception ex)
            {
                ShowStatusMessage($"{providerName} 登录失败: {ex.Message}", isError: true);
            }
            finally
            {
                SetLoading(false);
            }
        }
        #endregion

        #region 跳过登录
        private async void OnSkipLoginClicked(object sender, EventArgs e)
        {
            var result = await DisplayAlertAsync("提示", "不登录将无法使用某些功能，确定要跳过吗？", "跳过", "取消");
            if (result)
            {
                await Navigation.PopAsync();
            }
        }
        #endregion

        #region UI辅助方法
        private void SetLoading(bool isLoading)
        {
            LoadingIndicator.IsRunning = isLoading;
            LoadingIndicator.IsVisible = isLoading;

            // 禁用所有按钮和输入框
            LoginButton.IsEnabled = !isLoading && !string.IsNullOrWhiteSpace(LoginUserNameEntry.Text) && !string.IsNullOrWhiteSpace(LoginPasswordEntry.Text);
            RegisterButton.IsEnabled = !isLoading && !string.IsNullOrWhiteSpace(RegisterUserNameEntry.Text);
            GoogleLoginButton.IsEnabled = !isLoading;
            GitHubLoginButton.IsEnabled = !isLoading;
            MicrosoftLoginButton.IsEnabled = !isLoading;
            SkipLoginButton.IsEnabled = !isLoading;
            
            LoginUserNameEntry.IsEnabled = !isLoading;
            LoginPasswordEntry.IsEnabled = !isLoading;
            RegisterUserNameEntry.IsEnabled = !isLoading;
            RegisterEmailEntry.IsEnabled = !isLoading;
            RegisterPasswordEntry.IsEnabled = !isLoading;
            RegisterConfirmPasswordEntry.IsEnabled = !isLoading;
        }

        private void ShowStatusMessage(string message, bool isError)
        {
            StatusLabel.Text = message;
            StatusLabel.TextColor = isError 
                ? Colors.Red 
                : Application.Current?.Resources["Primary"] as Color ?? Colors.Blue;
            StatusLabel.IsVisible = true;
        }

        private void HideStatusMessage()
        {
            StatusLabel.IsVisible = false;
        }
        #endregion
    }
}
