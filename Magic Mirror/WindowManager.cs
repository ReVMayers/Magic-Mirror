using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Magic_Mirror
{
    public static class WindowManager
    {
        private delegate bool EnumWindowsProc(
            IntPtr hWnd,
            IntPtr lParam
        );

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(
            EnumWindowsProc lpEnumFunc,
            IntPtr lParam
        );

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr hWnd,
            out uint lpdwProcessId
        );

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(
            IntPtr hWnd
        );

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(
            IntPtr hWnd
        );

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(
            IntPtr hWnd,
            out RECT lpRect
        );

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(
            IntPtr hWnd
        );

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(
            IntPtr hWnd
        );

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(
            IntPtr hWnd,
            int nCmdShow
        );

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(
            uint idAttach,
            uint idAttachTo,
            bool fAttach
        );

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width =>
                Right - Left;

            public int Height =>
                Bottom - Top;
        }

        public static IntPtr FindUsableWindow(
            IEnumerable<int> processIds)
        {
            var targetProcessIds =
                new HashSet<uint>();

            foreach (int processId in processIds)
            {
                if (processId > 0)
                {
                    targetProcessIds.Add(
                        (uint)processId
                    );
                }
            }

            if (targetProcessIds.Count == 0)
            {
                return IntPtr.Zero;
            }

            IntPtr foundWindow =
                IntPtr.Zero;

            EnumWindows(
                (hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(
                        hWnd,
                        out uint processId
                    );

                    if (!targetProcessIds.Contains(
                            processId))
                    {
                        return true;
                    }

                    bool visible =
                        IsWindowVisible(hWnd);

                    bool minimized =
                        IsIconic(hWnd);

                    // A hidden internal Electron window
                    // should not count as a usable Discord window.
                    if (!visible && !minimized)
                    {
                        return true;
                    }

                    if (!GetWindowRect(
                            hWnd,
                            out RECT rect))
                    {
                        return true;
                    }

                    if (rect.Width <= 0 ||
                        rect.Height <= 0)
                    {
                        return true;
                    }

                    foundWindow = hWnd;

                    // Stop enumerating once we've found
                    // a usable top-level window.
                    return false;
                },
                IntPtr.Zero
            );

            return foundWindow;
        }

        public static bool HasUsableWindow(
            IEnumerable<int> processIds)
        {
            return FindUsableWindow(processIds)
                != IntPtr.Zero;
        }

        public static void BringWindowToForeground(
            IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            const int SwRestore = 9;

            if (IsIconic(hWnd))
            {
                ShowWindow(
                    hWnd,
                    SwRestore
                );
            }

            IntPtr foregroundWindow =
                GetForegroundWindow();

            uint currentThreadId =
                GetCurrentThreadId();

            uint foregroundThreadId = 0;

            if (foregroundWindow != IntPtr.Zero)
            {
                foregroundThreadId =
                    GetWindowThreadProcessId(
                        foregroundWindow,
                        out _
                    );
            }

            bool attached = false;

            try
            {
                if (foregroundThreadId != 0 &&
                    foregroundThreadId !=
                        currentThreadId)
                {
                    attached =
                        AttachThreadInput(
                            currentThreadId,
                            foregroundThreadId,
                            true
                        );
                }

                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(
                        currentThreadId,
                        foregroundThreadId,
                        false
                    );
                }
            }
        }
    }
}