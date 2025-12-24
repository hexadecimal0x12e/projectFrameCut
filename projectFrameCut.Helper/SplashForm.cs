using projectFrameCut.Helper;
using System.Diagnostics;

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
            VersionLabel.Text = HelperProgram.AppVersion;
            TitleLabel.Text = HelperProgram.AppTitle;
        }

        private void closeButton_Click(object sender, EventArgs e)
        {
            Process.GetCurrentProcess().Kill(); 
        }
    }
}
