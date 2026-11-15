using System.Diagnostics;
using System.IO;
using System.Text;
namespace ObsidianDocs;

internal static class DocxRenderer
{
    public static string? FindEngine()
    {
        var configured = Environment.GetEnvironmentVariable("OBSIDIAN_DOCS_SOFFICE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            foreach (var relative in new[] { "native/libreoffice/runtime/program/soffice.com", "native/libreoffice/runtime/LibreOffice/program/soffice.com" })
            {
                var candidate = Path.Combine(folder.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
        if(InstalledComponents.Find("native/libreoffice/runtime/program/soffice.com") is { } installed) return installed;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var candidate = Path.Combine(root, "LibreOffice", "program", "soffice.com");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
    public static byte[] Render(string path,CancellationToken cancellation=default)
        =>Convert(path,"pdf","pdf:writer_pdf_Export",cancellation);
    public static byte[] ConvertToDocx(string path,CancellationToken cancellation=default)
        =>Convert(path,"docx","docx:Office Open XML Text",cancellation);
    static byte[] Convert(string path,string extension,string filter,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var engine = FindEngine() ?? throw new InvalidOperationException("Не найден LibreOffice для DOC/DOCX. Выполните setup-docx.ps1.");
        var format=Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var temp = Path.Combine(Path.GetTempPath(), "ObsidianDocs", Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(temp,"profile");
        Directory.CreateDirectory(Path.Combine(profile,"user"));
        File.WriteAllText(Path.Combine(profile,"user","registrymodifications.xcu"), """
        <?xml version="1.0" encoding="UTF-8"?>
        <oor:items xmlns:oor="http://openoffice.org/2001/registry"><item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item><item oor:path="/org.openoffice.Office.Writer/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>2</value></prop></item></oor:items>
        """);
        try
        {
            var start = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var arg in new[] { "-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri, "--headless", "--nologo", "--nodefault", "--norestore", "--convert-to", filter, "--outdir", temp, Path.GetFullPath(path) }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить LibreOffice.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            try { process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult(); }
            catch(OperationCanceledException)
            {
                try { if(!process.HasExited) process.Kill(true); } catch(InvalidOperationException) { }
                process.WaitForExit(5000);
                cancellation.ThrowIfCancellationRequested();
                throw new TimeoutException($"Подготовка {format} заняла больше двух минут.");
            }
            Task.WaitAll(stdout,stderr);
            cancellation.ThrowIfCancellationRequested();
            var output = Path.Combine(temp,Path.GetFileNameWithoutExtension(path)+"."+extension);
            if (process.ExitCode != 0 || !File.Exists(output)) throw new InvalidOperationException($"Не удалось преобразовать {format} в {extension.ToUpperInvariant()} (код {process.ExitCode}). " + stderr.Result + stdout.Result);
            return File.ReadAllBytes(output);
        }
        finally { try { Directory.Delete(temp,true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
