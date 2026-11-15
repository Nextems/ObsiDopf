using System.IO;
using Microsoft.Win32;

namespace ObsidianDocs;
internal static class InstalledComponents
{
    // A source checkout can reuse the large runtimes from an installed copy.
    public static string? Find(string relativePath)
    {
        try
        {
            using var key=Registry.CurrentUser.OpenSubKey(@"Software\ObsidianDocs");
            if(key?.GetValue("InstallDir") is not string root || !System.IO.Path.IsPathFullyQualified(root)) return null;
            var path=System.IO.Path.Combine(root,relativePath);
            return File.Exists(path) || Directory.Exists(path) ? path : null;
        }
        catch(System.Security.SecurityException) { return null; }
        catch(IOException) { return null; }
        catch(ArgumentException) { return null; }
    }
}
