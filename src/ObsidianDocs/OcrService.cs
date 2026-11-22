using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace ObsidianDocs;

public sealed record OcrRegion(string Text, double X, double Y, double W, double H, float Confidence);
public sealed record OcrResult(string Text, List<OcrRegion> Regions);
public sealed class OcrService : IDisposable
{
    Process? process;
    readonly SemaphoreSlim gate = new(1);
    public async Task<OcrResult> RecognizeAsync(BitmapSource bitmap, string language, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            if (process is null || process.HasExited)
            {
                var worker = Path.Combine(AppContext.BaseDirectory, "ocr-worker.exe");
                if(!File.Exists(worker)) worker=InstalledComponents.Find("ocr-worker.exe") ?? worker;
                if (!File.Exists(worker)) throw new FileNotFoundException("Не найден OCR-модуль. Запустите build.ps1.", worker);
                process = new Process { StartInfo = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 }};
                var models=Path.Combine(AppContext.BaseDirectory,"models");
                if(!Directory.Exists(models)) models=InstalledComponents.Find("models") ?? models;
                if (Directory.Exists(models)) process.StartInfo.Environment["OAR_HOME"]=models;
                process.Start();
                process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
            }
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { path, lang = language }));
            await process.StandardInput.FlushAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith("OCR_RESULT:")) continue;
                using var response = JsonDocument.Parse(line[11..]);
                var root = response.RootElement;
                if (!root.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(root.GetProperty("error").GetString());
                var result = root.GetProperty("result");
                var regions = new List<OcrRegion>();
                foreach (var r in result.GetProperty("regions").EnumerateArray())
                {
                    var b = r.GetProperty("bbox");
                    regions.Add(new(r.GetProperty("text").GetString() ?? "", b.GetProperty("x").GetDouble(), b.GetProperty("y").GetDouble(), b.GetProperty("w").GetDouble(), b.GetProperty("h").GetDouble(), r.GetProperty("confidence").GetSingle()));
                }
                return new(result.GetProperty("text").GetString() ?? "", regions);
            }
            throw new InvalidOperationException("OCR-процесс завершился без результата.");
        }
        catch { Dispose(); throw; }
        finally { File.Delete(path); gate.Release(); }
    }
    public void Dispose() { if (process is not null) { try { if (!process.HasExited) process.Kill(true); } catch { } process.Dispose(); process = null; } }
}
