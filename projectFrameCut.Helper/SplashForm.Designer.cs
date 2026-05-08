namespace projectFrameCut.SplashScreen
{
    partial class SplashForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(SplashForm));
            TitleLabel = new Label();
            CopyrightLabel = new Label();
            closeButton = new Button();
            LicenseLabel = new Label();
            pluginStatLabel = new Label();
            VersionLabel = new Label();
            pictureBox1 = new PictureBox();
            ((System.ComponentModel.ISupportInitialize)pictureBox1).BeginInit();
            SuspendLayout();
            // 
            // TitleLabel
            // 
            resources.ApplyResources(TitleLabel, "TitleLabel");
            TitleLabel.BackColor = Color.Transparent;
            TitleLabel.Name = "TitleLabel";
            // 
            // CopyrightLabel
            // 
            resources.ApplyResources(CopyrightLabel, "CopyrightLabel");
            CopyrightLabel.BackColor = Color.Transparent;
            CopyrightLabel.Name = "CopyrightLabel";
            // 
            // closeButton
            // 
            closeButton.BackColor = Color.Transparent;
            resources.ApplyResources(closeButton, "closeButton");
            closeButton.Name = "closeButton";
            closeButton.UseVisualStyleBackColor = false;
            closeButton.Click += closeButton_Click;
            // 
            // LicenseLabel
            // 
            resources.ApplyResources(LicenseLabel, "LicenseLabel");
            LicenseLabel.Name = "LicenseLabel";
            // 
            // pluginStatLabel
            // 
            resources.ApplyResources(pluginStatLabel, "pluginStatLabel");
            pluginStatLabel.Name = "pluginStatLabel";
            // 
            // VersionLabel
            // 
            resources.ApplyResources(VersionLabel, "VersionLabel");
            VersionLabel.Name = "VersionLabel";
            // 
            // pictureBox1
            // 
            pictureBox1.BackColor = Color.Transparent;
            resources.ApplyResources(pictureBox1, "pictureBox1");
            pictureBox1.Name = "pictureBox1";
            pictureBox1.TabStop = false;
            // 
            // SplashForm
            // 
            resources.ApplyResources(this, "$this");
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.FromArgb(255, 210, 114);
            ControlBox = false;
            Controls.Add(pictureBox1);
            Controls.Add(VersionLabel);
            Controls.Add(pluginStatLabel);
            Controls.Add(LicenseLabel);
            Controls.Add(closeButton);
            Controls.Add(CopyrightLabel);
            Controls.Add(TitleLabel);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "SplashForm";
            ShowIcon = false;
            ShowInTaskbar = false;
            Load += Form1_Load;
            ((System.ComponentModel.ISupportInitialize)pictureBox1).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label TitleLabel;
        private Label CopyrightLabel;
        private Button closeButton;
        private Label LicenseLabel;
        public Label pluginStatLabel;
        private Label VersionLabel;
        private PictureBox pictureBox1;
    }
}
