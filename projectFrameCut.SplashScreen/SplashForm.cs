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
            VersionLabel.Text = SplashProgram.AppVersion;
            TitleLabel.Text = SplashProgram.AppTitle;
        }
    }
}
