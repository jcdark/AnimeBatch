using AnimeBatch.App.Services;
using Microsoft.UI.Xaml;

namespace AnimeBatch.App;

public partial class App : Application
{
    public static Window MainWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        InstallCrashLogger();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Banco + ferramentas: falha de tools não impede abrir a janela
        // (a tela de Configurações mostra o que está faltando).
        try
        {
            AppServices.Initialize();
        }
        catch (Exception ex)
        {
            AppServices.FatalError = ex.Message;
        }

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    /// <summary>
    /// Qualquer exceção não tratada vira uma linha em data\crash.log — sem isso um crash
    /// de startup morre em silêncio (exceção "stowed" do XAML não aparece pro usuário).
    /// </summary>
    private void InstallCrashLogger()
    {
        void Log(string source, Exception ex) => AppServices.LogCrash(source, ex);

        this.UnhandledException += (_, e) =>
        {
            Log("XAML", e.Exception);
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Log("AppDomain", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Task", e.Exception);
            e.SetObserved();
        };
    }
}
