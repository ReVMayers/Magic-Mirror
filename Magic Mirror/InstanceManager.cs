using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    public sealed class InstanceManager
    {
        private const int CurrentStateVersion = 1;

        private readonly string stateDirectory;
        private readonly string stateFilePath;
        private readonly string discordBasePath;

        private InstanceState state;

        private static readonly JsonSerializerOptions JsonOptions =
            new()
            {
                WriteIndented = true
            };

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

        public IReadOnlyList<TrackedInstance> Instances =>
            state.Instances;

        public TrackedInstance? GetInstance(
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

        public void SetInstance(
            TrackedInstance instance)
        {
            TrackedInstance? existing =
                GetInstance(instance.ProfileName);

            if (existing != null)
            {
                state.Instances.Remove(existing);
            }

            state.Instances.Add(instance);

            SaveState();
        }

        public bool RemoveInstance(
            string profileName)
        {
            TrackedInstance? existing =
                GetInstance(profileName);

            if (existing == null)
            {
                return false;
            }

            state.Instances.Remove(existing);

            SaveState();

            return true;
        }

        public void Reload()
        {
            state = LoadState();
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

        public int CleanupStaleProcesses()
        {
            int removedProcesses = 0;
            bool stateChanged = false;

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
                SaveState();
            }

            return removedProcesses;
        }

        public async Task<StopInstanceResult>
            StopInstanceAsync(
                string profileName)
        {
            // Remove anything that is already
            // definitely stale.
            CleanupStaleProcesses();

            TrackedInstance? instance =
                GetInstance(profileName);

            if (instance == null)
            {
                return new StopInstanceResult();
            }

            var verifiedProcesses =
                new List<Process>();

            // Re-verify immediately before doing
            // anything destructive.
            foreach (TrackedProcess trackedProcess
                in instance.Processes.ToList())
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
                // Request termination for every verified
                // Discord process belonging to this instance.
                foreach (Process process
                    in verifiedProcesses)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // Process already exited.
                    }
                    catch (Win32Exception)
                    {
                        // Final verification below will
                        // determine whether it survived.
                    }
                    catch (NotSupportedException)
                    {
                        // Same as above.
                    }
                }

                // Give the processes time to disappear.
                using var timeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(5)
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
                        // Process is already gone.
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

            // Now ask Windows what actually survived.
            CleanupStaleProcesses();

            IReadOnlyList<int>
                remainingVerifiedPids =
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

                if (!normalizedActualPath.StartsWith(
                        discordBasePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return
                        ProcessVerificationStatus.IdentityMismatch;
                }

                if (!string.Equals(
                        Path.GetFileName(
                            normalizedActualPath
                        ),
                        "Discord.exe",
                        StringComparison.OrdinalIgnoreCase))
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
                // Process vanished while being inspected.
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
            catch (System.Security.SecurityException)
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

        private InstanceState LoadState()
        {
            if (!File.Exists(stateFilePath))
            {
                return new InstanceState
                {
                    Version =
                        CurrentStateVersion
                };
            }

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
                    "The instance state file could not be read."
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

            foreach (TrackedInstance instance
                in loadedState.Instances)
            {
                instance.Processes ??=
                    new List<TrackedProcess>();
            }

            return loadedState;
        }
    }
}