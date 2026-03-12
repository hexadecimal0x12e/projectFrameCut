namespace projectFrameCut.Helper
{
    partial class CrashForm
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(CrashForm));
            label1 = new Label();
            MessageLabel = new Label();
            LogBox = new TextBox();
            OpenLogButton = new Button();
            RestartButton = new Button();
            FeedbackButton = new Button();
            SuspendLayout();
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Font = new Font("HarmonyOS Sans SC", 48F, FontStyle.Regular, GraphicsUnit.Point, 134);
            label1.Location = new Point(10, 8);
            label1.Margin = new Padding(15, 0, 15, 0);
            label1.Name = "label1";
            label1.Size = new Size(88, 94);
            label1.TabIndex = 0;
            label1.Text = ":(";
            // 
            // MessageLabel
            // 
            MessageLabel.Font = new Font("HarmonyOS Sans SC", 18F, FontStyle.Regular, GraphicsUnit.Point, 134);
            MessageLabel.Location = new Point(10, 101);
            MessageLabel.Margin = new Padding(2, 0, 2, 0);
            MessageLabel.Name = "MessageLabel";
            MessageLabel.Size = new Size(1004, 76);
            MessageLabel.TabIndex = 1;
            MessageLabel.Text = "Sorry, the application has encountered an unhandled exception and needs to close now.";
            // 
            // LogBox
            // 
            LogBox.Location = new Point(10, 167);
            LogBox.Margin = new Padding(2, 2, 2, 2);
            LogBox.Multiline = true;
            LogBox.Name = "LogBox";
            LogBox.ReadOnly = true;
            LogBox.ScrollBars = ScrollBars.Both;
            LogBox.Size = new Size(1022, 348);
            LogBox.TabIndex = 2;
            LogBox.UseSystemPasswordChar = true;
            // 
            // OpenLogButton
            // 
            OpenLogButton.Location = new Point(10, 519);
            OpenLogButton.Margin = new Padding(2, 2, 2, 2);
            OpenLogButton.Name = "OpenLogButton";
            OpenLogButton.Size = new Size(133, 28);
            OpenLogButton.TabIndex = 3;
            OpenLogButton.Text = "Open log";
            OpenLogButton.UseVisualStyleBackColor = true;
            OpenLogButton.Click += OpenLogButton_Click;
            // 
            // RestartButton
            // 
            RestartButton.AutoSize = true;
            RestartButton.Location = new Point(862, 519);
            RestartButton.Margin = new Padding(2, 2, 2, 2);
            RestartButton.Name = "RestartButton";
            RestartButton.Size = new Size(176, 30);
            RestartButton.TabIndex = 4;
            RestartButton.Text = "Restart application";
            RestartButton.UseVisualStyleBackColor = true;
            RestartButton.Click += RestartButton_Click;
            // 
            // FeedbackButton
            // 
            FeedbackButton.Location = new Point(148, 519);
            FeedbackButton.Margin = new Padding(2, 2, 2, 2);
            FeedbackButton.Name = "FeedbackButton";
            FeedbackButton.Size = new Size(133, 28);
            FeedbackButton.TabIndex = 5;
            FeedbackButton.Text = "Feedback";
            FeedbackButton.UseVisualStyleBackColor = true;
            FeedbackButton.Click += FeedbackButton_Click;
            // 
            // CrashForm
            // 
            AcceptButton = RestartButton;
            AutoScaleDimensions = new SizeF(120F, 120F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1052, 561);
            Controls.Add(FeedbackButton);
            Controls.Add(RestartButton);
            Controls.Add(OpenLogButton);
            Controls.Add(LogBox);
            Controls.Add(MessageLabel);
            Controls.Add(label1);
            Icon = (Icon)resources.GetObject("$this.Icon");
            MaximizeBox = false;
            MinimumSize = new Size(1070, 608);
            Name = "CrashForm";
            Text = "projectFrameCut Crash Report";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label label1;
        private Label MessageLabel;
        private TextBox LogBox;
        private Button OpenLogButton;
        private Button RestartButton;
        private Button FeedbackButton;
    }
}