namespace Magic_Mirror
{
    partial class MainForm
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            lblProfiles = new Label();
            btnCreateProfile = new Button();
            btnOpenProfile = new Button();
            btnStopProfile = new Button();
            btnRefresh = new Button();
            btnSettings = new Button();
            btnDeleteProfile = new Button();
            lvProfiles = new ListView();
            colProfile = new ColumnHeader();
            colStatus = new ColumnHeader();
            SuspendLayout();
            // 
            // lblProfiles
            // 
            lblProfiles.AutoSize = true;
            lblProfiles.Location = new Point(12, 42);
            lblProfiles.Name = "lblProfiles";
            lblProfiles.Size = new Size(67, 15);
            lblProfiles.TabIndex = 0;
            lblProfiles.Text = "Profiles List";
            // 
            // btnCreateProfile
            // 
            btnCreateProfile.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCreateProfile.Location = new Point(564, 70);
            btnCreateProfile.Name = "btnCreateProfile";
            btnCreateProfile.Size = new Size(149, 23);
            btnCreateProfile.TabIndex = 2;
            btnCreateProfile.Text = "Create Profile";
            btnCreateProfile.UseVisualStyleBackColor = true;
            btnCreateProfile.Click += btnCreateProfile_Click;
            // 
            // btnOpenProfile
            // 
            btnOpenProfile.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnOpenProfile.Location = new Point(564, 99);
            btnOpenProfile.Name = "btnOpenProfile";
            btnOpenProfile.Size = new Size(149, 23);
            btnOpenProfile.TabIndex = 3;
            btnOpenProfile.Text = "Launch / Reopen";
            btnOpenProfile.UseVisualStyleBackColor = true;
            btnOpenProfile.Click += btnOpenProfile_Click;
            // 
            // btnStopProfile
            // 
            btnStopProfile.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnStopProfile.Location = new Point(564, 128);
            btnStopProfile.Name = "btnStopProfile";
            btnStopProfile.Size = new Size(149, 23);
            btnStopProfile.TabIndex = 4;
            btnStopProfile.Text = "Stop Instance";
            btnStopProfile.UseVisualStyleBackColor = true;
            btnStopProfile.Click += btnStopProfile_Click;
            // 
            // btnRefresh
            // 
            btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRefresh.Location = new Point(564, 157);
            btnRefresh.Name = "btnRefresh";
            btnRefresh.Size = new Size(149, 23);
            btnRefresh.TabIndex = 5;
            btnRefresh.Text = "Refresh";
            btnRefresh.UseVisualStyleBackColor = true;
            btnRefresh.Click += btnRefresh_Click;
            // 
            // btnSettings
            // 
            btnSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSettings.Location = new Point(564, 351);
            btnSettings.Name = "btnSettings";
            btnSettings.Size = new Size(149, 23);
            btnSettings.TabIndex = 6;
            btnSettings.Text = "Settings";
            btnSettings.UseVisualStyleBackColor = true;
            btnSettings.Click += btnSettings_Click;
            // 
            // btnDeleteProfile
            // 
            btnDeleteProfile.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDeleteProfile.Location = new Point(564, 322);
            btnDeleteProfile.Name = "btnDeleteProfile";
            btnDeleteProfile.Size = new Size(149, 23);
            btnDeleteProfile.TabIndex = 7;
            btnDeleteProfile.Text = "Delete Profile";
            btnDeleteProfile.UseVisualStyleBackColor = true;
            btnDeleteProfile.Click += btnDeleteProfile_Click;
            // 
            // lvProfiles
            // 
            lvProfiles.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lvProfiles.Columns.AddRange(new ColumnHeader[] { colProfile, colStatus });
            lvProfiles.FullRowSelect = true;
            lvProfiles.GridLines = true;
            lvProfiles.Location = new Point(12, 60);
            lvProfiles.MultiSelect = false;
            lvProfiles.Name = "lvProfiles";
            lvProfiles.Size = new Size(546, 328);
            lvProfiles.TabIndex = 8;
            lvProfiles.UseCompatibleStateImageBehavior = false;
            lvProfiles.View = View.Details;
            lvProfiles.ColumnWidthChanging += lvProfiles_ColumnWidthChanging;
            lvProfiles.SelectedIndexChanged += lvProfiles_SelectedIndexChanged;
            // 
            // colProfile
            // 
            colProfile.Text = "Profile";
            colProfile.Width = 350;
            // 
            // colStatus
            // 
            colStatus.Text = "Status";
            colStatus.Width = 130;
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(744, 411);
            Controls.Add(lvProfiles);
            Controls.Add(btnDeleteProfile);
            Controls.Add(btnSettings);
            Controls.Add(btnRefresh);
            Controls.Add(btnStopProfile);
            Controls.Add(btnOpenProfile);
            Controls.Add(btnCreateProfile);
            Controls.Add(lblProfiles);
            Icon = (Icon)resources.GetObject("$this.Icon");
            MinimumSize = new Size(650, 400);
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Magic Mirror";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label lblProfiles;
        private Button btnCreateProfile;
        private Button btnOpenProfile;
        private Button btnStopProfile;
        private Button btnRefresh;
        private Button btnSettings;
        private Button btnDeleteProfile;
        private ListView lvProfiles;
        private ColumnHeader colProfile;
        private ColumnHeader colStatus;
    }
}
