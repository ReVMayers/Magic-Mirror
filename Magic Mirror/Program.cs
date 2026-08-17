using System;
using System.Threading;
using System.Windows.Forms;

namespace Magic_Mirror
{
    internal static class Program
    {
        private const string SingleInstanceMutexName =
            @"Local\MagicMirror.SingleInstance.v1";

        private const string ActivationEventName =
            @"Local\MagicMirror.Activate.v1";

        [STAThread]
        static void Main()
        {
            using var singleInstanceMutex =
                new Mutex(
                    true,
                    SingleInstanceMutexName,
                    out bool isFirstInstance
                );

            if (!isFirstInstance)
            {
                SignalExistingInstance();
                return;
            }

            using var activationEvent =
                new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    ActivationEventName
                );

            ApplicationConfiguration.Initialize();

            AppLogger.Initialize();

            AppLogger.Info(
                "Application initialization completed."
            );

            AppLogger.Info(
                "Primary Magic Mirror instance acquired."
            );

            using var mainForm =
                new MainForm();

            RegisteredWaitHandle activationRegistration =
                ThreadPool.RegisterWaitForSingleObject(
                    activationEvent,
                    (state, timedOut) =>
                    {
                        if (timedOut ||
                            mainForm.IsDisposed ||
                            !mainForm.IsHandleCreated)
                        {
                            return;
                        }

                        try
                        {
                            mainForm.BeginInvoke(
                                new Action(
                                    mainForm.RestoreFromExternalLaunch
                                )
                            );
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    },
                    null,
                    Timeout.Infinite,
                    false
                );

            try
            {
                AppLogger.Info(
                    "Main window message loop starting."
                );

                Application.Run(mainForm);
            }
            finally
            {
                AppLogger.Info(
                    "Magic Mirror is shutting down."
                );

                activationRegistration.Unregister(
                    null
                );
            }
        }

        private static void SignalExistingInstance()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using EventWaitHandle activationEvent =
                        EventWaitHandle.OpenExisting(
                            ActivationEventName
                        );

                    activationEvent.Set();
                    return;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }
        }
    }
}