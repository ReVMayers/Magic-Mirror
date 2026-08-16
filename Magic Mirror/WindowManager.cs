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
    }
}