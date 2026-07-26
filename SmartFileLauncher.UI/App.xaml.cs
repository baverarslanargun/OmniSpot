using System;
using System.IO;
using System.Text;
using System.Windows;

namespace SmartFileLauncher.UI;

public partial class App : System.Windows.Application 
{
	private static string _logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "omnispot_crash.log");

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		// No settings loading needed - rule-based parser only
		AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
		DispatcherUnhandledException += App_DispatcherUnhandledException;
	}

	private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
	{
		LogCrash("Dispatcher", e.Exception);
		System.Windows.MessageBox.Show($"Beklenmeyen UI hatası:\n{e.Exception}", "UI Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
		e.Handled = true; // prevent silent shutdown
	}

	private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is Exception ex)
		{
			LogCrash("Domain", ex);
			System.Windows.MessageBox.Show($"Kritik hata:\n{ex}", "Kritik", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private static void LogCrash(string source, Exception ex)
	{
		try
		{
			var sb = new StringBuilder();
			sb.AppendLine($"==== Crash {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} Source={source} ====");
			sb.AppendLine(ex.ToString());
			File.AppendAllText(_logFile, sb.ToString());
		}
		catch { /* ignore logging failures */ }
	}
}