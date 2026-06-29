using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeWidget;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static Mutex? _mutex;

    public App()
    {
        // Red de seguridad: un fallo inesperado NO debe cerrar el widget.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, _) => { };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Instancia única: si ya hay un widget abierto, este se cierra
        // (así dos instancias no se pisan el settings.json).
        _mutex = new Mutex(true, "ClaudeWidget_SingleInstance_2026", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true; // tragamos la excepción para que la app siga viva
        try
        {
            MessageBox.Show("Ha ocurrido un fallo inesperado, pero el widget sigue funcionando.\n\n" +
                            e.Exception.Message, "Claude Widget", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
    }
}
