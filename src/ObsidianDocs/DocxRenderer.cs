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
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var candidate = Path.Combine(root, "LibreOffice", "program", "soffice.com");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
    public static byte[] Render(string path)
    {
        var engine = FindEngine() ?? throw new InvalidOperationException("Не найден движок просмотра DOCX. Выполните setup-docx.ps1.");
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
            foreach (var arg in new[] { "-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri, "--headless", "--nologo", "--nodefault", "--norestore", "--convert-to", "pdf:writer_pdf_Export", "--outdir", temp, Path.GetFullPath(path) }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить движок DOCX.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("Подготовка DOCX заняла больше двух минут."); }
            Task.WaitAll(stdout,stderr);
            var pdf = Path.Combine(temp,Path.GetFileNameWithoutExtension(path)+".pdf");
            if (process.ExitCode != 0 || !File.Exists(pdf)) throw new InvalidOperationException($"Не удалось подготовить DOCX для просмотра (код {process.ExitCode}). " + stderr.Result + stdout.Result);
            return File.ReadAllBytes(pdf);
        }
        finally { try { Directory.Delete(temp,true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
