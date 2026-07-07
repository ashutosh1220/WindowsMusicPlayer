using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WinUIMusicPlayer
{
    public partial class App : Application
    {
        private Window? _window;
        private static readonly string LogFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinUIMusicPlayer", "Logs");

        public App()
        {
            try
            {
                Directory.CreateDirectory(LogFolder);

                LogMessage("App constructor reached");
                this.InitializeComponent();

                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Fatal error in App constructor: {ex}");
            }
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                LogMessage("OnLaunched reached");

                _window = new MusicPlayerWindow();
                LogMessage("MusicPlayerWindow created");

                _window.Closed += OnWindowClosed;

                _window.Activate();
                LogMessage("Window activated");

                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                LogError("OnLaunched", ex);
                ShowFatalErrorAndExit(ex);
            }
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            try
            {
                LogMessage("Window closed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in OnWindowClosed: {ex}");
            }
        }

        private void OnUnhandledException(object? sender, System.UnhandledExceptionEventArgs args)
        {
            try
            {
                var ex = args.ExceptionObject as Exception;
                LogError("AppDomain UnhandledException", ex ?? new Exception(args.ExceptionObject?.ToString()));
            }
            catch { }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            try
            {
                LogError("UnobservedTaskException", args.Exception);
                args.SetObserved();
            }
            catch { }
        }

        private static void LogMessage(string message)
        {
            try
            {
                string logFile = Path.Combine(LogFolder, $"app_log_{DateTime.Now:yyyyMMdd}.txt");
                File.AppendAllText(logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [INFO] {message}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to log message '{message}': {ex.Message}");
            }
        }

        private static void LogError(string context, Exception ex)
        {
            try
            {
                string logFile = Path.Combine(LogFolder, $"app_errors_{DateTime.Now:yyyyMMdd}.txt");
                string errorDetails = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [ERROR] Context: {context}{Environment.NewLine}" +
                                      $"Message: {ex.Message}{Environment.NewLine}" +
                                      $"Stack: {ex.StackTrace}{Environment.NewLine}" +
                                      $"---{Environment.NewLine}";
                File.AppendAllText(logFile, errorDetails);
                Debug.Write(errorDetails);
            }
            catch
            {
            }
        }

        private void ShowFatalErrorAndExit(Exception ex)
        {
            try
            {
                var msgDialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    Title = "Fatal Error",
                    Content = $"The application failed to start.\n\n{ex.Message}",
                    CloseButtonText = "Exit"
                };
                _ = msgDialog.ShowAsync();
            }
            catch { }
            finally
            {
                Environment.Exit(1);
            }
        }
    }
}