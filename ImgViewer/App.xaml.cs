using System.Windows;
using System.IO;

namespace ImgViewer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            this.DispatcherUnhandledException += (s, args) =>
            {
                File.AppendAllText("crash_log.txt", args.Exception + Environment.NewLine);
                System.Windows.MessageBox.Show("Unexpected error: " + args.Exception.Message,
                                "ImageGenie", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args2) =>
            {
                var ex = args2.ExceptionObject as Exception;
                if (ex != null)
                {
                    File.AppendAllText("crash_log.txt", ex + Environment.NewLine);
                }
            };
        }
    }

}
