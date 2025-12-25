namespace projectFrameCut.Helper
{
    partial class LogForm
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(LogForm));
            LogTextbox = new RichTextBox();
            SuspendLayout();
            // 
            // LogTextbox
            // 
            LogTextbox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            LogTextbox.Location = new Point(12, 12);
            LogTextbox.Name = "LogTextbox";
            LogTextbox.ReadOnly = true;
            LogTextbox.Size = new Size(870, 536);
            LogTextbox.TabIndex = 0;
            LogTextbox.Text = "";
            // 
            // LogForm
            // 
            AutoScaleDimensions = new SizeF(11F, 24F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(894, 560);
            Controls.Add(LogTextbox);
            Icon = (Icon)resources.GetObject("$this.Icon");
            Name = "LogForm";
            Text = "Logging @ projectFrameCut";
            Load += LogForm_Load;
            ResumeLayout(false);
        }

        #endregion

        private RichTextBox LogTextbox;
    }
}