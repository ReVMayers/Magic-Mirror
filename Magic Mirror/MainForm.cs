using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Magic_Mirror
{
    public partial class MainForm : Form
    {
        private readonly string discordBasePath;
        private readonly string profileBasePath;
        private readonly InstanceManager instanceManager;

        private AppSettings settings;
        private bool isLaunchInProgress;
        private bool isReLaunchInProgress;
        private readonly NotifyIcon trayIcon = new();
        private readonly ContextMenuStrip trayMenu = new();
        private bool isExplicitExit;

        private static readonly HashSet<string> ReservedWindowsNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "CON",
                "PRN",
                "AUX",
                "NUL",

                "COM1", "COM2", "COM3", "COM4", "COM5",
                "COM6", "COM7", "COM8", "COM9",

                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
                "LPT6", "LPT7", "LPT8", "LPT9"
            };

        public MainForm()
        {
            InitializeComponent();

            settings = SettingsManager.Load();

            discordBasePath = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "Discord"
            );

            profileBasePath = Path.Combine(
                discordBasePath,
                "Profiles"
            );

            instanceManager =
                new InstanceManager(discordBasePath);
            InitializeTraySupport();
        }

        private void InitializeTraySupport()
        {
            var openItem =
                new ToolStripMenuItem(
                    "Open Magic Mirror"
                );

            var exitItem =
                new ToolStripMenuItem(
                    "Exit Magic Mirror"
                );

            openItem.Click +=
                (sender, e) =>
                {
                    RestoreFromTray();
                };

            exitItem.Click +=
                (sender, e) =>
                {
                    isExplicitExit = true;

                    trayIcon.Visible = false;

                    Application.Exit();
                };

            trayMenu.Items.Add(
                openItem
            );

            trayMenu.Items.Add(
                new ToolStripSeparator()
            );

            trayMenu.Items.Add(
                exitItem
            );

            trayIcon.Text =
                "Magic Mirror";

            trayIcon.Icon =
                this.Icon ??
                System.Drawing.SystemIcons.Application;

            trayIcon.ContextMenuStrip =
                trayMenu;

            trayIcon.DoubleClick +=
                (sender, e) =>
                {
                    RestoreFromTray();
                };

            UpdateTrayIconVisibility();
        }

        private void UpdateTrayIconVisibility()
        {
            trayIcon.Visible =
                settings.MinimizeToTrayOnClose;
        }

        private void RestoreFromTray()
        {
            // Re-check tracked Discord processes before showing
            // the window again.
            instanceManager.CleanupStaleProcesses();

            RefreshProfiles();

            if (!Visible)
            {
                Show();
            }

            if (WindowState ==
                FormWindowState.Minimized)
            {
                WindowState =
                    FormWindowState.Normal;
            }

            Activate();
            BringToFront();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!isExplicitExit &&
                settings.MinimizeToTrayOnClose &&
                e.CloseReason ==
                    CloseReason.UserClosing)
            {
                e.Cancel = true;

                Hide();
            }

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(
            FormClosedEventArgs e)
        {
            trayIcon.Visible = false;

            trayIcon.Dispose();
            trayMenu.Dispose();

            base.OnFormClosed(e);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            ApplyVoiceToInterface();

            WriteTrackingDiagnostics(
                "Before stale-process cleanup"
            );

            int removedProcesses =
                instanceManager.CleanupStaleProcesses();

            Debug.WriteLine(
                $"Stale process records removed: {removedProcesses}"
            );

            RefreshProfiles();

            WriteTrackingDiagnostics(
                "After stale-process cleanup"
            );
        }

        // =========================================================
        // Mirror voice / interface
        // =========================================================

        private string Speak(
            string normalMessage,
            string mirrorMessage)
        {
            return settings.UseMirrorVoice
                ? mirrorMessage
                : normalMessage;
        }

        private string SpeakTitle(
            string normalTitle,
            string mirrorTitle)
        {
            return settings.UseMirrorVoice
                ? mirrorTitle
                : normalTitle;
        }

        private void ApplyVoiceToInterface()
        {
            lblProfiles.Text = Speak(
                "Profiles List",
                "Known Reflections"
            );

            btnCreateProfile.Text = Speak(
                "Create Profile",
                "Establish a Reflection"
            );

            btnStopProfile.Text = Speak(
                "Stop Instance",
                "Dismiss Reflection"
            );

            btnRefresh.Text = Speak(
                "Refresh",
                "Gaze Again"
            );

            btnDeleteProfile.Text = Speak(
                "Delete Profile",
                "Shatter Reflection"
            );

            btnSettings.Text = Speak(
                "Settings",
                "Consult the Mirror"
            );

            colProfile.Text = Speak(
                "Profile",
                "Reflection"
            );

            colStatus.Text = Speak(
                "Status",
                "Presence"
            );

            UpdateSelectedProfileControls();
        }

        private enum ProfilePresenceState
        {
            Dormant,
            Present,
            Veiled
        }

        private ProfilePresenceState GetProfilePresenceState(string profileName)
        {
            IReadOnlyList<int> verifiedPids =
                instanceManager.GetVerifiedProcessIds(
                    profileName
                );

            if (verifiedPids.Count == 0)
            {
                return ProfilePresenceState.Dormant;
            }

            bool hasUsableWindow =
                WindowManager.HasUsableWindow(
                    verifiedPids
                );

            return hasUsableWindow
                ? ProfilePresenceState.Present
                : ProfilePresenceState.Veiled;
        }

        private void UpdateSelectedProfileControls()
        {
            string? profileName =
                GetSelectedProfileName();

            if (isLaunchInProgress)
            {
                btnOpenProfile.Text = isReLaunchInProgress
                    ? Speak(
                        "Re-Launching...",
                        "Reforming..."
                    )
                    : Speak(
                        "Launching...",
                        "Summoning..."
                    );

                btnOpenProfile.Enabled = false;
                btnStopProfile.Enabled = false;
                btnDeleteProfile.Enabled = false;

                return;
            }

            if (profileName == null)
            {
                btnOpenProfile.Text = Speak(
                    "Select a Profile",
                    "Choose a Reflection"
                );

                btnOpenProfile.Enabled = false;
                btnStopProfile.Enabled = false;
                btnDeleteProfile.Enabled = false;

                return;
            }

            ProfilePresenceState presence =
                GetProfilePresenceState(
                    profileName
                );

            btnDeleteProfile.Enabled =
                presence == ProfilePresenceState.Dormant;

            btnStopProfile.Enabled =
                presence !=
                ProfilePresenceState.Dormant;

            switch (presence)
            {
                case ProfilePresenceState.Dormant:
                    btnOpenProfile.Text = Speak(
                        "Launch",
                        "Summon"
                    );

                    btnOpenProfile.Enabled = true;
                    break;

                case ProfilePresenceState.Present:
                    btnOpenProfile.Text = Speak(
                        "Already Open",
                        "Already Present"
                    );

                    btnOpenProfile.Enabled = false;
                    break;

                case ProfilePresenceState.Veiled:
                    btnOpenProfile.Text = Speak(
                        "Re-Launch",
                        "Reform Reflection"
                    );

                    btnOpenProfile.Enabled = true;
                    break;
            }
        }

        // =========================================================
        // Profile list
        // =========================================================

        private string? GetSelectedProfileName()
        {
            if (lvProfiles.SelectedItems.Count == 0)
            {
                return null;
            }

            ListViewItem selectedItem =
                lvProfiles.SelectedItems[0];

            return selectedItem.Tag as string;
        }

        private void SelectProfile(
            string profileName)
        {
            foreach (ListViewItem item
                in lvProfiles.Items)
            {
                if (item.Tag
                    is not string itemProfileName)
                {
                    continue;
                }

                if (!string.Equals(
                        itemProfileName,
                        profileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                item.Selected = true;
                item.Focused = true;
                item.EnsureVisible();

                break;
            }
        }

        private void RefreshProfiles()
        {
            string? selectedProfileName =
                GetSelectedProfileName();

            lvProfiles.BeginUpdate();

            try
            {
                lvProfiles.Items.Clear();

                if (!Directory.Exists(
                        discordBasePath))
                {
                    MessageBox.Show(
                        "Discord could not be found in your Local AppData folder.",
                        "Discord Installation Not Found",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );

                    return;
                }

                if (!Directory.Exists(
                        profileBasePath))
                {
                    Directory.CreateDirectory(
                        profileBasePath
                    );
                }

                var profiles =
                    new DirectoryInfo(profileBasePath)
                        .EnumerateDirectories()
                        .Select(
                            directory =>
                                directory.Name
                        )
                        .OrderBy(
                            name => name,
                            StringComparer.OrdinalIgnoreCase
                        );

                foreach (string profileName
                    in profiles)
                {
                    ProfilePresenceState presence =
                        GetProfilePresenceState(
                            profileName
                        );

                    string statusText =
                        presence switch
                        {
                            ProfilePresenceState.Present =>
                                Speak(
                                    "● Running",
                                    "● Present"
                                ),

                            ProfilePresenceState.Veiled =>
                                Speak(
                                    "◐ Background",
                                    "◐ Veiled"
                                ),

                            _ =>
                                Speak(
                                    "○ Stopped",
                                    "○ Dormant"
                                )
                        };

                    var item =
                        new ListViewItem(
                            profileName
                        );

                    item.SubItems.Add(
                        statusText
                    );

                    // Keep the actual profile name
                    // separate from displayed text.
                    item.Tag = profileName;

                    lvProfiles.Items.Add(
                        item
                    );
                }

                if (selectedProfileName != null)
                {
                    SelectProfile(
                        selectedProfileName
                    );
                }
            }
            finally
            {
                lvProfiles.EndUpdate();
            }

            UpdateSelectedProfileControls();
        }

        // =========================================================
        // Diagnostics
        // =========================================================

        private void WriteTrackingDiagnostics(
            string heading)
        {
            Debug.WriteLine(
                $"===== {heading} ====="
            );

            foreach (TrackedInstance instance
                in instanceManager.Instances)
            {
                Debug.WriteLine(
                    $"Profile: {instance.ProfileName}"
                );

                foreach (TrackedProcess trackedProcess
                    in instance.Processes)
                {
                    ProcessVerificationStatus status =
                        instanceManager
                            .GetProcessVerificationStatus(
                                trackedProcess
                            );

                    Debug.WriteLine(
                        $"PID {trackedProcess.ProcessId}: {status}"
                    );
                }

                Debug.WriteLine(
                    $"Stored processes: {instance.Processes.Count}"
                );

                Debug.WriteLine(
                    $"Running: {instanceManager.IsInstanceRunning(instance.ProfileName)}"
                );

                Debug.WriteLine(
                    "--------------------------------------------"
                );
            }
        }

        // =========================================================
        // Discord process discovery / tracking
        // =========================================================

        private HashSet<int> GetDiscordProcessIds()
        {
            var processIds =
                new HashSet<int>();

            Process[] processes =
                Process.GetProcessesByName(
                    "Discord"
                );

            foreach (Process process
                in processes)
            {
                try
                {
                    processIds.Add(
                        process.Id
                    );
                }
                finally
                {
                    process.Dispose();
                }
            }

            return processIds;
        }

        private TrackedProcess?
            CaptureTrackedProcess(
                int processId)
        {
            try
            {
                using Process process =
                    Process.GetProcessById(
                        processId
                    );

                if (!string.Equals(
                        process.ProcessName,
                        "Discord",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                DateTime startTimeUtc =
                    process
                        .StartTime
                        .ToUniversalTime();

                string executablePath =
                    process.MainModule?.FileName
                    ?? string.Empty;

                return new TrackedProcess
                {
                    ProcessId =
                        process.Id,

                    StartTimeUtc =
                        startTimeUtc,

                    ExecutablePath =
                        executablePath
                };
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (Win32Exception)
            {
                return null;
            }
        }

        private async Task<TrackedInstance?>
            TrackNewDiscordProcessesAsync(
                string profileName,
                HashSet<int> processesBeforeLaunch,
                DateTime trackingStartedUtc)
        {
            TimeSpan maximumWait =
                TimeSpan.FromSeconds(15);

            TimeSpan settlingTime =
                TimeSpan.FromSeconds(2);

            TimeSpan pollInterval =
                TimeSpan.FromMilliseconds(500);

            var discoveredProcessIds =
                new HashSet<int>();

            var timer =
                Stopwatch.StartNew();

            TimeSpan? lastNewProcessTime =
                null;

            while (timer.Elapsed
                < maximumWait)
            {
                HashSet<int> currentProcesses =
                    GetDiscordProcessIds();

                bool foundSomethingNew =
                    false;

                foreach (int processId
                    in currentProcesses)
                {
                    if (processesBeforeLaunch
                        .Contains(processId))
                    {
                        continue;
                    }

                    if (discoveredProcessIds
                        .Add(processId))
                    {
                        foundSomethingNew =
                            true;
                    }
                }

                if (foundSomethingNew)
                {
                    lastNewProcessTime =
                        timer.Elapsed;
                }

                if (discoveredProcessIds.Count > 0 &&
                    lastNewProcessTime.HasValue &&
                    timer.Elapsed -
                        lastNewProcessTime.Value
                        >= settlingTime)
                {
                    break;
                }

                await Task.Delay(
                    pollInterval
                );
            }

            timer.Stop();

            var trackedProcesses =
                new List<TrackedProcess>();

            foreach (int processId
                in discoveredProcessIds)
            {
                TrackedProcess?
                    trackedProcess =
                        CaptureTrackedProcess(
                            processId
                        );

                if (trackedProcess != null)
                {
                    trackedProcesses.Add(
                        trackedProcess
                    );
                }
            }

            if (trackedProcesses.Count == 0)
            {
                return null;
            }

            return new TrackedInstance
            {
                ProfileName =
                    profileName,

                TrackingStartedUtc =
                    trackingStartedUtc,

                Processes =
                    trackedProcesses
            };
        }

        // =========================================================
        // Launch
        // =========================================================

        private async Task LaunchProfileAsync(
            string profileName)
        {
            string profilePath =
                Path.Combine(
                    profileBasePath,
                    profileName
                );

            string updateExe =
                Path.Combine(
                    discordBasePath,
                    "Update.exe"
                );

            if (!Directory.Exists(
                    profilePath))
            {
                MessageBox.Show(
                    Speak(
                        $"The profile \"{profileName}\" could not be found.",
                        $"The reflection \"{profileName}\" has vanished from the glass."
                    ),
                    SpeakTitle(
                        "Profile Not Found",
                        "A Reflection Is Missing"
                    ),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );

                RefreshProfiles();

                return;
            }

            if (!File.Exists(
                    updateExe))
            {
                MessageBox.Show(
                    $"Discord's Update.exe could not be found.\n\nExpected location:\n{updateExe}",
                    "Discord Launcher Not Found",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );

                return;
            }

            try
            {
                // Remember every Discord process
                // that existed before this launch.
                HashSet<int>
                    processesBeforeLaunch =
                        GetDiscordProcessIds();

                DateTime trackingStartedUtc =
                    DateTime.UtcNow;

                var startInfo =
                    new ProcessStartInfo
                    {
                        FileName =
                            updateExe,

                        Arguments =
                            "--processStart Discord.exe --process-start-args \"--multi-instance\"",

                        UseShellExecute =
                            false,

                        WorkingDirectory =
                            discordBasePath
                    };

                startInfo.Environment[
                    "DISCORD_USER_DATA_DIR"
                ] = profilePath;

                using Process? launcher =
                    Process.Start(
                        startInfo
                    );

                TrackedInstance?
                    trackedInstance =
                        await TrackNewDiscordProcessesAsync(
                            profileName,
                            processesBeforeLaunch,
                            trackingStartedUtc
                        );

                if (trackedInstance == null)
                {
                    MessageBox.Show(
                        Speak(
                            "Discord was launched, but Magic Mirror could not identify the new instance.",
                            "The glass stirred, yet the mirror could not bind what answered."
                        ),
                        SpeakTitle(
                            "Instance Tracking Failed",
                            "The Reflection Eludes the Mirror"
                        ),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );

                    return;
                }

                instanceManager.SetInstance(
                    trackedInstance
                );

                RefreshProfiles();

                WriteTrackingDiagnostics(
                    "After tracking new instance"
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Magic Mirror could not launch or track Discord.\n\n{ex.Message}",
                    "Discord Launch Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        private async Task ReLaunchProfileAsync(
    string profileName)
        {
            // Re-check immediately before doing anything destructive.
            instanceManager.CleanupStaleProcesses();

            ProfilePresenceState presence =
                GetProfilePresenceState(
                    profileName
                );

            // It may have stopped on its own since the user
            // last refreshed the interface.
            if (presence == ProfilePresenceState.Dormant)
            {
                await LaunchProfileAsync(
                    profileName
                );

                return;
            }

            // If a usable window somehow came back, there is
            // no reason to kill and restart the instance.
            if (presence == ProfilePresenceState.Present)
            {
                RefreshProfiles();
                return;
            }

            // At this point it is still genuinely Veiled.
            StopInstanceResult stopResult =
                await instanceManager.StopInstanceAsync(
                    profileName
                );

            bool safeToLaunch =
                stopResult.Success
                ||
                (
                    !stopResult.WasRunning &&
                    stopResult.RemainingVerifiedPids.Count == 0 &&
                    stopResult.UncertainProcessCount == 0
                );

            if (!safeToLaunch)
            {
                string remaining =
                    stopResult.RemainingVerifiedPids.Count > 0
                        ? string.Join(
                            ", ",
                            stopResult.RemainingVerifiedPids
                        )
                        : "None";

                MessageBox.Show(
                    $"Magic Mirror could not safely stop the old Discord instance.\n\n" +
                    $"Verified processes still running: {remaining}\n" +
                    $"Processes that could not be conclusively inspected: {stopResult.UncertainProcessCount}\n\n" +
                    $"The profile was not re-launched to avoid creating a duplicate instance.",
                    "Discord Re-Launch Aborted",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );

                RefreshProfiles();
                return;
            }

            // The old instance is now definitely gone.
            // Launch normally and establish a fresh tracking record.
            await LaunchProfileAsync(
                profileName
            );
        }

        // =========================================================
        // Profile creation
        // =========================================================

        private string? ValidateProfileName(
            string profileName)
        {
            if (string.IsNullOrWhiteSpace(
                    profileName))
            {
                return Speak(
                    "Profile name cannot be empty.",
                    "The mirror hears no name. Speak one, if you wish to be remembered."
                );
            }

            if (!string.Equals(
                    profileName,
                    profileName.Trim(),
                    StringComparison.Ordinal))
            {
                return Speak(
                    "Profile names cannot begin or end with spaces.",
                    "The name is veiled by empty space. Strip away what does not belong."
                );
            }

            if (profileName == "." ||
                profileName == "..")
            {
                return Speak(
                    "That profile name is not valid.",
                    "That name points somewhere it should not. The mirror refuses it."
                );
            }

            if (profileName.EndsWith(
                    ".",
                    StringComparison.Ordinal))
            {
                return Speak(
                    "Profile names cannot end with a period.",
                    "A name may not fade into a final dot. Choose another."
                );
            }

            if (profileName.IndexOfAny(
                    Path.GetInvalidFileNameChars())
                >= 0)
            {
                return Speak(
                    "That profile name contains characters Windows does not allow in folder names.",
                    "Forbidden marks stain that name. The mirror cannot carve it into this realm."
                );
            }

            string reservedPart =
                profileName.Split('.')[0];

            if (ReservedWindowsNames
                .Contains(reservedPart))
            {
                return Speak(
                    $"\"{reservedPart}\" is a reserved Windows name and cannot be used.",
                    $"\"{reservedPart}\" is an ancient name claimed by Windows itself. The mirror will not challenge it."
                );
            }

            if (profileName.Length > 80)
            {
                return Speak(
                    "Profile names cannot be longer than 80 characters.",
                    "That name stretches too far across the glass. Choose something shorter."
                );
            }

            return null;
        }

        private string?
            PromptForProfileName()
        {
            using var dialog =
                new Form
                {
                    Text = SpeakTitle(
                        "Create Profile",
                        "Name a Reflection"
                    ),

                    Width = 400,
                    Height = 165,

                    FormBorderStyle =
                        FormBorderStyle.FixedDialog,

                    StartPosition =
                        FormStartPosition.CenterParent,

                    MaximizeBox = false,
                    MinimizeBox = false,
                    ShowInTaskbar = false
                };

            var label =
                new Label
                {
                    Text = Speak(
                        "Profile name:",
                        "Speak the name of the reflection:"
                    ),

                    Left = 15,
                    Top = 15,
                    AutoSize = true
                };

            var textBox =
                new TextBox
                {
                    Left = 15,
                    Top = 42,
                    Width = 350
                };

            var createButton =
                new Button
                {
                    Text = Speak(
                        "Create",
                        "Bind"
                    ),

                    Left = 210,
                    Top = 78,
                    Width = 75
                };

            var cancelButton =
                new Button
                {
                    Text = Speak(
                        "Cancel",
                        "Turn Away"
                    ),

                    Left = 290,
                    Top = 78,
                    Width = 75,

                    DialogResult =
                        DialogResult.Cancel
                };

            dialog.Controls.Add(
                label
            );

            dialog.Controls.Add(
                textBox
            );

            dialog.Controls.Add(
                createButton
            );

            dialog.Controls.Add(
                cancelButton
            );

            dialog.AcceptButton =
                createButton;

            dialog.CancelButton =
                cancelButton;

            createButton.Click +=
                (sender, e) =>
                {
                    string profileName =
                        textBox.Text;

                    string? validationMessage =
                        ValidateProfileName(
                            profileName
                        );

                    if (validationMessage != null)
                    {
                        MessageBox.Show(
                            dialog,
                            validationMessage,
                            SpeakTitle(
                                "Invalid Profile Name",
                                "The Mirror Refuses"
                            ),
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning
                        );

                        textBox.Focus();
                        textBox.SelectAll();

                        return;
                    }

                    string profilePath =
                        Path.Combine(
                            profileBasePath,
                            profileName
                        );

                    if (Directory.Exists(profilePath))
                    {
                        MessageBox.Show(
                            dialog,
                            Speak(
                                $"A profile named \"{profileName}\" already exists.",
                                $"The name \"{profileName}\" is already reflected within the glass."
                            ),
                            SpeakTitle(
                                "Profile Already Exists",
                                "A Reflection Already Exists"
                            ),
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information
                        );

                        textBox.Focus();
                        textBox.SelectAll();

                        return;
                    }

                    // Easter egg: Just Monika.
                    if (string.Equals(
                            profileName,
                            "Just Monika",
                            StringComparison.Ordinal))
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            MessageBox.Show(
                                dialog,
                                "Just Monika",
                                "",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.None
                            );
                        }
                    }

                    // Everything is valid. Only now may the dialog close.
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                };

            if (dialog.ShowDialog(this)
                == DialogResult.OK)
            {
                return textBox.Text;
            }

            return null;
        }

        // =========================================================
        // Delete / Shatter profile
        // =========================================================

        private void DeleteProfile(
    string profileName)
        {
            string profilePath =
                Path.Combine(
                    profileBasePath,
                    profileName
                );

            if (!Directory.Exists(profilePath))
            {
                MessageBox.Show(
                    Speak(
                        $"The profile \"{profileName}\" no longer exists.",
                        $"The reflection \"{profileName}\" has already vanished from the mirror."
                    ),
                    SpeakTitle(
                        "Profile Not Found",
                        "Nothing Remains to Shatter"
                    ),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                RefreshProfiles();
                return;
            }

            // Safety check:
            // make sure the target is an immediate child of
            // Discord\Profiles and not some unexpected path.
            string normalizedProfileBase =
                Path.GetFullPath(profileBasePath)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    );

            string normalizedProfilePath =
                Path.GetFullPath(profilePath);

            DirectoryInfo targetDirectory =
                new DirectoryInfo(
                    normalizedProfilePath
                );

            string? parentPath =
                targetDirectory.Parent?.FullName
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    );

            if (!string.Equals(
                    parentPath,
                    normalizedProfileBase,
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    $"Magic Mirror refused to delete the profile because its path failed a safety check.\n\n" +
                    $"Target:\n{normalizedProfilePath}",
                    "Unsafe Profile Path",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );

                return;
            }

            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    normalizedProfilePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException
                );

                // The profile is gone, so any old tracking record
                // is meaningless now.
                instanceManager.RemoveInstance(
                    profileName
                );

                RefreshProfiles();
            }
            catch (OperationCanceledException)
            {
                // The Windows operation was cancelled.
                // Nothing else to do.
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Magic Mirror could not delete the profile.\n\n{ex.Message}",
                    "Profile Deletion Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        // =========================================================
        // Settings
        // =========================================================

        private void ShowSettingsDialog()
        {
            using var dialog =
                new Form
                {
                    Text = "Magic Mirror Settings",

                    Width = 410,
                    Height = 285,

                    FormBorderStyle =
                        FormBorderStyle.FixedDialog,

                    StartPosition =
                        FormStartPosition.CenterParent,

                    MaximizeBox = false,
                    MinimizeBox = false,
                    ShowInTaskbar = false
                };

            // =========================================================
            // Mirror voice
            // =========================================================

            var mirrorVoiceCheckBox =
                new CheckBox
                {
                    Text = "Let the Mirror speak",

                    Left = 20,
                    Top = 20,
                    Width = 330,

                    Checked =
                        settings.UseMirrorVoice
                };

            var mirrorVoiceDescription =
                new Label
                {
                    Text =
                        "Use ominous dialogue instead of ordinary application messages.",

                    Left = 40,
                    Top = 47,
                    Width = 330,
                    Height = 35
                };

            // =========================================================
            // Tray behavior
            // =========================================================

            var minimizeToTrayCheckBox =
                new CheckBox
                {
                    Text =
                        "Minimize to traybar on close",

                    Left = 20,
                    Top = 90,
                    Width = 330,

                    Checked =
                        settings.MinimizeToTrayOnClose
                };

            // =========================================================
            // Utility / Easter egg buttons
            // =========================================================

            var openProfilesFolderButton =
                new Button
                {
                    Text = Speak(
                        "Open Profiles Folder",
                        "Peer Behind the Glass"
                    ),

                    Left = 20,
                    Top = 130,
                    Width = 170,
                    Height = 30
                };

            var donateButton =
                new Button
                {
                    Text = "Donate",

                    Left = 205,
                    Top = 130,
                    Width = 170,
                    Height = 30
                };

            // =========================================================
            // Save / Cancel
            // =========================================================

            var saveButton =
                new Button
                {
                    Text = "Save",

                    Left = 220,
                    Top = 200,
                    Width = 75,

                    DialogResult =
                        DialogResult.OK
                };

            var cancelButton =
                new Button
                {
                    Text = "Cancel",

                    Left = 300,
                    Top = 200,
                    Width = 75,

                    DialogResult =
                        DialogResult.Cancel
                };

            // =========================================================
            // Open Profiles Folder
            // =========================================================

            openProfilesFolderButton.Click +=
                (sender, e) =>
                {
                    try
                    {
                        if (!Directory.Exists(
                                profileBasePath))
                        {
                            Directory.CreateDirectory(
                                profileBasePath
                            );
                        }

                        Process.Start(
                            new ProcessStartInfo
                            {
                                FileName =
                                    profileBasePath,

                                UseShellExecute =
                                    true
                            }
                        );
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            dialog,
                            $"Magic Mirror could not open the Profiles folder.\n\n{ex.Message}",
                            "Could Not Open Profiles Folder",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                    }
                };

            // =========================================================
            // Donate Easter Egg
            // =========================================================

            donateButton.Click +=
                (sender, e) =>
                {
                    MessageBox.Show(
                        dialog,

                        "I appreaciate your concern but this software, or rather app as the kids call it these days, was developed by sheer stubborness, lemon beer, tea, and mainly from ChatGPT with me just copy and pasting code and testing it to the best of my abilities.",

                        "Donate",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                };

            // =========================================================
            // Add controls
            // =========================================================

            dialog.Controls.Add(
                mirrorVoiceCheckBox
            );

            dialog.Controls.Add(
                mirrorVoiceDescription
            );

            dialog.Controls.Add(
                minimizeToTrayCheckBox
            );

            dialog.Controls.Add(
                openProfilesFolderButton
            );

            dialog.Controls.Add(
                donateButton
            );

            dialog.Controls.Add(
                saveButton
            );

            dialog.Controls.Add(
                cancelButton
            );

            dialog.AcceptButton =
                saveButton;

            dialog.CancelButton =
                cancelButton;

            // =========================================================
            // Show dialog
            // =========================================================

            if (dialog.ShowDialog(this)
                != DialogResult.OK)
            {
                return;
            }

            // =========================================================
            // Apply settings
            // =========================================================

            settings.UseMirrorVoice =
                mirrorVoiceCheckBox.Checked;

            settings.MinimizeToTrayOnClose =
                minimizeToTrayCheckBox.Checked;

            try
            {
                SettingsManager.Save(
                    settings
                );

                // Apply everything immediately.
                UpdateTrayIconVisibility();
                ApplyVoiceToInterface();
                RefreshProfiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Magic Mirror could not save its settings.\n\n{ex.Message}",
                    "Settings Save Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        // =========================================================
        // Button / ListView events
        // =========================================================

        private void btnRefresh_Click(
            object sender,
            EventArgs e)
        {
            RefreshProfiles();
        }

        private void btnCreateProfile_Click(
            object sender,
            EventArgs e)
        {
            string? profileName =
                PromptForProfileName();

            if (profileName == null)
            {
                return;
            }

            string profilePath =
                Path.Combine(
                    profileBasePath,
                    profileName
                );

            try
            {
                Directory.CreateDirectory(
                    profilePath
                );

                RefreshProfiles();

                SelectProfile(
                    profileName
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Magic Mirror could not create the profile.\n\n{ex.Message}",
                    "Profile Creation Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        private void btnSettings_Click(
            object sender,
            EventArgs e)
        {
            ShowSettingsDialog();
        }

        private async void btnOpenProfile_Click(
    object sender,
    EventArgs e)
        {
            if (isLaunchInProgress)
            {
                return;
            }

            string? profileName =
                GetSelectedProfileName();

            if (profileName == null)
            {
                return;
            }

            // Don't trust the displayed status alone.
            // Check the actual state again when clicked.
            ProfilePresenceState presence =
                GetProfilePresenceState(
                    profileName
                );

            if (presence == ProfilePresenceState.Present)
            {
                RefreshProfiles();
                return;
            }

            if (presence == ProfilePresenceState.Veiled)
            {
                DialogResult confirmation =
                    MessageBox.Show(
                        Speak(
                            $"The Discord instance for \"{profileName}\" is still running, but its window is gone.\n\n" +
                            $"Re-launching will terminate that background instance and start the same profile again.",

                            $"The reflection \"{profileName}\" remains within the glass, yet its visage is lost.\n\n" +
                            $"To reform it, the mirror must first dismiss what remains and summon the reflection anew."
                        ),
                        SpeakTitle(
                            "Re-Launch Discord Instance?",
                            "Reform This Reflection?"
                        ),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2
                    );

                if (confirmation != DialogResult.Yes)
                {
                    return;
                }
            }

            isLaunchInProgress = true;

            isReLaunchInProgress =
                presence == ProfilePresenceState.Veiled;

            UpdateSelectedProfileControls();

            try
            {
                if (presence == ProfilePresenceState.Veiled)
                {
                    await ReLaunchProfileAsync(
                        profileName
                    );
                }
                else
                {
                    await LaunchProfileAsync(
                        profileName
                    );
                }
            }
            finally
            {
                isReLaunchInProgress = false;
                isLaunchInProgress = false;

                RefreshProfiles();
                UpdateSelectedProfileControls();
            }
        }

        private void lvProfiles_ColumnWidthChanging(
            object sender,
            ColumnWidthChangingEventArgs e)
        {
            int minimumWidth =
                e.ColumnIndex switch
                {
                    0 => 120,
                    1 => 90,
                    _ => 50
                };

            if (e.NewWidth < minimumWidth)
            {
                e.Cancel = true;
                e.NewWidth = minimumWidth;
            }
        }

        private void lvProfiles_SelectedIndexChanged(
            object sender,
            EventArgs e)
        {
            UpdateSelectedProfileControls();
        }

        // =========================================================
        // Stop / Dismiss instance
        // =========================================================

        private async void btnStopProfile_Click(
            object sender,
            EventArgs e)
        {
            string? profileName =
                GetSelectedProfileName();

            if (profileName == null)
            {
                MessageBox.Show(
                    Speak(
                        "Select a profile first.",
                        "Choose a reflection before asking the mirror to dismiss it."
                    ),
                    SpeakTitle(
                        "No Profile Selected",
                        "The Mirror Sees Nothing"
                    ),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                return;
            }

            // First verification before asking
            // the user for confirmation.
            instanceManager
                .CleanupStaleProcesses();

            IReadOnlyList<int> verifiedPids =
                instanceManager
                    .GetVerifiedProcessIds(
                        profileName
                    );

            if (verifiedPids.Count == 0)
            {
                RefreshProfiles();

                MessageBox.Show(
                    Speak(
                        $"The profile \"{profileName}\" is not currently running.",
                        $"The reflection \"{profileName}\" already lies dormant."
                    ),
                    SpeakTitle(
                        "Instance Not Running",
                        "The Reflection Sleeps"
                    ),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                return;
            }

            DialogResult confirmation =
                MessageBox.Show(
                    Speak(
                        $"Stop the Discord instance for \"{profileName}\"?\n\n" +
                        $"Magic Mirror has verified {verifiedPids.Count} Discord process(es) belonging to this tracked instance.",

                        $"Dismiss the reflection \"{profileName}\"?\n\n" +
                        $"The mirror has bound {verifiedPids.Count} living Discord process(es) to this reflection. " +
                        $"Their presence will be severed, though the reflection itself shall remain."
                    ),
                    SpeakTitle(
                        "Stop Discord Instance?",
                        "Dismiss This Reflection?"
                    ),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2
                );

            if (confirmation
                != DialogResult.Yes)
            {
                return;
            }

            btnStopProfile.Enabled =
                false;

            btnOpenProfile.Enabled =
                false;

            try
            {
                StopInstanceResult result =
                    await instanceManager
                        .StopInstanceAsync(
                            profileName
                        );

                RefreshProfiles();

                if (!result.WasRunning)
                {
                    MessageBox.Show(
                        Speak(
                            "The instance stopped before Magic Mirror could terminate it.",
                            "The reflection vanished before the mirror could dismiss it."
                        ),
                        SpeakTitle(
                            "Instance Already Stopped",
                            "The Reflection Faded"
                        ),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );

                    return;
                }

                if (!result.Success)
                {
                    string remaining =
                        result
                            .RemainingVerifiedPids
                            .Count > 0
                                ? string.Join(
                                    ", ",
                                    result.RemainingVerifiedPids
                                )
                                : "None";

                    MessageBox.Show(
                        $"Magic Mirror could not confirm that the entire Discord instance stopped.\n\n" +
                        $"Verified processes still running: {remaining}\n" +
                        $"Processes that could not be conclusively inspected: {result.UncertainProcessCount}",
                        "Discord Instance Did Not Fully Stop",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Magic Mirror could not stop the Discord instance.\n\n{ex.Message}",
                    "Discord Stop Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                RefreshProfiles();
                UpdateSelectedProfileControls();
            }
        }

        private void btnDeleteProfile_Click(
    object sender,
    EventArgs e)
        {
            string? profileName =
                GetSelectedProfileName();

            if (profileName == null)
            {
                return;
            }

            // Re-check reality at the moment the button is pressed.
            instanceManager.CleanupStaleProcesses();

            ProfilePresenceState presence =
                GetProfilePresenceState(
                    profileName
                );

            if (presence != ProfilePresenceState.Dormant)
            {
                RefreshProfiles();

                MessageBox.Show(
                    Speak(
                        $"The profile \"{profileName}\" is still running.\n\nStop the instance before deleting the profile.",
                        $"The reflection \"{profileName}\" still has a presence within the glass.\n\nDismiss it before attempting to shatter it."
                    ),
                    SpeakTitle(
                        "Profile Is Running",
                        "The Reflection Resists"
                    ),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );

                return;
            }

            DialogResult confirmation =
                MessageBox.Show(
                    Speak(
                        $"Delete profile \"{profileName}\"?\n\n" +
                        $"The entire profile folder and its local Discord data will be moved to the Windows Recycle Bin.\n\n" +
                        $"This does not delete the Discord account itself.",

                        $"Shatter the reflection \"{profileName}\"?\n\n" +
                        $"Everything bound locally to this reflection will be cast from the mirror and placed within the Windows Recycle Bin.\n\n" +
                        $"The Discord account beyond the glass will remain untouched."
                    ),
                    SpeakTitle(
                        "Delete This Profile?",
                        "Shatter This Reflection?"
                    ),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2
                );

            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            // Check AGAIN after the user confirms.
            // Something may have changed while the dialog was open.
            instanceManager.CleanupStaleProcesses();

            presence =
                GetProfilePresenceState(
                    profileName
                );

            if (presence != ProfilePresenceState.Dormant)
            {
                RefreshProfiles();

                MessageBox.Show(
                    Speak(
                        "The profile became active before it could be deleted. Deletion was cancelled.",
                        "The reflection stirred before the mirror could shatter it. The rite has been abandoned."
                    ),
                    SpeakTitle(
                        "Deletion Cancelled",
                        "The Reflection Stirred"
                    ),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );

                return;
            }

            DeleteProfile(
                profileName
            );
        }
    }
}