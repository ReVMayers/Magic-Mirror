using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Magic_Mirror
{
    public enum ProcessVerificationStatus
    {
        Verified,
        ProcessNotFound,
        IdentityMismatch,
        AccessDenied,
        InspectionFailed
    }

    public sealed class StopInstanceResult
    {
        public int VerifiedBeforeStop { get; init; }

        public IReadOnlyList<int> RemainingVerifiedPids { get; init; }
            = Array.Empty<int>();

        public int UncertainProcessCount { get; init; }

        public bool WasRunning =>
            VerifiedBeforeStop > 0;

        public bool Success =>
            WasRunning &&
            RemainingVerifiedPids.Count == 0 &&
            UncertainProcessCount == 0;
    }

    public sealed class DiscordRecoveryResult
    {
        public int VerifiedBeforeKill { get; init; }

        public int TargetedProcessCount { get; init; }

        public int TargetedGroupCount { get; init; }

        public IReadOnlyList<int> RemainingVerifiedPids { get; init; }
            = Array.Empty<int>();

        public int UncertainProcessCount { get; init; }

        public bool Success =>
            RemainingVerifiedPids.Count == 0 &&
            UncertainProcessCount == 0;
    }

    public sealed class InstanceManager
    {
        private const int CurrentStateVersion = 1;
        private const uint Th32csSnapProcess = 0x00000002;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        private readonly string stateDirectory;
        private readonly string stateFilePath;
        private readonly string discordBasePath;
        private readonly object stateSync = new();

        private InstanceState state;

        private static readonly JsonSerializerOptions JsonOptions =
            new()
            {
                WriteIndented = true
            };

        public string? StateRecoveryWarning { get; private set; }

        public InstanceManager(string discordBasePath)
        {
            this.discordBasePath =
                NormalizeDirectoryPath(discordBasePath);

            stateDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "Magic Mirror"
            );

            stateFilePath = Path.Combine(
                stateDirectory,
                "instances.json"
            );

            state = LoadState();
        }

        public IReadOnlyList<TrackedInstance> Instances
        {
            get
            {
                lock (stateSync)
                {
                    return state.Instances
                        .Select(CloneInstance)
                        .ToList();
                }
            }
        }

        public TrackedInstance? GetInstance(
            string profileName)
        {
            lock (stateSync)
            {
                TrackedInstance? instance =
                    FindInstanceNoLock(profileName);

                return instance == null
                    ? null
                    : CloneInstance(instance);
            }
        }

        public void SetInstance(
            TrackedInstance instance)
        {
            lock (stateSync)
            {
                TrackedInstance? existing =
                    FindInstanceNoLock(instance.ProfileName);

                if (existing != null)
                {
                    state.Instances.Remove(existing);
                }

                state.Instances.Add(
                    CloneInstance(instance)
                );

                SaveStateNoLock();
            }
        }

        public bool RemoveInstance(
            string profileName)
        {
            lock (stateSync)
            {
                TrackedInstance? existing =
                    FindInstanceNoLock(profileName);

                if (existing == null)
                {
                    return false;
                }

                state.Instances.Remove(existing);
                SaveStateNoLock();

                return true;
            }
        }

        public void ClearAllInstances()
        {
            lock (stateSync)
            {
                if (state.Instances.Count == 0)
                {
                    return;
                }

                state.Instances.Clear();
                SaveStateNoLock();
            }
        }

        public void Reload()
        {
            lock (stateSync)
            {
                state = LoadState();
            }
        }

        public ProcessVerificationStatus
            GetProcessVerificationStatus(
                TrackedProcess trackedProcess)
        {
            ProcessVerificationStatus status =
                VerifyTrackedProcess(
                    trackedProcess,
                    out Process? process
                );

            process?.Dispose();

            return status;
        }

        public IReadOnlyList<int>
            GetVerifiedInstalledDiscordProcessIds()
        {
            DiscordProcessScan scan =
                ScanInstalledDiscordProcesses();

            return scan.Processes
                .Select(process => process.ProcessId)
                .ToList();
        }

        public IReadOnlyList<int> GetVerifiedProcessIds(
            string profileName)
        {
            TrackedInstance? instance =
                GetInstance(profileName);

            if (instance == null)
            {
                return Array.Empty<int>();
            }

            var verifiedProcessIds =
                new List<int>();

            foreach (TrackedProcess trackedProcess
                in instance.Processes)
            {
                ProcessVerificationStatus status =
                    VerifyTrackedProcess(
                        trackedProcess,
                        out Process? process
                    );

                if (status !=
                        ProcessVerificationStatus.Verified ||
                    process == null)
                {
                    continue;
                }

                using (process)
                {
                    verifiedProcessIds.Add(
                        process.Id
                    );
                }
            }

            return verifiedProcessIds;
        }

        public bool IsInstanceRunning(
            string profileName)
        {
            return GetVerifiedProcessIds(
                profileName
            ).Count > 0;
        }

        /// <summary>
        /// Expands tracked instances to include newly spawned Discord helper
        /// processes that descend from a currently verified member of the same
        /// tracked process group. This deliberately prefers missing a process
        /// over attaching an unrelated Discord process to the wrong profile.
        /// </summary>
        public int RefreshTrackedProcesses(
            string? profileName = null)
        {
            DiscordProcessScan scan =
                ScanInstalledDiscordProcesses();

            Dictionary<int, int> parentMap =
                BuildParentProcessMap();

            int addedCount = 0;

            lock (stateSync)
            {
                List<TrackedInstance> targetInstances =
                    state.Instances
                        .Where(
                            instance =>
                                profileName == null ||
                                string.Equals(
                                    instance.ProfileName,
                                    profileName,
                                    StringComparison.OrdinalIgnoreCase
                                )
                        )
                        .ToList();

                if (targetInstances.Count == 0 ||
                    scan.Processes.Count == 0)
                {
                    return 0;
                }

                var currentByPid =
                    scan.Processes.ToDictionary(
                        process => process.ProcessId
                    );

                var assignedPids =
                    new HashSet<int>();

                foreach (TrackedProcess trackedProcess
                    in state.Instances
                        .SelectMany(instance => instance.Processes))
                {
                    if (currentByPid.TryGetValue(
                            trackedProcess.ProcessId,
                            out TrackedProcess? currentProcess) &&
                        SameProcessIdentity(
                            trackedProcess,
                            currentProcess))
                    {
                        assignedPids.Add(
                            trackedProcess.ProcessId
                        );
                    }
                }

                foreach (TrackedInstance instance
                    in targetInstances)
                {
                    var verifiedSeedPids =
                        new HashSet<int>();

                    foreach (TrackedProcess trackedProcess
                        in instance.Processes)
                    {
                        if (!currentByPid.TryGetValue(
                                trackedProcess.ProcessId,
                                out TrackedProcess? currentProcess))
                        {
                            continue;
                        }

                        if (SameProcessIdentity(
                                trackedProcess,
                                currentProcess))
                        {
                            verifiedSeedPids.Add(
                                trackedProcess.ProcessId
                            );
                        }
                    }

                    if (verifiedSeedPids.Count == 0)
                    {
                        continue;
                    }

                    DateTime earliestAllowedStart =
                        instance.TrackingStartedUtc
                            .ToUniversalTime()
                            .AddSeconds(-2);

                    foreach (TrackedProcess candidate
                        in scan.Processes)
                    {
                        if (assignedPids.Contains(
                                candidate.ProcessId) ||
                            candidate.StartTimeUtc <
                                earliestAllowedStart)
                        {
                            continue;
                        }

                        if (!IsDescendantOfAny(
                                candidate.ProcessId,
                                verifiedSeedPids,
                                parentMap))
                        {
                            continue;
                        }

                        instance.Processes.Add(
                            CloneTrackedProcess(candidate)
                        );

                        assignedPids.Add(
                            candidate.ProcessId
                        );

                        verifiedSeedPids.Add(
                            candidate.ProcessId
                        );

                        addedCount++;
                    }
                }

                if (addedCount > 0)
                {
                    SaveStateNoLock();
                }
            }

            if (addedCount > 0)
            {
                AppLogger.Info(
                    profileName != null
                        ? $"Tracking refresh adopted {addedCount} new Discord helper process(es) for profile \"{profileName}\"."
                        : $"Tracking refresh adopted {addedCount} new Discord helper process(es) across tracked profiles."
                );
            }

            return addedCount;
        }

        public int CleanupStaleProcesses()
        {
            int removedProcesses = 0;
            bool stateChanged = false;

            lock (stateSync)
            {
                foreach (TrackedInstance instance
                    in state.Instances.ToList())
                {
                    foreach (TrackedProcess trackedProcess
                        in instance.Processes.ToList())
                    {
                        ProcessVerificationStatus status =
                            VerifyTrackedProcess(
                                trackedProcess,
                                out Process? process
                            );

                        process?.Dispose();

                        bool definitelyStale =
                            status ==
                                ProcessVerificationStatus.ProcessNotFound
                            ||
                            status ==
                                ProcessVerificationStatus.IdentityMismatch;

                        if (!definitelyStale)
                        {
                            continue;
                        }

                        instance.Processes.Remove(
                            trackedProcess
                        );

                        removedProcesses++;
                        stateChanged = true;
                    }

                    if (instance.Processes.Count == 0)
                    {
                        state.Instances.Remove(instance);
                        stateChanged = true;
                    }
                }

                if (stateChanged)
                {
                    SaveStateNoLock();
                }
            }

            if (removedProcesses > 0)
            {
                AppLogger.Info(
                    $"Removed {removedProcesses} stale tracked Discord process record(s)."
                );
            }

            return removedProcesses;
        }

        public async Task<TrackedInstance?>
            TrackLaunchedInstanceAsync(
                string profileName,
                IReadOnlyCollection<int> processesBeforeLaunch,
                int launcherProcessId,
                DateTime trackingStartedUtc)
        {
            AppLogger.Info(
                $"PID tracking started for profile \"{profileName}\". " +
                $"Launcher PID={launcherProcessId}, " +
                $"Baseline Discord processes={processesBeforeLaunch.Count}."
            );

            TimeSpan maximumWait =
                TimeSpan.FromSeconds(30);

            TimeSpan settlingTime =
                TimeSpan.FromSeconds(2);

            TimeSpan pollInterval =
                TimeSpan.FromMilliseconds(350);

            var baselinePids =
                new HashSet<int>(processesBeforeLaunch);

            var discovered =
                new Dictionary<int, TrackedProcess>();

            var timer = Stopwatch.StartNew();
            TimeSpan? lastNewProcessTime = null;

            IReadOnlyList<TrackedProcess> lastNewCandidates =
                Array.Empty<TrackedProcess>();

            Dictionary<int, int> lastParentMap =
                new();

            while (timer.Elapsed < maximumWait)
            {
                DiscordProcessScan scan =
                    ScanInstalledDiscordProcesses();

                Dictionary<int, int> parentMap =
                    BuildParentProcessMap();

                DateTime earliestAllowedStart =
                    trackingStartedUtc
                        .ToUniversalTime()
                        .AddSeconds(-2);

                List<TrackedProcess> newCandidates =
                    scan.Processes
                        .Where(
                            process =>
                                !baselinePids.Contains(
                                    process.ProcessId
                                ) &&
                                process.StartTimeUtc >=
                                    earliestAllowedStart
                        )
                        .ToList();

                lastNewCandidates = newCandidates;
                lastParentMap = parentMap;

                var candidatePidSet =
                    new HashSet<int>(
                        newCandidates.Select(
                            process => process.ProcessId
                        )
                    );

                var matchingGroupKeys =
                    new HashSet<string>(
                        StringComparer.Ordinal
                    );

                foreach (TrackedProcess candidate
                    in newCandidates)
                {
                    if (IsDescendantOf(
                            candidate.ProcessId,
                            launcherProcessId,
                            parentMap))
                    {
                        matchingGroupKeys.Add(
                            GetDiscordGroupKey(
                                candidate.ProcessId,
                                candidatePidSet,
                                parentMap
                            )
                        );
                    }
                }

                // Once any member has been tied to the launcher, capture the
                // entire Discord sibling/child group. This picks up Electron
                // helper processes without claiming unrelated Discord groups.
                if (matchingGroupKeys.Count > 0)
                {
                    if (discovered.Count == 0)
                    {
                        AppLogger.Info(
                            $"PID tracking for \"{profileName}\" traced " +
                            $"{matchingGroupKeys.Count} Discord process group(s) " +
                            $"to launcher PID {launcherProcessId}."
                        );
                    }

                    bool foundSomethingNew = false;

                    foreach (TrackedProcess candidate
                        in newCandidates)
                    {
                        string groupKey =
                            GetDiscordGroupKey(
                                candidate.ProcessId,
                                candidatePidSet,
                                parentMap
                            );

                        if (!matchingGroupKeys.Contains(groupKey))
                        {
                            continue;
                        }

                        if (discovered.TryAdd(
                                candidate.ProcessId,
                                CloneTrackedProcess(candidate)))
                        {
                            foundSomethingNew = true;
                        }
                    }

                    if (foundSomethingNew)
                    {
                        lastNewProcessTime =
                            timer.Elapsed;
                    }
                }

                if (discovered.Count > 0 &&
                    lastNewProcessTime.HasValue &&
                    timer.Elapsed -
                        lastNewProcessTime.Value >=
                        settlingTime)
                {
                    break;
                }

                await Task.Delay(
                    pollInterval
                );
            }

            timer.Stop();

            // Some Discord updater versions can make the launcher disappear
            // before the process snapshot catches the parent chain. In that
            // case, use a conservative fallback only when exactly one new
            // genuine Discord process group appeared during the launch window.
            if (discovered.Count == 0 &&
                lastNewCandidates.Count > 0)
            {
                var candidatePidSet =
                    new HashSet<int>(
                        lastNewCandidates.Select(
                            process => process.ProcessId
                        )
                    );

                DateTime latestFallbackStart =
                    trackingStartedUtc
                        .ToUniversalTime()
                        .AddSeconds(15);

                List<IGrouping<string, TrackedProcess>> groups =
                    lastNewCandidates
                        .Where(
                            process =>
                                process.StartTimeUtc <=
                                    latestFallbackStart
                        )
                        .GroupBy(
                            process =>
                                GetDiscordGroupKey(
                                    process.ProcessId,
                                    candidatePidSet,
                                    lastParentMap
                                )
                        )
                        .ToList();

                if (groups.Count == 1)
                {
                    AppLogger.Warning(
                        $"Direct launcher ancestry was not available for \"{profileName}\". " +
                        $"Using the conservative single-group fallback. " +
                        $"Candidate PIDs: {string.Join(", ", groups[0].Select(process => process.ProcessId))}"
                    );

                    foreach (TrackedProcess process
                        in groups[0])
                    {
                        discovered[process.ProcessId] =
                            CloneTrackedProcess(process);
                    }
                }
                else
                {
                    AppLogger.Warning(
                        $"PID tracking fallback for \"{profileName}\" was ambiguous. " +
                        $"Detected {groups.Count} new Discord process group(s)."
                    );
                }
            }

            if (discovered.Count == 0)
            {
                AppLogger.Warning(
                    $"PID tracking finished for \"{profileName}\" without a safe process association."
                );

                return null;
            }

            List<TrackedProcess> finalProcesses =
                discovered.Values
                    .OrderBy(
                        process =>
                            process.StartTimeUtc
                    )
                    .ToList();

            AppLogger.Info(
                $"PID tracking completed for \"{profileName}\". " +
                $"Tracked {finalProcesses.Count} process(es). " +
                $"PIDs: {string.Join(", ", finalProcesses.Select(process => process.ProcessId))}"
            );

            return new TrackedInstance
            {
                ProfileName = profileName,
                TrackingStartedUtc =
                    trackingStartedUtc.ToUniversalTime(),
                Processes = finalProcesses
            };
        }

        public async Task<StopInstanceResult>
            StopInstanceAsync(
                string profileName)
        {
            RefreshTrackedProcesses(profileName);
            CleanupStaleProcesses();

            TrackedInstance? instance =
                GetInstance(profileName);

            if (instance == null)
            {
                return new StopInstanceResult();
            }

            var verifiedProcesses =
                new List<Process>();

            var lineageProcessIds =
                new HashSet<int>();

            foreach (TrackedProcess trackedProcess
                in instance.Processes)
            {
                ProcessVerificationStatus status =
                    VerifyTrackedProcess(
                        trackedProcess,
                        out Process? process
                    );

                if (status ==
                        ProcessVerificationStatus.Verified &&
                    process != null)
                {
                    verifiedProcesses.Add(process);
                    lineageProcessIds.Add(
                        process.Id
                    );
                }
            }

            int verifiedBeforeStop =
                verifiedProcesses.Count;

            if (verifiedBeforeStop == 0)
            {
                return new StopInstanceResult
                {
                    VerifiedBeforeStop = 0,
                    UncertainProcessCount =
                        CountUncertainProcesses(
                            profileName
                        )
                };
            }

            try
            {
                foreach (Process process
                    in verifiedProcesses)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (Win32Exception)
                    {
                    }
                    catch (NotSupportedException)
                    {
                    }
                }

                using var timeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(3)
                    );

                foreach (Process process
                    in verifiedProcesses)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            await process.WaitForExitAsync(
                                timeout.Token
                            );
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
            finally
            {
                foreach (Process process
                    in verifiedProcesses)
                {
                    process.Dispose();
                }
            }

            // Electron may spawn or replace helpers while the original members
            // are being terminated. Follow the verified parent/child lineage for
            // a short bounded period and terminate newly appearing descendants
            // as well. We do not adopt unrelated Discord process groups.
            DateTime earliestAllowedStart =
                instance.TrackingStartedUtc
                    .ToUniversalTime()
                    .AddSeconds(-2);

            var stopTimer = Stopwatch.StartNew();
            TimeSpan? quietSince = null;

            while (stopTimer.Elapsed <
                TimeSpan.FromSeconds(5))
            {
                DiscordProcessScan scan =
                    ScanInstalledDiscordProcesses();

                Dictionary<int, int> parentMap =
                    BuildParentProcessMap();

                List<TrackedProcess> lineageProcesses =
                    scan.Processes
                        .Where(
                            process =>
                                process.StartTimeUtc >=
                                    earliestAllowedStart &&
                                (
                                    lineageProcessIds.Contains(
                                        process.ProcessId
                                    ) ||
                                    IsDescendantOfAny(
                                        process.ProcessId,
                                        lineageProcessIds,
                                        parentMap
                                    )
                                )
                        )
                        .ToList();

                bool foundNewLineageMember = false;

                foreach (TrackedProcess process
                    in lineageProcesses)
                {
                    if (lineageProcessIds.Add(
                            process.ProcessId))
                    {
                        foundNewLineageMember = true;
                    }
                }

                if (lineageProcesses.Count == 0)
                {
                    quietSince ??=
                        stopTimer.Elapsed;

                    if (stopTimer.Elapsed -
                            quietSince.Value >=
                        TimeSpan.FromMilliseconds(500))
                    {
                        break;
                    }

                    await Task.Delay(100);
                    continue;
                }

                quietSince = null;

                MergeTrackedProcesses(
                    profileName,
                    lineageProcesses
                );

                await KillVerifiedProcessesAsync(
                    lineageProcesses
                );

                if (!foundNewLineageMember)
                {
                    await Task.Delay(100);
                }
            }

            stopTimer.Stop();

            // Preserve any verified descendants that survived the stop attempt
            // in the tracking record so the UI reports failure rather than
            // incorrectly declaring the profile dormant.
            DiscordProcessScan finalLineageScan =
                ScanInstalledDiscordProcesses();

            Dictionary<int, int> finalParentMap =
                BuildParentProcessMap();

            List<TrackedProcess> finalLineageProcesses =
                finalLineageScan.Processes
                    .Where(
                        process =>
                            process.StartTimeUtc >=
                                earliestAllowedStart &&
                            (
                                lineageProcessIds.Contains(
                                    process.ProcessId
                                ) ||
                                IsDescendantOfAny(
                                    process.ProcessId,
                                    lineageProcessIds,
                                    finalParentMap
                                )
                            )
                    )
                    .ToList();

            MergeTrackedProcesses(
                profileName,
                finalLineageProcesses
            );

            CleanupStaleProcesses();

            IReadOnlyList<int> remainingVerifiedPids =
                GetVerifiedProcessIds(
                    profileName
                );

            int uncertainProcessCount =
                CountUncertainProcesses(
                    profileName
                );

            return new StopInstanceResult
            {
                VerifiedBeforeStop =
                    verifiedBeforeStop,
                RemainingVerifiedPids =
                    remainingVerifiedPids,
                UncertainProcessCount =
                    uncertainProcessCount
            };
        }

        public async Task<DiscordRecoveryResult>
            NukeAllDiscordInstancesAsync()
        {
            var timer = Stopwatch.StartNew();
            var targetedPids = new HashSet<int>();
            var targetedGroupKeys =
                new HashSet<string>(
                    StringComparer.Ordinal
                );

            DiscordProcessScan initialScan =
                ScanInstalledDiscordProcesses();

            int verifiedBeforeKill =
                initialScan.Processes.Count;

            while (timer.Elapsed <
                TimeSpan.FromSeconds(5))
            {
                DiscordProcessScan scan =
                    ScanInstalledDiscordProcesses();

                if (scan.Processes.Count == 0)
                {
                    break;
                }

                Dictionary<int, int> parentMap =
                    BuildParentProcessMap();

                foreach (DiscordProcessGroup group
                    in BuildDiscordGroups(
                        scan.Processes,
                        parentMap
                    ))
                {
                    targetedGroupKeys.Add(
                        group.Key
                    );
                }

                foreach (TrackedProcess process
                    in scan.Processes)
                {
                    targetedPids.Add(
                        process.ProcessId
                    );
                }

                await KillVerifiedProcessesAsync(
                    scan.Processes
                );

                await Task.Delay(150);
            }

            timer.Stop();

            DiscordProcessScan finalScan =
                ScanInstalledDiscordProcesses();

            // Nuke All is an emergency reset. Tracking is discarded whether
            // or not Windows denied access to a process. Profile data is never
            // touched here.
            ClearAllInstances();

            return new DiscordRecoveryResult
            {
                VerifiedBeforeKill =
                    verifiedBeforeKill,
                TargetedProcessCount =
                    targetedPids.Count,
                TargetedGroupCount =
                    targetedGroupKeys.Count,
                RemainingVerifiedPids =
                    finalScan.Processes
                        .Select(process => process.ProcessId)
                        .OrderBy(id => id)
                        .ToList(),
                UncertainProcessCount =
                    finalScan.UncertainProcessCount
            };
        }

        public async Task<DiscordRecoveryResult>
            NukeNonVisibleDiscordInstancesAsync()
        {
            var timer = Stopwatch.StartNew();
            var targetedPids = new HashSet<int>();
            var targetedGroupKeys =
                new HashSet<string>(
                    StringComparer.Ordinal
                );

            int verifiedBeforeKill = 0;
            bool recordedInitialTargets = false;

            while (timer.Elapsed <
                TimeSpan.FromSeconds(5))
            {
                DiscordProcessScan scan =
                    ScanInstalledDiscordProcesses();

                Dictionary<int, int> parentMap =
                    BuildParentProcessMap();

                List<DiscordProcessGroup> groups =
                    BuildDiscordGroups(
                        scan.Processes,
                        parentMap
                    );

                List<DiscordProcessGroup> headlessGroups =
                    groups
                        .Where(
                            group =>
                                !WindowManager.HasUsableWindow(
                                    group.Processes.Select(
                                        process => process.ProcessId
                                    )
                                )
                        )
                        .ToList();

                if (!recordedInitialTargets)
                {
                    verifiedBeforeKill =
                        headlessGroups.Sum(
                            group => group.Processes.Count
                        );

                    recordedInitialTargets = true;
                }

                List<TrackedProcess> targets =
                    headlessGroups
                        .SelectMany(group => group.Processes)
                        .ToList();

                if (targets.Count == 0)
                {
                    break;
                }

                foreach (DiscordProcessGroup group
                    in headlessGroups)
                {
                    targetedGroupKeys.Add(group.Key);

                    foreach (TrackedProcess process
                        in group.Processes)
                    {
                        targetedPids.Add(
                            process.ProcessId
                        );
                    }
                }

                await KillVerifiedProcessesAsync(
                    targets
                );

                await Task.Delay(150);
            }

            timer.Stop();

            RefreshTrackedProcesses();
            CleanupStaleProcesses();

            DiscordProcessScan finalScan =
                ScanInstalledDiscordProcesses();

            List<DiscordProcessGroup> finalHeadlessGroups =
                BuildDiscordGroups(
                    finalScan.Processes,
                    BuildParentProcessMap()
                )
                .Where(
                    group =>
                        !WindowManager.HasUsableWindow(
                            group.Processes.Select(
                                process => process.ProcessId
                            )
                        )
                )
                .ToList();

            return new DiscordRecoveryResult
            {
                VerifiedBeforeKill =
                    verifiedBeforeKill,
                TargetedProcessCount =
                    targetedPids.Count,
                TargetedGroupCount =
                    targetedGroupKeys.Count,
                RemainingVerifiedPids =
                    finalHeadlessGroups
                        .SelectMany(group => group.Processes)
                        .Select(process => process.ProcessId)
                        .Distinct()
                        .OrderBy(id => id)
                        .ToList(),
                UncertainProcessCount =
                    finalScan.UncertainProcessCount
            };
        }

        private async Task KillVerifiedProcessesAsync(
            IReadOnlyCollection<TrackedProcess> trackedProcesses)
        {
            var killedProcesses =
                new List<Process>();

            try
            {
                foreach (TrackedProcess trackedProcess
                    in trackedProcesses)
                {
                    ProcessVerificationStatus status =
                        VerifyTrackedProcess(
                            trackedProcess,
                            out Process? process
                        );

                    if (status !=
                            ProcessVerificationStatus.Verified ||
                        process == null)
                    {
                        continue;
                    }

                    try
                    {
                        process.Kill();
                        killedProcesses.Add(process);
                    }
                    catch (InvalidOperationException)
                    {
                        process.Dispose();
                    }
                    catch (Win32Exception)
                    {
                        process.Dispose();
                    }
                    catch (NotSupportedException)
                    {
                        process.Dispose();
                    }
                }

                using var timeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(2)
                    );

                foreach (Process process
                    in killedProcesses)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            await process.WaitForExitAsync(
                                timeout.Token
                            );
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
            finally
            {
                foreach (Process process
                    in killedProcesses)
                {
                    process.Dispose();
                }
            }
        }

        private void MergeTrackedProcesses(
            string profileName,
            IEnumerable<TrackedProcess> processes)
        {
            lock (stateSync)
            {
                TrackedInstance? instance =
                    FindInstanceNoLock(
                        profileName
                    );

                if (instance == null)
                {
                    return;
                }

                bool changed = false;

                foreach (TrackedProcess process
                    in processes)
                {
                    TrackedProcess? existing =
                        instance.Processes
                            .FirstOrDefault(
                                trackedProcess =>
                                    trackedProcess.ProcessId ==
                                        process.ProcessId
                            );

                    if (existing == null)
                    {
                        instance.Processes.Add(
                            CloneTrackedProcess(process)
                        );
                        changed = true;
                        continue;
                    }

                    if (SameProcessIdentity(
                            existing,
                            process))
                    {
                        continue;
                    }

                    instance.Processes.Remove(
                        existing
                    );
                    instance.Processes.Add(
                        CloneTrackedProcess(process)
                    );
                    changed = true;
                }

                if (changed)
                {
                    SaveStateNoLock();
                }
            }
        }

        private int CountUncertainProcesses(
            string profileName)
        {
            TrackedInstance? instance =
                GetInstance(profileName);

            if (instance == null)
            {
                return 0;
            }

            int count = 0;

            foreach (TrackedProcess trackedProcess
                in instance.Processes)
            {
                ProcessVerificationStatus status =
                    GetProcessVerificationStatus(
                        trackedProcess
                    );

                if (status ==
                        ProcessVerificationStatus.AccessDenied ||
                    status ==
                        ProcessVerificationStatus.InspectionFailed)
                {
                    count++;
                }
            }

            return count;
        }

        private DiscordProcessScan
            ScanInstalledDiscordProcesses()
        {
            var verifiedProcesses =
                new List<TrackedProcess>();

            int uncertainProcessCount = 0;

            Process[] processes =
                Process.GetProcessesByName(
                    "Discord"
                );

            foreach (Process process
                in processes)
            {
                using (process)
                {
                    ProcessVerificationStatus status =
                        CaptureInstalledDiscordProcess(
                            process,
                            out TrackedProcess? trackedProcess
                        );

                    if (status ==
                            ProcessVerificationStatus.Verified &&
                        trackedProcess != null)
                    {
                        verifiedProcesses.Add(
                            trackedProcess
                        );
                    }
                    else if (status ==
                                ProcessVerificationStatus.AccessDenied ||
                             status ==
                                ProcessVerificationStatus.InspectionFailed)
                    {
                        uncertainProcessCount++;
                    }
                }
            }

            return new DiscordProcessScan(
                verifiedProcesses,
                uncertainProcessCount
            );
        }

        private ProcessVerificationStatus
            CaptureInstalledDiscordProcess(
                Process process,
                out TrackedProcess? trackedProcess)
        {
            trackedProcess = null;

            try
            {
                if (!string.Equals(
                        process.ProcessName,
                        "Discord",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return
                        ProcessVerificationStatus.IdentityMismatch;
                }

                string? executablePath =
                    process.MainModule?.FileName;

                if (string.IsNullOrWhiteSpace(
                        executablePath))
                {
                    return
                        ProcessVerificationStatus.InspectionFailed;
                }

                if (!TryNormalizeFilePath(
                        executablePath,
                        out string normalizedExecutablePath))
                {
                    return
                        ProcessVerificationStatus.InspectionFailed;
                }

                if (!IsDiscordExecutablePath(
                        normalizedExecutablePath))
                {
                    return
                        ProcessVerificationStatus.IdentityMismatch;
                }

                trackedProcess = new TrackedProcess
                {
                    ProcessId = process.Id,
                    StartTimeUtc =
                        process.StartTime.ToUniversalTime(),
                    ExecutablePath =
                        normalizedExecutablePath
                };

                return
                    ProcessVerificationStatus.Verified;
            }
            catch (InvalidOperationException)
            {
                return
                    ProcessVerificationStatus.ProcessNotFound;
            }
            catch (Win32Exception ex)
                when (ex.NativeErrorCode == 5)
            {
                return
                    ProcessVerificationStatus.AccessDenied;
            }
            catch (UnauthorizedAccessException)
            {
                return
                    ProcessVerificationStatus.AccessDenied;
            }
            catch (SecurityException)
            {
                return
                    ProcessVerificationStatus.AccessDenied;
            }
            catch (Win32Exception)
            {
                return
                    ProcessVerificationStatus.InspectionFailed;
            }
            catch (NotSupportedException)
            {
                return
                    ProcessVerificationStatus.InspectionFailed;
            }
            catch (IOException)
            {
                return
                    ProcessVerificationStatus.InspectionFailed;
            }
        }

        private ProcessVerificationStatus
            VerifyTrackedProcess(
                TrackedProcess trackedProcess,
                out Process? process)
        {
            process = null;
            bool verified = false;

            if (trackedProcess.ProcessId <= 0)
            {
                return
                    ProcessVerificationStatus.IdentityMismatch;
            }

            if (string.IsNullOrWhiteSpace(
                    trackedProcess.ExecutablePath))
            {
                return
                    ProcessVerificationStatus.IdentityMismatch;
            }

            try
            {
                try
                {
                    process =
                        Process.GetProcessById(
                            trackedProcess.ProcessId
                        );
                }
                catch (ArgumentException)
                {
                    return
                        ProcessVerificationStatus.ProcessNotFound;
                }

                if (!string.Equals(
                        process.ProcessName,
                        "Discord",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return
                        ProcessVerificationStatus.IdentityMismatch;
                }

                DateTime actualStartTimeUtc =
                    process.StartTime.ToUniversalTime();

                DateTime storedStartTimeUtc =
                    trackedProcess
                        .StartTimeUtc
                        .ToUniversalTime();

                if (actualStartTimeUtc.Ticks !=
                    storedStartTimeUtc.Ticks)
                {
                    return
                        ProcessVerificationStatus.IdentityMismatch;
                }

                string? actualExecutablePath =
                    process.MainModule?.FileName;

                if (string.IsNullOrWhiteSpace(
                        actualExecutablePath))
                {
                    return
                        ProcessVerificationStatus.InspectionFailed;
                }

                if (!TryNormalizeFilePath(
                        actualExecutablePath,
                        out string normalizedActualPath))
                {
                    return
                        ProcessVerificationStatus.InspectionFailed;
                }

                if (!TryNormalizeFilePath(
                        trackedProcess.ExecutablePath,
                        out string normalizedStoredPath))
                {
                    return
                        ProcessVerificationStatus.IdentityMismatch;
                }

                if (!IsDiscordExecutablePath(
                        normalizedActualPath))
                {
                    return
                        ProcessVerificationStatus.IdentityMismatch;
                }

                if (!string.Equals(
                        normalizedActualPath,
                        normalizedStoredPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return
                        ProcessVerificationStatus.IdentityMismatch;
                }

                verified = true;

                return
                    ProcessVerificationStatus.Verified;
            }
            catch (InvalidOperationException)
            {
                return
                    ProcessVerificationStatus.ProcessNotFound;
            }
            catch (Win32Exception ex)
                when (ex.NativeErrorCode == 5)
            {
                return
                    ProcessVerificationStatus.AccessDenied;
            }
            catch (UnauthorizedAccessException)
            {
                return
                    ProcessVerificationStatus.AccessDenied;
            }
            catch (SecurityException)
            {
                return
                    ProcessVerificationStatus.AccessDenied;
            }
            catch (Win32Exception)
            {
                return
                    ProcessVerificationStatus.InspectionFailed;
            }
            catch (NotSupportedException)
            {
                return
                    ProcessVerificationStatus.InspectionFailed;
            }
            catch (IOException)
            {
                return
                    ProcessVerificationStatus.InspectionFailed;
            }
            finally
            {
                if (!verified)
                {
                    process?.Dispose();
                    process = null;
                }
            }
        }

        private bool IsDiscordExecutablePath(
            string normalizedExecutablePath)
        {
            return normalizedExecutablePath.StartsWith(
                       discordBasePath,
                       StringComparison.OrdinalIgnoreCase
                   ) &&
                   string.Equals(
                       Path.GetFileName(
                           normalizedExecutablePath
                       ),
                       "Discord.exe",
                       StringComparison.OrdinalIgnoreCase
                   );
        }

        private static bool SameProcessIdentity(
            TrackedProcess left,
            TrackedProcess right)
        {
            return left.ProcessId == right.ProcessId &&
                   left.StartTimeUtc.ToUniversalTime().Ticks ==
                       right.StartTimeUtc.ToUniversalTime().Ticks &&
                   string.Equals(
                       left.ExecutablePath,
                       right.ExecutablePath,
                       StringComparison.OrdinalIgnoreCase
                   );
        }

        private static bool IsDescendantOfAny(
            int processId,
            HashSet<int> ancestorProcessIds,
            Dictionary<int, int> parentMap)
        {
            foreach (int ancestorProcessId
                in ancestorProcessIds)
            {
                if (processId == ancestorProcessId)
                {
                    continue;
                }

                if (IsDescendantOf(
                        processId,
                        ancestorProcessId,
                        parentMap))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDescendantOf(
            int processId,
            int ancestorProcessId,
            Dictionary<int, int> parentMap)
        {
            if (processId <= 0 ||
                ancestorProcessId <= 0 ||
                processId == ancestorProcessId)
            {
                return false;
            }

            var visited = new HashSet<int>();
            int current = processId;

            while (visited.Add(current) &&
                   parentMap.TryGetValue(
                       current,
                       out int parentProcessId))
            {
                if (parentProcessId ==
                    ancestorProcessId)
                {
                    return true;
                }

                if (parentProcessId <= 0 ||
                    parentProcessId == current)
                {
                    break;
                }

                current = parentProcessId;
            }

            return false;
        }

        private static int CountDiscordGroups(
            IReadOnlyList<TrackedProcess> processes,
            Dictionary<int, int> parentMap)
        {
            return BuildDiscordGroups(
                    processes,
                    parentMap
                )
                .Count;
        }

        private static List<DiscordProcessGroup>
            BuildDiscordGroups(
                IReadOnlyList<TrackedProcess> processes,
                Dictionary<int, int> parentMap)
        {
            if (processes.Count == 0)
            {
                return new List<DiscordProcessGroup>();
            }

            var processIds =
                new HashSet<int>(
                    processes.Select(
                        process => process.ProcessId
                    )
                );

            return processes
                .GroupBy(
                    process =>
                        GetDiscordGroupKey(
                            process.ProcessId,
                            processIds,
                            parentMap
                        )
                )
                .Select(
                    group =>
                        new DiscordProcessGroup(
                            group.Key,
                            group.ToList()
                        )
                )
                .ToList();
        }

        private static string GetDiscordGroupKey(
            int processId,
            HashSet<int> discordProcessIds,
            Dictionary<int, int> parentMap)
        {
            int current = processId;
            var visited = new HashSet<int>();

            while (visited.Add(current) &&
                   parentMap.TryGetValue(
                       current,
                       out int parentProcessId) &&
                   discordProcessIds.Contains(
                       parentProcessId))
            {
                current = parentProcessId;
            }

            if (parentMap.TryGetValue(
                    current,
                    out int externalParentProcessId) &&
                externalParentProcessId > 0)
            {
                // Discord roots launched by the same updater/launcher belong
                // together even when the updater has already exited.
                return $"parent:{externalParentProcessId}";
            }

            return $"root:{current}";
        }

        private static Dictionary<int, int>
            BuildParentProcessMap()
        {
            var parentMap =
                new Dictionary<int, int>();

            IntPtr snapshot =
                CreateToolhelp32Snapshot(
                    Th32csSnapProcess,
                    0
                );

            if (snapshot == InvalidHandleValue)
            {
                return parentMap;
            }

            try
            {
                var entry = new PROCESSENTRY32
                {
                    dwSize =
                        (uint)Marshal.SizeOf<PROCESSENTRY32>(),
                    szExeFile = string.Empty
                };

                if (!Process32First(
                        snapshot,
                        ref entry))
                {
                    return parentMap;
                }

                do
                {
                    if (entry.th32ProcessID > 0)
                    {
                        parentMap[
                            unchecked((int)entry.th32ProcessID)
                        ] =
                            unchecked((int)entry.th32ParentProcessID);
                    }

                    entry.dwSize =
                        (uint)Marshal.SizeOf<PROCESSENTRY32>();
                }
                while (Process32Next(
                    snapshot,
                    ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return parentMap;
        }

        private static bool TryNormalizeFilePath(
            string path,
            out string normalizedPath)
        {
            try
            {
                normalizedPath =
                    Path.GetFullPath(path);

                return true;
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (IOException)
            {
            }

            normalizedPath = string.Empty;

            return false;
        }

        private static string NormalizeDirectoryPath(
            string path)
        {
            return Path.GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                )
                + Path.DirectorySeparatorChar;
        }

        public void SaveState()
        {
            lock (stateSync)
            {
                SaveStateNoLock();
            }
        }

        private void SaveStateNoLock()
        {
            Directory.CreateDirectory(
                stateDirectory
            );

            string json =
                JsonSerializer.Serialize(
                    state,
                    JsonOptions
                );

            string tempFilePath =
                stateFilePath + ".tmp";

            try
            {
                File.WriteAllText(
                    tempFilePath,
                    json
                );

                File.Move(
                    tempFilePath,
                    stateFilePath,
                    true
                );
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch
                {
                }
            }
        }

        private InstanceState LoadState()
        {
            StateRecoveryWarning = null;

            if (!File.Exists(stateFilePath))
            {
                return CreateEmptyState();
            }

            try
            {
                string json =
                    File.ReadAllText(
                        stateFilePath
                    );

                InstanceState? loadedState =
                    JsonSerializer.Deserialize<InstanceState>(
                        json,
                        JsonOptions
                    );

                if (loadedState == null)
                {
                    throw new InvalidDataException(
                        "The instance state file contained no usable state."
                    );
                }

                if (loadedState.Version !=
                    CurrentStateVersion)
                {
                    throw new InvalidDataException(
                        $"Unsupported instance state version: {loadedState.Version}"
                    );
                }

                loadedState.Instances ??=
                    new List<TrackedInstance>();

                var profileNames =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase
                    );

                foreach (TrackedInstance? instance
                    in loadedState.Instances)
                {
                    if (instance == null ||
                        string.IsNullOrWhiteSpace(
                            instance.ProfileName))
                    {
                        throw new InvalidDataException(
                            "The instance state contains an invalid profile record."
                        );
                    }

                    if (!profileNames.Add(
                            instance.ProfileName))
                    {
                        throw new InvalidDataException(
                            $"The instance state contains duplicate tracking records for profile '{instance.ProfileName}'."
                        );
                    }

                    instance.Processes ??=
                        new List<TrackedProcess>();

                    if (instance.Processes.Any(
                            process => process == null))
                    {
                        throw new InvalidDataException(
                            $"The instance state for profile '{instance.ProfileName}' contains an invalid process record."
                        );
                    }

                    foreach (TrackedProcess process
                        in instance.Processes)
                    {
                        process.ExecutablePath ??=
                            string.Empty;
                    }
                }

                return loadedState;
            }
            catch (Exception ex)
                when (ex is JsonException ||
                      ex is InvalidDataException ||
                      ex is IOException ||
                      ex is UnauthorizedAccessException ||
                      ex is NotSupportedException ||
                      ex is SecurityException)
            {
                string? archivedPath =
                    TryArchiveCorruptStateFile();

                AppLogger.Error(
                    archivedPath != null
                        ? $"instances.json could not be loaded and was archived to \"{archivedPath}\"."
                        : "instances.json could not be loaded and could not be archived.",
                    ex
                );

                StateRecoveryWarning =
                    archivedPath != null
                        ? "Magic Mirror could not read instances.json. " +
                          "The damaged state file was archived as:\n\n" +
                          archivedPath +
                          "\n\nTracking has been reset. Profile data was not changed."
                        : "Magic Mirror could not read instances.json. " +
                          "Tracking has been reset, but the damaged file could not be archived.\n\n" +
                          ex.Message +
                          "\n\nProfile data was not changed.";

                return CreateEmptyState();
            }
        }

        private string? TryArchiveCorruptStateFile()
        {
            try
            {
                if (!File.Exists(stateFilePath))
                {
                    return null;
                }

                Directory.CreateDirectory(
                    stateDirectory
                );

                string timestamp =
                    DateTime.UtcNow.ToString(
                        "yyyyMMdd-HHmmssfff"
                    );

                string archivePath =
                    Path.Combine(
                        stateDirectory,
                        $"instances.corrupt.{timestamp}.json"
                    );

                File.Move(
                    stateFilePath,
                    archivePath,
                    false
                );

                return archivePath;
            }
            catch
            {
                return null;
            }
        }

        private static InstanceState CreateEmptyState()
        {
            return new InstanceState
            {
                Version = CurrentStateVersion,
                Instances = new List<TrackedInstance>()
            };
        }

        private TrackedInstance? FindInstanceNoLock(
            string profileName)
        {
            return state.Instances.FirstOrDefault(
                instance =>
                    string.Equals(
                        instance.ProfileName,
                        profileName,
                        StringComparison.OrdinalIgnoreCase
                    )
            );
        }

        private static TrackedInstance CloneInstance(
            TrackedInstance instance)
        {
            return new TrackedInstance
            {
                ProfileName = instance.ProfileName,
                TrackingStartedUtc =
                    instance.TrackingStartedUtc,
                Processes = instance.Processes
                    .Select(CloneTrackedProcess)
                    .ToList()
            };
        }

        private static TrackedProcess CloneTrackedProcess(
            TrackedProcess process)
        {
            return new TrackedProcess
            {
                ProcessId = process.ProcessId,
                StartTimeUtc = process.StartTimeUtc,
                ExecutablePath = process.ExecutablePath
            };
        }

        private sealed class DiscordProcessScan
        {
            public DiscordProcessScan(
                List<TrackedProcess> processes,
                int uncertainProcessCount)
            {
                Processes = processes;
                UncertainProcessCount =
                    uncertainProcessCount;
            }

            public List<TrackedProcess> Processes { get; }

            public int UncertainProcessCount { get; }
        }

        private sealed class DiscordProcessGroup
        {
            public DiscordProcessGroup(
                string key,
                List<TrackedProcess> processes)
            {
                Key = key;
                Processes = processes;
            }

            public string Key { get; }

            public List<TrackedProcess> Processes { get; }
        }

        [StructLayout(
            LayoutKind.Sequential,
            CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;

            [MarshalAs(
                UnmanagedType.ByValTStr,
                SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport(
            "kernel32.dll",
            SetLastError = true)]
        private static extern IntPtr
            CreateToolhelp32Snapshot(
                uint dwFlags,
                uint th32ProcessID
            );

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(
            IntPtr hSnapshot,
            ref PROCESSENTRY32 lppe
        );

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(
            IntPtr hSnapshot,
            ref PROCESSENTRY32 lppe
        );

        [DllImport(
            "kernel32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(
            IntPtr hObject
        );
    }
}