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

            // Ensure a valid working directory to avoid "drive not found" errors
            try
            {
                var baseDir = AppContext.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(baseDir) && Directory.Exists(baseDir))
                {
                    Environment.CurrentDirectory = baseDir;
                }
            }
            catch
            {
                // ignore and continue with default working directory
            }

            string crashLogPath = GetCrashLogPath();

            this.DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    File.AppendAllText(crashLogPath, args.Exception + Environment.NewLine);
                }
                catch { }
                System.Windows.MessageBox.Show("Unexpected error: " + args.Exception.Message,
                                "ImageGenie", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args2) =>
            {
                var ex = args2.ExceptionObject as Exception;
                if (ex != null)
                {
                    try
                    {
                        File.AppendAllText(crashLogPath, ex + Environment.NewLine);
                    }
                    catch { }
                }
            };
        }

        private static string GetCrashLogPath()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                if (string.IsNullOrWhiteSpace(baseDir))
                {
                    baseDir = Environment.CurrentDirectory;
                }
                if (!string.IsNullOrWhiteSpace(baseDir))
                {
                    return Path.Combine(baseDir, "crash_log.txt");
                }
            }
            catch
            {
                // ignore and fallback to relative path
            }
            return "crash_log.txt";
        }
    }

}
