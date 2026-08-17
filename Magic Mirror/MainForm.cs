using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using System.Reflection;

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
        private readonly SemaphoreSlim instanceOperationGate = new(1, 1);
        private bool isExplicitExit;
        private bool isHiddenToTray;
        private bool isDiscordInstallationAvailable;
        private bool isInstanceOperationInProgress;

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

        private string DiscordUpdateExePath =>
            Path.Combine(
                discordBasePath,
                "Update.exe"
            );

        private void InitializeTraySupport()
        {
            trayIcon.Text =
                "Magic Mirror";

            trayIcon.Icon =
                this.Icon ??
                System.Drawing.SystemIcons.Application;

            trayIcon.ContextMenuStrip =
                trayMenu;

            trayMenu.Opening +=
                (sender, e) =>
                {
                    RebuildTrayMenu();
                };

            trayIcon.DoubleClick +=
                (sender, e) =>
                {
                    RestoreFromTray();
                };

            // The tray icon is not a permanent second taskbar presence.
            // It is shown only while the main window is intentionally hidden.
            trayIcon.Visible = false;
        }

        private void UpdateVersionLabel()
        {
            string? version =
                Assembly
                    .GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(version))
            {
                version =
                    Assembly
                        .GetExecutingAssembly()
                        .GetName()
                        .Version?
                        .ToString(3)
                    ?? "Unknown";
            }

            // .NET may append build/source metadata such as:
            // 1.0.0+abc123...
            // We only want the user-facing version.
            int metadataIndex =
                version.IndexOf('+');

            if (metadataIndex >= 0)
            {
                version =
                    version.Substring(
                        0,
                        metadataIndex
                    );
            }

            lblVersion.Text =
                $"v{version}";
        }
        private void RebuildTrayMenu()
        {
            while (trayMenu.Items.Count > 0)
            {
                ToolStripItem item =
                    trayMenu.Items[0];

                trayMenu.Items.RemoveAt(0);
                item.Dispose();
            }

            trayMenu.Items.Clear();

            instanceManager.RefreshTrackedProcesses();
            instanceManager.CleanupStaleProcesses();
            ValidateDiscordInstallation(false);

            var openItem =
                new ToolStripMenuItem(
                    "Open Magic Mirror"
                );

            openItem.Click +=
                (sender, e) =>
                {
                    RestoreFromTray();
                };

            trayMenu.Items.Add(openItem);
            trayMenu.Items.Add(
                new ToolStripSeparator()
            );

            if (Directory.Exists(profileBasePath))
            {
                string[] profileNames =
                    new DirectoryInfo(profileBasePath)
                        .EnumerateDirectories()
                        .Select(directory => directory.Name)
                        .OrderBy(
                            name => name,
                            StringComparer.OrdinalIgnoreCase
                        )
                        .ToArray();

                foreach (string profileName
                    in profileNames)
                {
                    ProfilePresenceState presence =
                        GetProfilePresenceState(
                            profileName,
                            false
                        );

                    var profileItem =
                        new ToolStripMenuItem(
                            profileName
                        );

                    switch (presence)
                    {
                        case ProfilePresenceState.Dormant:
                            {
                                var launchItem =
                                    new ToolStripMenuItem(
                                        Speak(
                                            "Launch",
                                            "Summon"
                                        )
                                    )
                                    {
                                        Enabled =
                                            isDiscordInstallationAvailable &&
                                            !isInstanceOperationInProgress
                                    };

                                launchItem.Click +=
                                    async (sender, e) =>
                                    {
                                        await ActivateProfileAsync(
                                            profileName
                                        );
                                    };

                                profileItem.DropDownItems.Add(
                                    launchItem
                                );
                                break;
                            }

                        case ProfilePresenceState.Present:
                            {
                                var stopItem =
                                    new ToolStripMenuItem(
                                        Speak(
                                            "Stop",
                                            "Dismiss"
                                        )
                                    )
                                    {
                                        Enabled =
                                            isDiscordInstallationAvailable &&
                                            !isInstanceOperationInProgress
                                    };

                                stopItem.Click +=
                                    async (sender, e) =>
                                    {
                                        await StopProfileAsync(
                                            profileName
                                        );
                                    };

                                profileItem.DropDownItems.Add(
                                    stopItem
                                );
                                break;
                            }

                        case ProfilePresenceState.Veiled:
                            {
                                var reLaunchItem =
                                    new ToolStripMenuItem(
                                        Speak(
                                            "Re-Launch",
                                            "Reform"
                                        )
                                    )
                                    {
                                        Enabled =
                                            isDiscordInstallationAvailable &&
                                            !isInstanceOperationInProgress
                                    };

                                reLaunchItem.Click +=
                                    async (sender, e) =>
                                    {
                                        await ActivateProfileAsync(
                                            profileName
                                        );
                                    };

                                var stopItem =
                                    new ToolStripMenuItem(
                                        Speak(
                                            "Stop",
                                            "Dismiss"
                                        )
                                    )
                                    {
                                        Enabled =
                                            isDiscordInstallationAvailable &&
                                            !isInstanceOperationInProgress
                                    };

                                stopItem.Click +=
                                    async (sender, e) =>
                                    {
                                        await StopProfileAsync(
                                            profileName
                                        );
                                    };

                                profileItem.DropDownItems.Add(
                                    reLaunchItem
                                );
                                profileItem.DropDownItems.Add(
                                    stopItem
                                );
                                break;
                            }
                    }

                    trayMenu.Items.Add(
                        profileItem
                    );
                }

                if (profileNames.Length > 0)
                {
                    trayMenu.Items.Add(
                        new ToolStripSeparator()
                    );
                }
            }

            var exitItem =
                new ToolStripMenuItem(
                    "Exit Magic Mirror"
                );

            exitItem.Click +=
                (sender, e) =>
                {
                    isExplicitExit = true;
                    isHiddenToTray = false;
                    trayIcon.Visible = false;
                    Application.Exit();
                };

            trayMenu.Items.Add(exitItem);
        }

        private void UpdateTrayIconVisibility()
        {
            trayIcon.Visible =
                !isExplicitExit &&
                settings.MinimizeToTrayOnClose &&
                isHiddenToTray;
        }

        private void RestoreFromTray()
        {
            isHiddenToTray = false;
            UpdateTrayIconVisibility();

            // Opening from the tray always reconciles process state first.
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
            WindowManager.BringWindowToForeground(
                Handle
            );
        }

        public void RestoreFromExternalLaunch()
        {
            RestoreFromTray();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveWindowPlacement();

            if (!isExplicitExit &&
                settings.MinimizeToTrayOnClose &&
                e.CloseReason ==
                    CloseReason.UserClosing)
            {
                e.Cancel = true;

                Hide();
                isHiddenToTray = true;
                UpdateTrayIconVisibility();
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

            RestoreWindowPlacement();

            ApplyVoiceToInterface();

            UpdateVersionLabel();

            if (!string.IsNullOrWhiteSpace(
                    instanceManager.StateRecoveryWarning))
            {
                MessageBox.Show(
                    this,
                    instanceManager.StateRecoveryWarning,
                    "Tracking State Recovered",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }

            WriteTrackingDiagnostics(
                "Before stale-process cleanup"
            );

            RefreshProfiles(
                true
            );

            WriteTrackingDiagnostics(
                "After stale-process cleanup"
            );
        }

        private void SaveWindowPlacement()
        {
            try
            {
                Rectangle bounds;

                if (WindowState == FormWindowState.Normal)
                {
                    bounds = Bounds;
                }
                else
                {
                    // When maximized or minimized, RestoreBounds contains
                    // the normal window position and size.
                    bounds = RestoreBounds;
                }

                // Don't store obviously invalid geometry.
                if (bounds.Width <= 0 ||
                    bounds.Height <= 0)
                {
                    return;
                }

                settings.WindowX =
                    bounds.X;

                settings.WindowY =
                    bounds.Y;

                settings.WindowWidth =
                    bounds.Width;

                settings.WindowHeight =
                    bounds.Height;

                settings.WindowMaximized =
                    WindowState ==
                    FormWindowState.Maximized;

                SettingsManager.Save(
                    settings
                );
            }
            catch (Exception ex)
            {
                AppLogger.Warning(
                    $"Could not save window position: {ex.Message}"
                );
            }
        }

        private void RestoreWindowPlacement()
        {
            if (!settings.WindowX.HasValue ||
                !settings.WindowY.HasValue ||
                !settings.WindowWidth.HasValue ||
                !settings.WindowHeight.HasValue)
            {
                // No saved placement yet.
                // Keep the Designer's default CenterScreen behavior.
                return;
            }

            int width =
                Math.Max(
                    settings.WindowWidth.Value,
                    MinimumSize.Width
                );

            int height =
                Math.Max(
                    settings.WindowHeight.Value,
                    MinimumSize.Height
                );

            var savedBounds =
                new Rectangle(
                    settings.WindowX.Value,
                    settings.WindowY.Value,
                    width,
                    height
                );

            bool visibleOnAnyScreen =
                Screen.AllScreens.Any(
                    screen =>
                        screen.WorkingArea.IntersectsWith(
                            savedBounds
                        )
                );

            if (!visibleOnAnyScreen)
            {
                // The monitor may have been unplugged or its
                // resolution/layout changed. Don't restore off-screen.
                StartPosition =
                    FormStartPosition.CenterScreen;

                return;
            }

            StartPosition =
                FormStartPosition.Manual;

            Bounds =
                savedBounds;

            if (settings.WindowMaximized)
            {
                WindowState =
                    FormWindowState.Maximized;
            }
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

            btnBackupProfiles.Text = Speak(
                "Backup Profiles",
                "Preserve Reflections"
            );

            UpdateSelectedProfileControls();
        }

        private enum ProfilePresenceState
        {
            Dormant,
            Present,
            Veiled
        }

        private bool ValidateDiscordInstallation(
            bool showWarning)
        {
            bool available =
                File.Exists(
                    DiscordUpdateExePath
                );

            isDiscordInstallationAvailable =
                available;

            if (!available && showWarning)
            {
                AppLogger.Warning(
                    $"Discord installation validation failed. " +
                    $"Update.exe was not found at: {DiscordUpdateExePath}"
                );
            }

            if (!available && showWarning)
            {
                MessageBox.Show(
                    this,
                    "Magic Mirror could not find Discord's launcher.\n\n" +
                    "Expected location:\n" +
                    DiscordUpdateExePath +
                    "\n\nDiscord-dependent actions are disabled. Refresh remains available so Magic Mirror can detect a later installation or repair.",
                    "Discord Installation Not Found",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }

            return available;
        }

        private ProfilePresenceState GetProfilePresenceState(
            string profileName,
            bool refreshTracking = true)
        {
            if (refreshTracking)
            {
                instanceManager.RefreshTrackedProcesses(
                    profileName
                );

                instanceManager.CleanupStaleProcesses();
            }

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
            btnCreateProfile.Enabled =
                isDiscordInstallationAvailable &&
                !isInstanceOperationInProgress;

            btnBackupProfiles.Enabled =
                !isLaunchInProgress;

            string? profileName =
                GetSelectedProfileName();

            if (isInstanceOperationInProgress)
            {
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
                }

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
                isDiscordInstallationAvailable &&
                presence != ProfilePresenceState.Dormant;

            switch (presence)
            {
                case ProfilePresenceState.Dormant:
                    btnOpenProfile.Text = Speak(
                        "Launch",
                        "Summon"
                    );

                    btnOpenProfile.Enabled =
                        isDiscordInstallationAvailable;
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

                    btnOpenProfile.Enabled =
                        isDiscordInstallationAvailable;
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

        private void RefreshProfiles(
            bool showInstallationWarning = false)
        {
            string? selectedProfileName =
                GetSelectedProfileName();

            ValidateDiscordInstallation(
                showInstallationWarning
            );

            instanceManager.RefreshTrackedProcesses();
            instanceManager.CleanupStaleProcesses();

            lvProfiles.BeginUpdate();

            try
            {
                lvProfiles.Items.Clear();

                IEnumerable<string> profiles =
                    Directory.Exists(profileBasePath)
                        ? new DirectoryInfo(profileBasePath)
                            .EnumerateDirectories()
                            .Select(directory => directory.Name)
                            .OrderBy(
                                name => name,
                                StringComparer.OrdinalIgnoreCase
                            )
                        : Enumerable.Empty<string>();

                foreach (string profileName
                    in profiles)
                {
                    ProfilePresenceState presence =
                        GetProfilePresenceState(
                            profileName,
                            false
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
        // Process discovery, executable-path verification, parent/child
        // association, and recovery scans live in InstanceManager.

        // =========================================================
        // Launch
        // =========================================================

        private async Task LaunchProfileAsync(
            string profileName)
        {
            AppLogger.Info(
                $"Launch requested for profile \"{profileName}\"."
            );

            string profilePath =
                Path.Combine(
                    profileBasePath,
                    profileName
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

            if (!ValidateDiscordInstallation(true))
            {
                UpdateSelectedProfileControls();
                return;
            }

            try
            {
                IReadOnlyList<int> processesBeforeLaunch =
                    instanceManager
                        .GetVerifiedInstalledDiscordProcessIds();

                AppLogger.Info(
                    $"Launch baseline for \"{profileName}\": " +
                    $"{processesBeforeLaunch.Count} verified Discord process(es) already existed."
                );

                DateTime trackingStartedUtc =
                    DateTime.UtcNow;

                var startInfo =
                    new ProcessStartInfo
                    {
                        FileName =
                            DiscordUpdateExePath,

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

                if (launcher == null)
                {
                    throw new InvalidOperationException(
                        "Windows did not return a Discord launcher process."
                    );
                }

                AppLogger.Info(
                    $"Discord Update.exe started for \"{profileName}\". " +
                    $"Launcher PID: {launcher.Id}."
                );

                TrackedInstance? trackedInstance =
                    await instanceManager
                        .TrackLaunchedInstanceAsync(
                            profileName,
                            processesBeforeLaunch,
                            launcher.Id,
                            trackingStartedUtc
                        );

                if (trackedInstance == null)
                {
                    AppLogger.Warning(
                        $"PID tracking failed for profile \"{profileName}\"."
                    );

                    MessageBox.Show(
                        Speak(
                            "Discord was launched, but Magic Mirror could not safely identify which new Discord process group belonged to this profile.",
                            "The glass stirred, yet the mirror could not safely bind what answered to this reflection."
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

                AppLogger.Info(
                    $"Profile \"{profileName}\" was associated with " +
                    $"{trackedInstance.Processes.Count} Discord process(es). " +
                    $"PIDs: {string.Join(", ", trackedInstance.Processes.Select(p => p.ProcessId))}"
                );

                instanceManager.RefreshTrackedProcesses(
                    profileName
                );

                RefreshProfiles();

                WriteTrackingDiagnostics(
                    "After tracking new instance"
                );
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    $"Launch failed for profile \"{profileName}\".",
                    ex
                );

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
            AppLogger.Info(
                $"Re-Launch requested for profile \"{profileName}\"."
            );

            // Re-check immediately before doing anything destructive.
            instanceManager.CleanupStaleProcesses();

            ProfilePresenceState presence =
                GetProfilePresenceState(
                    profileName
                );

            AppLogger.Info(
                $"Profile \"{profileName}\" state before Re-Launch: {presence}."
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

            AppLogger.Info(
                $"Re-Launch stop result for \"{profileName}\": " +
                $"VerifiedBeforeStop={stopResult.VerifiedBeforeStop}, " +
                $"RemainingVerified={stopResult.RemainingVerifiedPids.Count}, " +
                $"Uncertain={stopResult.UncertainProcessCount}, " +
                $"Success={stopResult.Success}."
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
                AppLogger.Warning(
                    $"Re-Launch aborted for \"{profileName}\" because the previous instance could not be safely confirmed stopped."
                );

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

                AppLogger.Info(
                    $"Profile \"{profileName}\" was moved to the Windows Recycle Bin."
                );

                RefreshProfiles();
            }
            catch (OperationCanceledException)
            {
                AppLogger.Info(
                    $"Profile deletion was cancelled for \"{profileName}\"."
                );
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    $"Profile deletion failed for \"{profileName}\".",
                    ex
                );

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
                    Width = 430,
                    Height = 455,
                    FormBorderStyle =
                        FormBorderStyle.FixedDialog,
                    StartPosition =
                        FormStartPosition.CenterParent,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    ShowInTaskbar = false
                };

            var mirrorVoiceCheckBox =
                new CheckBox
                {
                    Text = "Let the Mirror speak",
                    Left = 20,
                    Top = 20,
                    Width = 350,
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
                    Width = 350,
                    Height = 35
                };

            var minimizeToTrayCheckBox =
                new CheckBox
                {
                    Text =
                        "Minimize to traybar on close",
                    Left = 20,
                    Top = 90,
                    Width = 350,
                    Checked =
                        settings.MinimizeToTrayOnClose
                };

            var openProfilesFolderButton =
                new Button
                {
                    Text = Speak(
                        "Open Profiles Folder",
                        "Peer Behind the Glass"
                    ),
                    Left = 20,
                    Top = 130,
                    Width = 180,
                    Height = 30
                };

            var donateButton =
                new Button
                {
                    Text = "Donate",
                    Left = 210,
                    Top = 130,
                    Width = 180,
                    Height = 30
                };

            var emergencyLabel =
                new Label
                {
                    Text = "Emergency Discord recovery",
                    Left = 20,
                    Top = 185,
                    Width = 350,
                    AutoSize = true
                };

            var emergencyDescription =
                new Label
                {
                    Text =
                        "These tools terminate verified Discord.exe processes only. They never delete profile folders or profile data.",
                    Left = 20,
                    Top = 208,
                    Width = 370,
                    Height = 45
                };

            var nukeNonVisibleButton =
                new Button
                {
                    Text = "Nuke Non-Visible Instances",
                    Left = 20,
                    Top = 260,
                    Width = 180,
                    Height = 30
                };

            var nukeAllButton =
                new Button
                {
                    Text = "Nuke All Discord Instances",
                    Left = 210,
                    Top = 260,
                    Width = 180,
                    Height = 30
                };

            var helpButton =
                new Button
                {
                    Text = Speak(
                        "Help",
                        "Seek Guidance"
                ),

                    Left = 20,
                    Top = 315,
                    Width = 180,
                    Height = 30
                };

            var saveButton =
                new Button
                {
                    Text = "Save",
                    Left = 235,
                    Top = 360,
                    Width = 75,
                    DialogResult =
                        DialogResult.OK
                };

            var cancelButton =
                new Button
                {
                    Text = "Cancel",
                    Left = 315,
                    Top = 360,
                    Width = 75,
                    DialogResult =
                        DialogResult.Cancel
                };

            openProfilesFolderButton.Click +=
    (sender, e) =>
    {
        if (!Directory.Exists(profileBasePath))
        {
            MessageBox.Show(
                dialog,
                $"The Profiles folder does not currently exist.\n\nExpected location:\n{profileBasePath}",
                "Profiles Folder Not Found",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );

            return;
        }

        try
        {
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

            helpButton.Click +=
                (sender, e) =>
                {
                    ShowHelpDialog(dialog);
                };

            nukeNonVisibleButton.Click +=
                async (sender, e) =>
                {
                    nukeNonVisibleButton.Enabled = false;
                    nukeAllButton.Enabled = false;
                    saveButton.Enabled = false;
                    cancelButton.Enabled = false;
                    dialog.ControlBox = false;
                    helpButton.Enabled = false;

                    try
                    {
                        await NukeNonVisibleDiscordInstancesAsync(
                            dialog
                        );
                    }
                    finally
                    {
                        nukeNonVisibleButton.Enabled = true;
                        nukeAllButton.Enabled = true;
                        saveButton.Enabled = true;
                        cancelButton.Enabled = true;
                        dialog.ControlBox = true;
                        helpButton.Enabled = true;
                    }
                };

            nukeAllButton.Click +=
                async (sender, e) =>
                {
                    nukeNonVisibleButton.Enabled = false;
                    nukeAllButton.Enabled = false;
                    saveButton.Enabled = false;
                    cancelButton.Enabled = false;
                    dialog.ControlBox = false;

                    try
                    {
                        await NukeAllDiscordInstancesAsync(
                            dialog
                        );
                    }
                    finally
                    {
                        nukeNonVisibleButton.Enabled = true;
                        nukeAllButton.Enabled = true;
                        saveButton.Enabled = true;
                        cancelButton.Enabled = true;
                        dialog.ControlBox = true;
                    }
                };

            dialog.Controls.Add(mirrorVoiceCheckBox);
            dialog.Controls.Add(mirrorVoiceDescription);
            dialog.Controls.Add(minimizeToTrayCheckBox);
            dialog.Controls.Add(openProfilesFolderButton);
            dialog.Controls.Add(donateButton);
            dialog.Controls.Add(emergencyLabel);
            dialog.Controls.Add(emergencyDescription);
            dialog.Controls.Add(nukeNonVisibleButton);
            dialog.Controls.Add(nukeAllButton);
            dialog.Controls.Add(helpButton);
            dialog.Controls.Add(saveButton);
            dialog.Controls.Add(cancelButton);

            dialog.AcceptButton = saveButton;
            dialog.CancelButton = cancelButton;

            if (dialog.ShowDialog(this)
                != DialogResult.OK)
            {
                return;
            }

            settings.UseMirrorVoice =
                mirrorVoiceCheckBox.Checked;

            settings.MinimizeToTrayOnClose =
                minimizeToTrayCheckBox.Checked;

            try
            {
                SettingsManager.Save(
                    settings
                );

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

        private async Task NukeAllDiscordInstancesAsync(
            Form owner)
        {
            DialogResult firstConfirmation =
                MessageBox.Show(
                    owner,
                    "Emergency recovery will search for every Discord.exe whose executable path is genuinely under your Local AppData Discord installation and terminate it.\n\nProfile folders and profile data will NOT be deleted.\n\nContinue?",
                    "Nuke All Discord Instances?",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2
                );

            if (firstConfirmation !=
                DialogResult.Yes)
            {
                return;
            }

            DialogResult secondConfirmation =
                MessageBox.Show(
                    owner,
                    "Final confirmation. This will forcibly terminate ALL verified Discord instances under the Discord installation and clear Magic Mirror's tracking state.\n\nIt will not delete any profile data.\n\nProceed?",
                    "Final Confirmation — Nuke All",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Stop,
                    MessageBoxDefaultButton.Button2
                );

            if (secondConfirmation !=
                DialogResult.Yes)
            {
                return;
            }

            AppLogger.Warning(
                "Nuke All Discord Instances confirmed by the user."
            );

            if (!await TryBeginInstanceOperationAsync())
            {
                return;
            }

            DiscordRecoveryResult? result = null;

            try
            {
                result =
                    await instanceManager
                        .NukeAllDiscordInstancesAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "Nuke All Discord Instances failed.",
                    ex
                );

                MessageBox.Show(
                    owner,
                    $"Magic Mirror could not complete the emergency recovery.\n\n{ex.Message}",
                    "Nuke All Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                EndInstanceOperation();
                RefreshProfiles();
            }

            if (result == null)
            {
                return;
            }

            AppLogger.Warning(
                $"Nuke All completed. " +
                $"VerifiedBeforeKill={result.VerifiedBeforeKill}, " +
                $"TargetedProcesses={result.TargetedProcessCount}, " +
                $"TargetedGroups={result.TargetedGroupCount}, " +
                $"RemainingVerified={result.RemainingVerifiedPids.Count}, " +
                $"Uncertain={result.UncertainProcessCount}, " +
                $"Success={result.Success}."
            );

            ShowRecoveryResult(
                owner,
                result,
                "Nuke All Complete",
                "All verified Discord processes targeted by the recovery tool have stopped. Magic Mirror tracking state was cleared. Profile data was not changed."
            );
        }

        private async Task NukeNonVisibleDiscordInstancesAsync(
            Form owner)
        {
            DialogResult firstConfirmation =
                MessageBox.Show(
                    owner,
                    "This recovery tool groups verified Discord.exe processes and targets only groups with no usable visible Discord window.\n\nIt is intended for orphaned or headless Discord instances. Profile data will NOT be deleted.\n\nContinue?",
                    "Nuke Non-Visible Discord Instances?",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2
                );

            if (firstConfirmation !=
                DialogResult.Yes)
            {
                return;
            }

            DialogResult secondConfirmation =
                MessageBox.Show(
                    owner,
                    "Final confirmation. Magic Mirror will re-verify executable paths immediately before termination and will leave any Discord process group with a usable visible window alone.\n\nProceed?",
                    "Final Confirmation — Nuke Non-Visible",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Stop,
                    MessageBoxDefaultButton.Button2
                );

            if (secondConfirmation !=
                DialogResult.Yes)
            {
                return;
            }

            AppLogger.Warning(
                "Nuke Non-Visible Discord Instances confirmed by the user."
            );

            if (!await TryBeginInstanceOperationAsync())
            {
                return;
            }

            DiscordRecoveryResult? result = null;

            try
            {
                result =
                    await instanceManager
                        .NukeNonVisibleDiscordInstancesAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "Nuke Non-Visible Discord Instances failed.",
                    ex
                );

                MessageBox.Show(
                    owner,
                    $"Magic Mirror could not complete the non-visible recovery.\n\n{ex.Message}",
                    "Nuke Non-Visible Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                EndInstanceOperation();
                RefreshProfiles();
            }

            if (result == null)
            {
                return;
            }

            AppLogger.Warning(
                $"Nuke Non-Visible completed. " +
                $"VerifiedBeforeKill={result.VerifiedBeforeKill}, " +
                $"TargetedProcesses={result.TargetedProcessCount}, " +
                $"TargetedGroups={result.TargetedGroupCount}, " +
                $"RemainingVerified={result.RemainingVerifiedPids.Count}, " +
                $"Uncertain={result.UncertainProcessCount}, " +
                $"Success={result.Success}."
            );

            ShowRecoveryResult(
                owner,
                result,
                "Non-Visible Recovery Complete",
                "All verified non-visible Discord process groups targeted by the recovery tool have stopped. Profile data was not changed."
            );
        }

        private static void ShowRecoveryResult(
            Form owner,
            DiscordRecoveryResult result,
            string successTitle,
            string successMessage)
        {
            if (result.Success &&
                result.VerifiedBeforeKill == 0 &&
                result.TargetedProcessCount == 0)
            {
                MessageBox.Show(
                    owner,
                    "No matching verified Discord processes needed recovery. Profile data was not changed.",
                    successTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                return;
            }

            if (result.Success)
            {
                MessageBox.Show(
                    owner,
                    successMessage +
                    $"\n\nVerified processes initially targeted: {result.VerifiedBeforeKill}" +
                    $"\nProcesses terminated/targeted during recovery: {result.TargetedProcessCount}" +
                    $"\nProcess groups targeted: {result.TargetedGroupCount}",
                    successTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                return;
            }

            string remaining =
                result.RemainingVerifiedPids.Count > 0
                    ? string.Join(
                        ", ",
                        result.RemainingVerifiedPids
                    )
                    : "None";

            MessageBox.Show(
                owner,
                "Magic Mirror could not conclusively finish the recovery.\n\n" +
                $"Verified target processes still running: {remaining}\n" +
                $"Discord processes that could not be conclusively inspected: {result.UncertainProcessCount}\n\n" +
                "No profile data was deleted.",
                "Recovery Incomplete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }

        private List<string>? PromptForBackupProfiles()
        {
            if (!Directory.Exists(profileBasePath))
            {
                MessageBox.Show(
                    this,
                    Speak(
                        "No Profiles folder currently exists.",
                        "The mirror holds no reflections to preserve."
                    ),
                    SpeakTitle(
                        "No Profiles Found",
                        "No Reflections Found"
                    ),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                return null;
            }

            // Refresh tracking first so we don't accidentally
            // offer a currently running profile for backup.
            instanceManager.RefreshTrackedProcesses();
            instanceManager.CleanupStaleProcesses();

            List<string> allProfiles =
                new DirectoryInfo(profileBasePath)
                    .EnumerateDirectories()
                    .Select(directory => directory.Name)
                    .OrderBy(
                        name => name,
                        StringComparer.OrdinalIgnoreCase
                    )
                    .ToList();

            if (allProfiles.Count == 0)
            {
                MessageBox.Show(
                    this,
                    Speak(
                        "There are no profiles available to back up.",
                        "There are no reflections within the glass to preserve."
                    ),
                    SpeakTitle(
                        "No Profiles Found",
                        "No Reflections Found"
                    ),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                return null;
            }

            List<string> dormantProfiles =
                allProfiles
                    .Where(
                        profileName =>
                            GetProfilePresenceState(
                                profileName,
                                refreshTracking: false
                            )
                            == ProfilePresenceState.Dormant
                    )
                    .ToList();

            if (dormantProfiles.Count == 0)
            {
                MessageBox.Show(
                    this,
                    Speak(
                        "There are no stopped profiles available for backup.\n\n" +
                        "Stop the profiles you want to back up first.",

                        "No dormant reflections are ready to be preserved.\n\n" +
                        "Dismiss the reflections you wish to preserve first."
                    ),
                    SpeakTitle(
                        "No Profiles Available",
                        "No Reflections Available"
                    ),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                return null;
            }

            using var dialog =
                new Form
                {
                    Text = SpeakTitle(
                        "Backup Profiles",
                        "Preserve Reflections"
                    ),

                    Width = 430,
                    Height = 430,

                    FormBorderStyle =
                        FormBorderStyle.FixedDialog,

                    StartPosition =
                        FormStartPosition.CenterParent,

                    MaximizeBox = false,
                    MinimizeBox = false,
                    ShowInTaskbar = false
                };

            var descriptionLabel =
                new Label
                {
                    Text = Speak(
                        "Select the profiles you want to back up.\n" +
                        "Only stopped profiles can be backed up.",

                        "Choose the reflections you wish to preserve.\n" +
                        "Only dormant reflections may be preserved."
                    ),

                    Left = 15,
                    Top = 15,
                    Width = 385,
                    Height = 40
                };

            var profileList =
                new CheckedListBox
                {
                    Left = 15,
                    Top = 60,
                    Width = 385,
                    Height = 250,
                    CheckOnClick = true
                };

            foreach (string profileName
                in dormantProfiles)
            {
                profileList.Items.Add(
                    profileName
                );
            }

            var selectAllButton =
                new Button
                {
                    Text = Speak(
                        "Select All",
                        "Mark All"
                    ),

                    Left = 15,
                    Top = 320,
                    Width = 90,
                    Height = 28
                };

            var continueButton =
                new Button
                {
                    Text = Speak(
                        "Continue",
                        "Proceed"
                    ),

                    Left = 230,
                    Top = 320,
                    Width = 80,
                    Height = 28
                };

            var cancelButton =
                new Button
                {
                    Text = Speak(
                        "Cancel",
                        "Turn Away"
                    ),

                    Left = 320,
                    Top = 320,
                    Width = 80,
                    Height = 28,

                    DialogResult =
                        DialogResult.Cancel
                };

            selectAllButton.Click +=
                (sender, e) =>
                {
                    for (int i = 0;
                        i < profileList.Items.Count;
                        i++)
                    {
                        profileList.SetItemChecked(
                            i,
                            true
                        );
                    }
                };

            continueButton.Click +=
                (sender, e) =>
                {
                    if (profileList.CheckedItems.Count == 0)
                    {
                        MessageBox.Show(
                            dialog,
                            Speak(
                                "Select at least one profile to back up.",
                                "Choose at least one reflection to preserve."
                            ),
                            SpeakTitle(
                                "No Profiles Selected",
                                "No Reflections Chosen"
                            ),
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information
                        );

                        return;
                    }

                    dialog.DialogResult =
                        DialogResult.OK;

                    dialog.Close();
                };

            dialog.Controls.Add(
                descriptionLabel
            );

            dialog.Controls.Add(
                profileList
            );

            dialog.Controls.Add(
                selectAllButton
            );

            dialog.Controls.Add(
                continueButton
            );

            dialog.Controls.Add(
                cancelButton
            );

            dialog.AcceptButton =
                continueButton;

            dialog.CancelButton =
                cancelButton;

            if (dialog.ShowDialog(this)
                != DialogResult.OK)
            {
                return null;
            }

            return profileList.CheckedItems
                .Cast<string>()
                .ToList();
        }

        private string CreateUniqueBackupDirectory(
            string destinationRoot)
        {
            string timestamp =
                DateTime.Now.ToString(
                    "yyyy-MM-dd HHmm"
                );

            string baseName =
                $"Magic Mirror Backup {timestamp}";

            string candidate =
                Path.Combine(
                    destinationRoot,
                    baseName
                );

            int suffix = 2;

            while (Directory.Exists(candidate))
            {
                candidate =
                    Path.Combine(
                        destinationRoot,
                        $"{baseName} ({suffix})"
                    );

                suffix++;
            }

            return candidate;
        }

        private void CopyDirectory(
    string sourceDirectory,
    string destinationDirectory)
        {
            DirectoryInfo source =
                new DirectoryInfo(
                    sourceDirectory
                );

            if (!source.Exists)
            {
                throw new DirectoryNotFoundException(
                    $"Source directory could not be found:\n{sourceDirectory}"
                );
            }

            Directory.CreateDirectory(
                destinationDirectory
            );

            foreach (FileInfo file
                in source.GetFiles())
            {
                string destinationFile =
                    Path.Combine(
                        destinationDirectory,
                        file.Name
                    );

                file.CopyTo(
                    destinationFile,
                    false
                );
            }

            foreach (DirectoryInfo directory
                in source.GetDirectories())
            {
                // Do not follow junctions or symbolic directory links.
                // A profile backup should never wander outside
                // the actual profile directory.
                if ((directory.Attributes &
                     FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                string childDestination =
                    Path.Combine(
                        destinationDirectory,
                        directory.Name
                    );

                CopyDirectory(
                    directory.FullName,
                    childDestination
                );
            }
        }

        private static bool IsPathInsideOrEqual(
            string path,
            string parentDirectory)
        {
            string normalizedPath =
                Path.GetFullPath(path)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    );

            string normalizedParent =
                Path.GetFullPath(parentDirectory)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    );

            if (string.Equals(
                    normalizedPath,
                    normalizedParent,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string parentWithSeparator =
                normalizedParent +
                Path.DirectorySeparatorChar;

            return normalizedPath.StartsWith(
                parentWithSeparator,
                StringComparison.OrdinalIgnoreCase
            );
        }

        // =========================================================
        // Button / ListView events
        // =========================================================

        private void btnRefresh_Click(
            object sender,
            EventArgs e)
        {
            RefreshProfiles(
                true
            );
        }

        private void btnCreateProfile_Click(
            object sender,
            EventArgs e)
        {
            if (!ValidateDiscordInstallation(true))
            {
                UpdateSelectedProfileControls();
                return;
            }

            string? profileName =
                PromptForProfileName();

            if (profileName == null)
            {
                return;
            }

            // Re-check immediately before creating any directories. This is
            // what prevents profile creation from manufacturing a fake
            // %LOCALAPPDATA%\Discord tree when Discord is not installed.
            if (!ValidateDiscordInstallation(true))
            {
                UpdateSelectedProfileControls();
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

                AppLogger.Info(
                    $"Profile \"{profileName}\" created."
                );

                RefreshProfiles();

                SelectProfile(
                    profileName
                );
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    $"Profile creation failed for \"{profileName}\".",
                    ex
                );

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

        private void ShowHelpDialog(
    IWin32Window owner)
        {
            using var helpDialog =
                new Form
                {
                    Text = SpeakTitle(
                        "Magic Mirror Help",
                        "The Mirror Offers Guidance"
                    ),

                    Width = 620,
                    Height = 560,

                    FormBorderStyle =
                        FormBorderStyle.FixedDialog,

                    StartPosition =
                        FormStartPosition.CenterParent,

                    MaximizeBox = false,
                    MinimizeBox = false,
                    ShowInTaskbar = false
                };

            string normalHelpText =
                """
        MAGIC MIRROR

        Magic Mirror allows multiple isolated Discord profiles to be launched from the same Windows installation. Each profile keeps its own local Discord data inside the Discord\Profiles folder.


        PROFILE STATES

        Stopped
        No verified Discord processes are currently associated with the profile.

        Running
        The profile has verified Discord processes and a usable Discord window.

        Background
        The profile still has verified Discord processes, but Magic Mirror cannot find a usable Discord window.


        PROFILE ACTIONS

        Launch
        Starts Discord using the selected profile.

        Re-Launch
        Used for a Background profile. Magic Mirror safely stops the remaining tracked Discord instance before starting the profile again.

        Stop Instance
        Terminates the verified Discord processes associated with that profile.

        Delete Profile
        Deletes the local profile folder by sending it to the Windows Recycle Bin. This does not delete the Discord account itself.


        BACKUPS

        Backup Profiles copies selected stopped profiles to a location of your choice.

        Only stopped profiles can be backed up so Discord is not writing to the profile while it is being copied.

        Backup folders may contain private Discord data, cached content, identifiers, settings, and other local information. Store backups somewhere you trust.

        Backing up or copying a profile does not modify the original profile.


        SYSTEM TRAY

        If "Minimize to traybar on close" is enabled, closing the Magic Mirror window hides it instead of terminating it.

        Right-click the tray icon to open Magic Mirror or control profiles.

        Use "Exit Magic Mirror" from the tray when you actually want to terminate the application.


        EMERGENCY RECOVERY

        Nuke Non-Visible Discord Instances
        Terminates verified Discord process groups that have no usable visible Discord window.

        Nuke All Discord Instances
        Terminates all Discord.exe processes that Magic Mirror can verify are genuinely located under the local Discord installation.

        These emergency tools never delete profile folders or profile data.


        DISCORD INSTALLATION

        Magic Mirror normally expects Discord's Update.exe inside:

        %LOCALAPPDATA%\Discord

        If Update.exe is missing, launching Discord-dependent actions is disabled.

        Existing profile folders can still be accessed or backed up even if Discord itself needs to be repaired or reinstalled.
        """;

            string mirrorHelpText =
                """
        MAGIC MIRROR

        Within the glass dwell separate Reflections of Discord. Each Reflection keeps its own local memories beneath the Discord\Profiles directory.


        STATES OF A REFLECTION

        Dormant
        No verified Discord presence remains bound to the Reflection.

        Present
        The Reflection lives, and its visage remains visible through a usable Discord window.

        Veiled
        The Reflection still lives within the glass, yet no usable Discord window can be seen.


        COMMANDS OF THE MIRROR

        Summon
        Calls forth Discord using the chosen Reflection.

        Reform Reflection
        Used when a Reflection has become Veiled. What remains is safely dismissed before the Reflection is summoned anew.

        Dismiss Reflection
        Terminates the verified Discord processes bound to that Reflection.

        Shatter Reflection
        Sends the Reflection's local profile folder to the Windows Recycle Bin. The Discord account beyond the glass remains untouched.


        PRESERVING REFLECTIONS

        Preserve Reflections copies chosen dormant Reflections to a location of your choosing.

        Only dormant Reflections may be preserved. A living Reflection may still be writing memories into its files, and copying them at such a moment could leave an incomplete preservation.

        These preserved Reflections may contain private Discord data, cached memories, identifiers, settings, and other local traces.

        Entrust their resting place only to somewhere you deem safe.

        Preservation never alters the original Reflection.


        THE TRAY

        When "Minimize to traybar on close" is enabled, closing the mirror's window merely conceals it.

        The tray icon may then be invoked to reopen the mirror or command individual Reflections.

        Choose "Exit Magic Mirror" when the mirror itself must truly fall silent.


        EMERGENCY RITES

        Nuke Non-Visible Discord Instances
        Dismisses verified Discord process groups whose visible presence has been lost.

        Nuke All Discord Instances
        Dismisses every Discord.exe process the mirror can verify as genuinely dwelling beneath the local Discord installation.

        Neither rite shatters Reflections nor deletes their profile data.


        THE DISCORD INSTALLATION

        The mirror normally seeks Discord's Update.exe beneath:

        %LOCALAPPDATA%\Discord

        If that launcher is absent, actions requiring Discord are withheld rather than attempted blindly.

        Existing Reflections may still be inspected, opened as folders, or preserved while Discord itself is repaired or restored.
        """;

            var helpTextBox =
                new TextBox
                {
                    Left = 15,
                    Top = 15,
                    Width = 575,
                    Height = 445,

                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars =
                        ScrollBars.Vertical,

                    WordWrap = true,

                    Text = Speak(
                        normalHelpText,
                        mirrorHelpText
                    )
                };

            var closeButton =
                new Button
                {
                    Text = Speak(
                        "Close",
                        "Turn Away"
                    ),

                    Left = 500,
                    Top = 475,
                    Width = 90,
                    Height = 30,

                    DialogResult =
                        DialogResult.OK
                };

            helpDialog.Controls.Add(
                helpTextBox
            );

            helpDialog.Controls.Add(
                closeButton
            );

            helpDialog.AcceptButton =
                closeButton;

            helpDialog.CancelButton =
                closeButton;

            helpDialog.ShowDialog(owner);
        }

        private async void btnOpenProfile_Click(
            object sender,
            EventArgs e)
        {
            string? profileName =
                GetSelectedProfileName();

            if (profileName == null)
            {
                return;
            }

            await ActivateProfileAsync(
                profileName
            );
        }

        private async void lvProfiles_MouseDoubleClick(
            object sender,
            MouseEventArgs e)
        {
            ListViewItem? clickedItem =
                lvProfiles.GetItemAt(
                    e.X,
                    e.Y
                );

            if (clickedItem?.Tag
                is not string profileName)
            {
                return;
            }

            ProfilePresenceState presence =
                GetProfilePresenceState(
                    profileName
                );

            // Present profiles deliberately ignore double-click.
            if (presence == ProfilePresenceState.Present)
            {
                return;
            }

            await ActivateProfileAsync(
                profileName
            );
        }

        private async Task ActivateProfileAsync(
            string profileName)
        {
            if (!ValidateDiscordInstallation(true))
            {
                UpdateSelectedProfileControls();
                return;
            }

            if (!await TryBeginInstanceOperationAsync())
            {
                return;
            }

            try
            {
                ProfilePresenceState presence =
                    GetProfilePresenceState(
                        profileName
                    );

                if (presence == ProfilePresenceState.Present)
                {
                    return;
                }

                if (presence == ProfilePresenceState.Veiled)
                {
                    DialogResult confirmation =
                        MessageBox.Show(
                            Speak(
                                $"The Discord instance for \"{profileName}\" is still running, but its window is gone.\n\n" +
                                "Re-launching will terminate that background instance and start the same profile again.",

                                $"The reflection \"{profileName}\" remains within the glass, yet its visage is lost.\n\n" +
                                "To reform it, the mirror must first dismiss what remains and summon the reflection anew."
                            ),
                            SpeakTitle(
                                "Re-Launch Discord Instance?",
                                "Reform This Reflection?"
                            ),
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2
                        );

                    if (confirmation !=
                        DialogResult.Yes)
                    {
                        return;
                    }
                }

                isLaunchInProgress = true;
                isReLaunchInProgress =
                    presence ==
                        ProfilePresenceState.Veiled;

                UpdateSelectedProfileControls();

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
                EndInstanceOperation();
                RefreshProfiles();
            }
        }

        private async Task<bool> TryBeginInstanceOperationAsync()
        {
            bool entered =
                await instanceOperationGate
                    .WaitAsync(0);

            if (!entered)
            {
                MessageBox.Show(
                    this,
                    "Another Discord operation is already in progress. Finish that operation before starting another one.",
                    "Operation In Progress",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                return false;
            }

            isInstanceOperationInProgress = true;
            UpdateSelectedProfileControls();
            return true;
        }

        private void EndInstanceOperation()
        {
            if (!isInstanceOperationInProgress)
            {
                return;
            }

            isInstanceOperationInProgress = false;
            instanceOperationGate.Release();
            UpdateSelectedProfileControls();
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

            await StopProfileAsync(
                profileName
            );
        }

        private async Task StopProfileAsync(
            string profileName)
        {
            AppLogger.Info(
                $"Stop requested for profile \"{profileName}\"."
            );

            if (!ValidateDiscordInstallation(true))
            {
                UpdateSelectedProfileControls();
                return;
            }

            if (!await TryBeginInstanceOperationAsync())
            {
                return;
            }

            try
            {
                instanceManager.RefreshTrackedProcesses(
                    profileName
                );
                instanceManager.CleanupStaleProcesses();

                IReadOnlyList<int> verifiedPids =
                    instanceManager
                        .GetVerifiedProcessIds(
                            profileName
                        );

                AppLogger.Info(
                    $"Stop verification for \"{profileName}\": " +
                    $"{verifiedPids.Count} verified process(es). " +
                    $"PIDs: {string.Join(", ", verifiedPids)}"
                );

                if (verifiedPids.Count == 0)
                {
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
                            "Their presence will be severed, though the reflection itself shall remain."
                        ),
                        SpeakTitle(
                            "Stop Discord Instance?",
                            "Dismiss This Reflection?"
                        ),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2
                    );

                if (confirmation !=
                    DialogResult.Yes)
                {
                    return;
                }

                StopInstanceResult result =
                    await instanceManager
                        .StopInstanceAsync(
                            profileName
                        );

                AppLogger.Info(
                    $"Stop result for \"{profileName}\": " +
                    $"VerifiedBeforeStop={result.VerifiedBeforeStop}, " +
                    $"RemainingVerified={result.RemainingVerifiedPids.Count}, " +
                    $"Uncertain={result.UncertainProcessCount}, " +
                    $"Success={result.Success}."
                );

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
                        result.RemainingVerifiedPids.Count > 0
                            ? string.Join(
                                ", ",
                                result.RemainingVerifiedPids
                            )
                            : "None";

                    MessageBox.Show(
                        "Magic Mirror could not confirm that the entire Discord instance stopped.\n\n" +
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
                AppLogger.Error(
                    $"Stop failed for profile \"{profileName}\".",
                    ex
                );

                MessageBox.Show(
                    $"Magic Mirror could not stop the Discord instance.\n\n{ex.Message}",
                    "Discord Stop Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                EndInstanceOperation();
                RefreshProfiles();
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

        private void btnBackupProfiles_Click(
    object sender,
    EventArgs e)
        {
            List<string>? selectedProfiles =
                PromptForBackupProfiles();

            if (selectedProfiles == null ||
                selectedProfiles.Count == 0)
            {
                return;
            }

            using var folderDialog =
                new FolderBrowserDialog
                {
                    Description = Speak(
                        "Choose where Magic Mirror should create the profile backup.",
                        "Choose where the mirror shall preserve these reflections."
                    ),

                    UseDescriptionForTitle = true,

                    ShowNewFolderButton = true
                };

            if (folderDialog.ShowDialog(this)
                != DialogResult.OK)
            {
                return;
            }

            string destinationRoot =
                folderDialog.SelectedPath;

            if (string.IsNullOrWhiteSpace(
                    destinationRoot))
            {
                return;
            }

            // Never allow backups to be created inside the
            // live Discord Profiles directory.
            if (IsPathInsideOrEqual(
                    destinationRoot,
                    profileBasePath))
            {
                MessageBox.Show(
                    this,
                    Speak(
                        "The backup destination cannot be inside Discord's live Profiles folder.\n\n" +
                        "Choose a different location for the backup.",

                        "The mirror refuses to preserve reflections within the same glass that holds them.\n\n" +
                        "Choose a place beyond the live Reflections folder."
                    ),
                    SpeakTitle(
                        "Invalid Backup Location",
                        "The Mirror Refuses This Place"
                    ),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );

                return;
            }

            // Re-check the selected profiles immediately before copying.
            // A profile that became active must not be backed up.
            instanceManager.RefreshTrackedProcesses();
            instanceManager.CleanupStaleProcesses();

            foreach (string profileName
                in selectedProfiles)
            {
                ProfilePresenceState presence =
                    GetProfilePresenceState(
                        profileName,
                        refreshTracking: false
                    );

                if (presence !=
                    ProfilePresenceState.Dormant)
                {
                    MessageBox.Show(
                        this,
                        Speak(
                            $"The profile \"{profileName}\" is no longer stopped.\n\n" +
                            "The backup was cancelled. Stop the profile and try again.",

                            $"The reflection \"{profileName}\" has stirred within the glass.\n\n" +
                            "The preservation has been abandoned. Dismiss the reflection and try again."
                        ),
                        SpeakTitle(
                            "Backup Cancelled",
                            "The Reflection Stirred"
                        ),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );

                    RefreshProfiles();

                    return;
                }
            }

            DialogResult confirmation =
                MessageBox.Show(
                    this,
                    Speak(
                        $"Back up {selectedProfiles.Count} profile(s) to:\n\n" +
                        $"{destinationRoot}\n\n" +
                        "Magic Mirror will create a new backup folder at this location.\n\n" +
                        "Backups may contain private Discord data, cached content, identifiers, and settings. " +
                        "Store them somewhere you trust.",

                        $"Preserve {selectedProfiles.Count} reflection(s) within:\n\n" +
                        $"{destinationRoot}\n\n" +
                        "The mirror will carve a new vessel for them at this location.\n\n" +
                        "What is preserved may contain private traces of Discord, cached memories, identifiers, and settings. " +
                        "Entrust them only to a place you deem safe."
                    ),
                    SpeakTitle(
                        "Create Profile Backup?",
                        "Preserve These Reflections?"
                    ),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2
                );

            if (confirmation !=
                DialogResult.Yes)
            {
                return;
            }

            AppLogger.Info(
                $"Backup confirmed for {selectedProfiles.Count} profile(s). " +
                $"Profiles: {string.Join(", ", selectedProfiles)}."
            );

            string backupDirectory =
                CreateUniqueBackupDirectory(
                    destinationRoot
                );

            try
            {
                Directory.CreateDirectory(
                    backupDirectory
                );

                foreach (string profileName
                    in selectedProfiles)
                {
                    string sourceDirectory =
                        Path.Combine(
                            profileBasePath,
                            profileName
                        );

                    if (!Directory.Exists(
                            sourceDirectory))
                    {
                        throw new DirectoryNotFoundException(
                            $"The profile \"{profileName}\" could not be found."
                        );
                    }

                    string destinationDirectory =
                        Path.Combine(
                            backupDirectory,
                            profileName
                        );

                    CopyDirectory(
                        sourceDirectory,
                        destinationDirectory
                    );
                }

                AppLogger.Info(
                    $"Backup completed successfully. " +
                    $"{selectedProfiles.Count} profile(s) copied to \"{backupDirectory}\"."
                );

                MessageBox.Show(
                    this,
                    Speak(
                        $"Backup completed successfully.\n\n" +
                        $"{selectedProfiles.Count} profile(s) were copied to:\n\n" +
                        $"{backupDirectory}",

                        $"The preservation is complete.\n\n" +
                        $"{selectedProfiles.Count} reflection(s) now rest within:\n\n" +
                        $"{backupDirectory}"
                    ),
                    SpeakTitle(
                        "Backup Complete",
                        "The Reflections Are Preserved"
                    ),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    $"Backup failed. Partial destination may exist at \"{backupDirectory}\".",
                    ex
                );

                MessageBox.Show(
                    this,
                    Speak(
                        $"Magic Mirror could not complete the backup.\n\n" +
                        $"{ex.Message}\n\n" +
                        "A partial backup may remain at:\n" +
                        $"{backupDirectory}",

                        $"The mirror could not complete the preservation.\n\n" +
                        $"{ex.Message}\n\n" +
                        "Fragments of the attempted preservation may remain within:\n" +
                        $"{backupDirectory}"
                    ),
                    SpeakTitle(
                        "Backup Failed",
                        "The Preservation Failed"
                    ),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }
    }
}