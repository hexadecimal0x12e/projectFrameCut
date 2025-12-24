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
            VersionLabel = new Label();
            label3 = new Label();
            label1 = new Label();
            closeButton = new Button();
            SuspendLayout();
            // 
            // TitleLabel
            // 
            resources.ApplyResources(TitleLabel, "TitleLabel");
            TitleLabel.Name = "TitleLabel";
            // 
            // VersionLabel
            // 
            resources.ApplyResources(VersionLabel, "VersionLabel");
            VersionLabel.Name = "VersionLabel";
            // 
            // label3
            // 
            resources.ApplyResources(label3, "label3");
            label3.Name = "label3";
            // 
            // label1
            // 
            resources.ApplyResources(label1, "label1");
            label1.Name = "label1";
            // 
            // closeButton
            // 
            closeButton.BackColor = Color.Transparent;
            resources.ApplyResources(closeButton, "closeButton");
            closeButton.Name = "closeButton";
            closeButton.UseVisualStyleBackColor = true;
            closeButton.Click += closeButton_Click;
            // 
            // SplashForm
            // 
            resources.ApplyResources(this, "$this");
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.FromArgb(255, 210, 114);
            ControlBox = false;
            Controls.Add(closeButton);
            Controls.Add(label1);
            Controls.Add(label3);
            Controls.Add(VersionLabel);
            Controls.Add(TitleLabel);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "SplashForm";
            ShowIcon = false;
            ShowInTaskbar = false;
            Load += Form1_Load;
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label TitleLabel;
        private Label VersionLabel;
        private Label label3;
        private Label label1;
        private Button closeButton;
    }
}
