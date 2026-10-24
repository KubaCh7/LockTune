using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LockTune.Core;

namespace LockTune;

public partial class App : Application
{
    private const string MutexName = "LockTune.Windows.SingleInstance";
    private const string ShowEventName = "LockTune.Windows.ShowSettings";
    private Mutex? instanceMutex;
    private EventWaitHandle? showEvent;
    private AppCoordinator? coordinator;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (args.Length > 0 && args[0] == "--self-test")
        {
            var folder = args.Length > 1 ? args[1] : System.IO.Path.Combine(Environment.CurrentDirectory, "selftest");
            int code = await SelfTest.RunAsync(folder);
            Shutdown(code);
            return;
        }

        // One LockTune at a time: a second launch just opens the settings.
        instanceMutex = new Mutex(true, MutexName, out bool first);
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first && !args.Contains("--updated"))
        {
            showEvent.Set();
            Shutdown();
            return;
        }
        if (!first)
        {
            // Right after an update the old process may still be exiting.
            for (int i = 0; i < 20 && !instanceMutex.WaitOne(250); i++) { }
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppSettings.Folder, "errors.log"),
                                             $"{DateTime.Now:u} {ex.Exception}\n\n");
            }
            catch { }
        };

        coordinator = new AppCoordinator(AppSettings.Load());
        coordinator.Start(background: args.Contains("--background"), updated: args.Contains("--updated"));

        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
            Dispatcher.BeginInvoke(() => coordinator?.OpenSettings()), null, Timeout.Infinite, false);

        if (args.Contains("--preview")) coordinator.TogglePreview();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        coordinator?.Settings.Save();
        try { instanceMutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
