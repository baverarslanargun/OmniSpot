using System;
using System.IO;
using System.Text;
using System.Windows;
using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.UI.Composition;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.Views;

namespace SmartFileLauncher.UI;

public partial class App : System.Windows.Application 
{
	private string _logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "omnispot_crash.log");
	private bool _redactCrashPaths;
	private ApplicationCompositionRoot? _compositionRoot;
	private MainWindow? _mainWindow;

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		var startupOptions = ApplicationStartupOptions.Parse(e.Args);
		if (startupOptions.Error != null)
		{
			System.Windows.MessageBox.Show(
				startupOptions.Error,
				"OmniSpot Ölçüm Profili",
				MessageBoxButton.OK,
				MessageBoxImage.Error);
			Shutdown(2);
			return;
		}

		MeasurementRunLayout? measurementRun = null;
		if (startupOptions.IsMeasurement)
		{
			try
			{
				measurementRun = MeasurementRunLayout.Prepare(startupOptions);
			}
			catch (Exception ex)
			{
                System.Windows.MessageBox.Show(
                    $"{startupOptions.ProfileName ?? "ölçüm"} profili başlatılamadı:{Environment.NewLine}{Environment.NewLine}{ex.Message}",
					"OmniSpot Ölçüm Profili",
					MessageBoxButton.OK,
					MessageBoxImage.Error);
				Shutdown(2);
				return;
			}
		}
		if (measurementRun != null)
		{
			_logFile = Path.Combine(measurementRun.RunRoot, "omnispot_crash.log");
			_redactCrashPaths = measurementRun.Profile == MeasurementProfile.ProductionCopy;
		}

		var indexRebuildFailed = e.Args.Any(argument =>
			string.Equals(
				argument,
				IndexMaintenanceService.RebuildFailedArgument,
				StringComparison.OrdinalIgnoreCase));
		AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
		DispatcherUnhandledException += App_DispatcherUnhandledException;

		try
		{
			_compositionRoot = new ApplicationCompositionRoot(
				startupOptions,
				measurementRun);
		}
		catch
		{
			measurementRun?.Dispose();
			throw;
		}
		_mainWindow = _compositionRoot.CreateMainWindow();
		MainWindow = _mainWindow;
		_mainWindow.Show();

		if (indexRebuildFailed)
		{
			System.Windows.MessageBox.Show(
				_mainWindow,
				"İndeks dosyaları silinemedi. OmniSpot mevcut indeksle açıldı. Dosyaların başka bir süreç tarafından kullanılmadığını kontrol edip yeniden deneyin.",
				"İndeks Yeniden Oluşturulamadı",
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
		}
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_mainWindow?.PrepareForShutdown();
		_compositionRoot?.Dispose();
		base.OnExit(e);
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

	private void LogCrash(string source, Exception ex)
	{
		try
		{
			File.AppendAllText(
				_logFile,
				FormatCrash(source, ex, DateTime.Now, _redactCrashPaths));
		}
		catch { /* ignore logging failures */ }
	}

	internal static string FormatCrash(
		string source,
		Exception ex,
		DateTime timestamp,
		bool redactPaths)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(source);
		ArgumentNullException.ThrowIfNull(ex);

		var sb = new StringBuilder();
		sb.AppendLine($"==== Crash {timestamp:yyyy-MM-dd HH:mm:ss.fff} Source={source} ====");
		var exceptionText = ex.ToString();
		if (redactPaths)
		{
			exceptionText = DiagnosticPathRedactor.Redact(exceptionText);
		}

		sb.AppendLine(exceptionText);
		return sb.ToString();
	}
}
