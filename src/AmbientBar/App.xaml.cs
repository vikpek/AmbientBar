using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace AmbientBar;

public partial class App : Application
{
    // Keeps a second instance from starting (both would fight over the media keys).
    static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // After a self-update the old instance is still shutting down, so wait for it to let go.
        bool afterUpdate = e.Args.Contains("--after-update");
        _singleInstance = new Mutex(false, "AmbientBar.SingleInstance");
        bool owned;
        try
        {
            owned = _singleInstance.WaitOne(afterUpdate ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the previous instance exited without releasing it, which is fine
        }
        if (!owned)
        {
            Shutdown();
            return;
        }

        Updates.CleanUpAfterUpdate();
        base.OnStartup(e);
    }
}
