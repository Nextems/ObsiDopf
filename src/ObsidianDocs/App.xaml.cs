using System.IO;
using System.Windows;
namespace ObsidianDocs;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--save-test"))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var report=Path.Combine(AppContext.BaseDirectory,"save-test.txt");
            try
            {
                var window=new MainWindow(); window.Show(); await window.TestSaveShortcutAsync(); window.Close();
                File.WriteAllText(report,"PASS: Ctrl+S routed command overwrites active PDF, commits draft, repeated saves/deletions, failed-save integrity and retry, DOCX overwrite protection"); Shutdown(0);
            }
            catch (Exception error) { File.WriteAllText(report,error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--comment-test"))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var report=Path.Combine(AppContext.BaseDirectory,"comment-test.txt");
            try
            {
                var window=new MainWindow(); window.Show(); await window.TestTextCommentsAsync(); window.Close();
                File.WriteAllText(report,"PASS: multiline text selection, rotated/cropped page coordinates, saved PDF highlight/quote, editing, duplicate author preservation, explicit deletion, no leftover highlight, unrelated comment integrity"); Shutdown(0);
            }
            catch (Exception error) { File.WriteAllText(report,error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--wheel-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var report = Path.Combine(AppContext.BaseDirectory,"wheel-test.txt");
            try
            {
                var window = new MainWindow(); window.Show();
                await window.TestWheelAsync(); window.Close();
                File.WriteAllText(report,"PASS: actual WPF wheel scrolling, 14 continuous pages and thumbnails, seam rendering, current-page tracking, comment column/markers, new comment author/page, author profiles, copied comments with changed authors, original integrity"); Shutdown(0);
            }
            catch (Exception error) { File.WriteAllText(report,error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { File.Delete(Path.Combine(AppContext.BaseDirectory,"smoke-error.txt")); await SmokeTests.RunAsync(); Shutdown(0); }
            catch (Exception error) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"smoke-error.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        base.OnStartup(e);
        var main=new MainWindow(); main.Show();
        var paths=e.Args.Where(arg=>!arg.StartsWith("--",StringComparison.Ordinal)).ToArray();
        if(paths.Length>0) await main.OpenPathsAsync(paths);
    }
}


