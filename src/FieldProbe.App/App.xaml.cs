using System.IO;
using System.Windows;
using System.Windows.Threading;
using FieldProbe.Core;
using FieldProbe.Drivers.EtherNetIP;
using FieldProbe.Drivers.Modbus;
using FieldProbe.Drivers.Mqtt;
using FieldProbe.Drivers.OpcUa;
using FieldProbe.Drivers.Siemens;

namespace FieldProbe.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterDrivers();

        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Abrir projeto passado por linha de comando (associação de arquivo .fpx)
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            window.OpenProjectOnStartup(e.Args[0]);
    }

    /// <summary>Para adicionar um protocolo: crie o projeto do driver e registre aqui.</summary>
    private static void RegisterDrivers()
    {
        foreach (var info in ModbusDrivers.All()) DriverRegistry.Register(info);
        DriverRegistry.Register(OpcUaDrivers.Info);
        DriverRegistry.Register(MqttDrivers.Info);
        DriverRegistry.Register(S7Drivers.Info);
        DriverRegistry.Register(EtherNetIpDrivers.Info);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        MessageBox.Show(args.Exception.Message, "FieldProbe · erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
    }
}
