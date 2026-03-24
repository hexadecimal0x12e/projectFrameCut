namespace projectFrameCut.Helper
{
    partial class FrozenForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
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
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(FrozenForm));
            TitleLabel = new Label();
            WaitButton = new Button();
            StopButton = new Button();
            SuspendLayout();
            // 
            // TitleLabel
            // 
            TitleLabel.AutoSize = true;
            TitleLabel.Location = new Point(21, 21);
            TitleLabel.Name = "TitleLabel";
            TitleLabel.Size = new Size(138, 20);
            TitleLabel.TabIndex = 0;
            TitleLabel.Text = "App not respond.";
            // 
            // WaitButton
            // 
            WaitButton.Location = new Point(12, 117);
            WaitButton.Name = "WaitButton";
            WaitButton.Size = new Size(525, 67);
            WaitButton.TabIndex = 1;
            WaitButton.Text = "Wait";
            WaitButton.UseVisualStyleBackColor = true;
            WaitButton.Click += WaitButton_Click;
            // 
            // StopButton
            // 
            StopButton.Location = new Point(12, 190);
            StopButton.Name = "StopButton";
            StopButton.Size = new Size(525, 67);
            StopButton.TabIndex = 2;
            StopButton.Text = "Reboot app";
            StopButton.UseVisualStyleBackColor = true;
            StopButton.Click += StopBotton_Click;
            // 
            // FrozenForm
            // 
            AutoScaleDimensions = new SizeF(9F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(549, 269);
            Controls.Add(StopButton);
            Controls.Add(WaitButton);
            Controls.Add(TitleLabel);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Icon = (Icon)resources.GetObject("$this.Icon");
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "FrozenForm";
            Text = "FrozenForm";
            Load += FrozenForm_Load;
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label TitleLabel;
        private Button WaitButton;
        private Button StopButton;
    }
}