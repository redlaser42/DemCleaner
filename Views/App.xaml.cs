using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using DemCleaner.Models;
using DemCleaner.Services;

namespace DemCleaner;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            RunSelfTest();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }

    private static void RunSelfTest()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DemCleanerSelfTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Verify default FillMethod is Minimum
            var defaultOptions = new DemProcessingOptions();
            if (defaultOptions.FillMethod != FillMethod.Minimum)
                throw new Exception($"Expected default FillMethod.Minimum, got {defaultOptions.FillMethod}");

            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest_result.txt"),
                $"SELF-TEST PASSED: Default={defaultOptions.FillMethod}, Themes={ThemeManager.Themes.Count}.");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
