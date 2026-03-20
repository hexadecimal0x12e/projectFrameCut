using projectFrameCut.Helper;
using System.Diagnostics;
using static SimpleLocalizerBaseGeneratedHelper;


namespace projectFrameCut.SplashScreen
{
    public partial class SplashForm : Form
    {
        public SplashForm()
        {
            InitializeComponent();
            this.StartPosition = FormStartPosition.CenterScreen;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            VersionLabel.Text = Localized.SplashForm_Version();
            VersionLabel.Left = this.ClientSize.Width - VersionLabel.Width - 15;
            TitleLabel.Text = HelperProgram.AppTitle;
            CopyrightLabel.Text = Localized.SplashForm_Copyright();
            CopyrightLabel.Left = this.ClientSize.Width - CopyrightLabel.Width - 15;
            LicenseLabel.Text = HelperProgram.AppChannel;
            pluginStatLabel.Text = Localized.AppLoading;
            

        }

        private void closeButton_Click(object sender, EventArgs e)
        {
            Process.GetCurrentProcess().Kill();
        }
    }
}
