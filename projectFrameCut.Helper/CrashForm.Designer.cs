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
            label1.Location = new Point(12, 9);
            label1.Margin = new Padding(18, 0, 18, 0);
            label1.Name = "label1";
            label1.Size = new Size(104, 112);
            label1.TabIndex = 0;
            label1.Text = ":(";
            // 
            // MessageLabel
            // 
            MessageLabel.Font = new Font("HarmonyOS Sans SC", 18F, FontStyle.Regular, GraphicsUnit.Point, 134);
            MessageLabel.Location = new Point(12, 121);
            MessageLabel.Name = "MessageLabel";
            MessageLabel.Size = new Size(1205, 91);
            MessageLabel.TabIndex = 1;
            MessageLabel.Text = "Sorry, the application has encountered an unhandled exception and needs to close now.";
            // 
            // LogBox
            // 
            LogBox.Location = new Point(12, 200);
            LogBox.Multiline = true;
            LogBox.Name = "LogBox";
            LogBox.ReadOnly = true;
            LogBox.ScrollBars = ScrollBars.Both;
            LogBox.Size = new Size(1225, 417);
            LogBox.TabIndex = 2;
            LogBox.UseSystemPasswordChar = true;
            // 
            // OpenLogButton
            // 
            OpenLogButton.Location = new Point(12, 623);
            OpenLogButton.Name = "OpenLogButton";
            OpenLogButton.Size = new Size(160, 34);
            OpenLogButton.TabIndex = 3;
            OpenLogButton.Text = "Open log";
            OpenLogButton.UseVisualStyleBackColor = true;
            OpenLogButton.Click += OpenLogButton_Click;
            // 
            // RestartButton
            // 
            RestartButton.AutoSize = true;
            RestartButton.Location = new Point(1035, 623);
            RestartButton.Name = "RestartButton";
            RestartButton.Size = new Size(211, 34);
            RestartButton.TabIndex = 4;
            RestartButton.Text = "Restart application";
            RestartButton.UseVisualStyleBackColor = true;
            RestartButton.Click += RestartButton_Click;
            // 
            // FeedbackButton
            // 
            FeedbackButton.Location = new Point(178, 623);
            FeedbackButton.Name = "FeedbackButton";
            FeedbackButton.Size = new Size(160, 34);
            FeedbackButton.TabIndex = 5;
            FeedbackButton.Text = "Feedback";
            FeedbackButton.UseVisualStyleBackColor = true;
            FeedbackButton.Click += FeedbackButton_Click;
            // 
            // CrashForm
            // 
            AcceptButton = RestartButton;
            AutoScaleDimensions = new SizeF(144F, 144F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1258, 664);
            Controls.Add(FeedbackButton);
            Controls.Add(RestartButton);
            Controls.Add(OpenLogButton);
            Controls.Add(LogBox);
            Controls.Add(MessageLabel);
            Controls.Add(label1);
            Icon = (Icon)resources.GetObject("$this.Icon");
            Margin = new Padding(4);
            MaximizeBox = false;
            MinimumSize = new Size(1280, 720);
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