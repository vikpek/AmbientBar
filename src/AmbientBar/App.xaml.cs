using System.Threading;
using System.Windows;

namespace AmbientBar;

public partial class App : Application
{
    // Keeps a second instance from starting (both would fight over the media keys).
    static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "AmbientBar.SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}
