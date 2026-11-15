using System.IO;
using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml.Packaging;

namespace ObsidianDocs;
internal static class DocConversionCache
{
    public static byte[] ConvertToDocx(string path,CancellationToken cancellation,IProgress<string>? progress)
    {
        cancellation.ThrowIfCancellationRequested();
        progress?.Report("Читаю DOC…");
        var source=File.ReadAllBytes(path);
        var engine=DocxRenderer.FindEngine() ?? throw new InvalidOperationException("Для преобразования DOC нужен LibreOffice. Выполните setup-docx.ps1.");
        // Hash the bytes we actually convert. Changing the source or renderer
        // invalidates the preview; filenames alone are not a reliable cache key.
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(source);
        hash.AppendData(Encoding.UTF8.GetBytes("doc-to-docx-v1|"+engine+"|"+File.GetLastWriteTimeUtc(engine).Ticks));
        string key=Convert.ToHexString(hash.GetHashAndReset());
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ObsidianDocs","doc-to-docx");
        var cache=Path.Combine(folder,key+".docx");
        cancellation.ThrowIfCancellationRequested();
        var cached=Read(cache);
        if(cached is not null) { progress?.Report("Открываю DOCX из кэша…"); return cached; }
        progress?.Report("Преобразую DOC в DOCX через LibreOffice…");
        var input=Directory.CreateTempSubdirectory("ObsiDopf-doc-");
        byte[] result;
        try
        {
            var snapshot=Path.Combine(input.FullName,"document.doc");
            File.WriteAllBytes(snapshot,source);
            result=DocxRenderer.ConvertToDocx(snapshot,cancellation);
        }
        finally { try { input.Delete(true); } catch(IOException) { } catch(UnauthorizedAccessException) { } }
        cancellation.ThrowIfCancellationRequested();
        Store(folder,cache,result);
        return result;
    }
    static byte[]? Read(string path)
    {
        try
        {
            if(!File.Exists(path)) return null;
            var bytes=File.ReadAllBytes(path);
            using var docx=WordprocessingDocument.Open(new MemoryStream(bytes),false);
            if(docx.MainDocumentPart?.Document?.Body is null) return null;
            File.SetLastWriteTimeUtc(path,DateTime.UtcNow);
            return bytes;
        }
        catch(IOException) { return null; }
        catch(UnauthorizedAccessException) { return null; }
        catch(OpenXmlPackageException) { return null; }
        catch(System.IO.FileFormatException) { return null; }
        catch(System.Xml.XmlException) { return null; }
    }
    static void Store(string folder,string path,byte[] bytes)
    {
        string? temp=null;
        try
        {
            Directory.CreateDirectory(folder);
            temp=Path.Combine(folder,Guid.NewGuid().ToString("N")+".tmp");
            File.WriteAllBytes(temp,bytes); File.Move(temp,path,true);
            long total=0; int count=0;
            foreach(var file in new DirectoryInfo(folder).EnumerateFiles("*.docx").Where(f=>Path.GetFileNameWithoutExtension(f.Name) is { Length:64 } name && name.All(Uri.IsHexDigit)).OrderByDescending(f=>f.LastWriteTimeUtc))
            {
                total+=file.Length; count++;
                if((total>256L*1024*1024 || count>64) && !file.FullName.Equals(path,StringComparison.OrdinalIgnoreCase)) file.Delete();
            }
        }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        finally { if(temp is not null) { try { File.Delete(temp); } catch(IOException) { } catch(UnauthorizedAccessException) { } } }
    }
}
