using System;
using System.Collections.Generic;

namespace Magic_Mirror
{
    public sealed class TrackedProcess
    {
        public int ProcessId { get; set; }

        public DateTime StartTimeUtc { get; set; }

        public string ExecutablePath { get; set; } = string.Empty;
    }

    public sealed class TrackedInstance
    {
        public string ProfileName { get; set; } = string.Empty;

        public DateTime TrackingStartedUtc { get; set; }

        public List<TrackedProcess> Processes { get; set; } = new();
    }

    public sealed class InstanceState
    {
        public int Version { get; set; } = 1;

        public List<TrackedInstance> Instances { get; set; } = new();
    }
}