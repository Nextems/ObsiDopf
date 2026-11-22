using System.IO;
using PdfSharp.Pdf.IO;

namespace ObsidianDocs;

internal static class PdfAuthorBatch
{
    // Author metadata belongs to markup, not links, widgets or popup containers.
    static readonly HashSet<string> markup=new(StringComparer.Ordinal)
    {
        "/Text","/FreeText","/Line","/Square","/Circle","/Polygon","/PolyLine",
        "/Highlight","/Underline","/Squiggly","/StrikeOut","/Stamp","/Caret",
        "/Ink","/FileAttachment","/Sound","/Redact","/Projection"
    };
    internal static string Author(string value)
    {
        value=value.Trim();
        if(value.Length is <1 or >100 || value.Any(char.IsControl)) throw new ArgumentException("Имя автора: от 1 до 100 символов, без переносов строк.");
        return value;
    }
    internal static Prepared Prepare(string path,string author,CancellationToken cancellation,string? outputDirectory=null)
    {
        author=Author(author); path=Path.GetFullPath(path);
        if(!Path.GetExtension(path).Equals(".pdf",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Выберите PDF.");
        cancellation.ThrowIfCancellationRequested();
        string destination=Path.GetFullPath(outputDirectory ?? Path.GetDirectoryName(path)!);
        if(!Directory.Exists(destination)) throw new DirectoryNotFoundException("Папка результатов недоступна: "+destination);
        var info=new FileInfo(path); long length=info.Length; var modified=info.LastWriteTimeUtc;
        var original=File.ReadAllBytes(path);
        using var input=new MemoryStream(original,false);
        using var pdf=PdfReader.Open(input,PdfDocumentOpenMode.Modify);
        int total=0,changed=0;
        foreach(var page in pdf.Pages)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!page.Elements.ContainsKey("/Annots")) continue;
            foreach(PdfSharp.Pdf.Annotations.PdfAnnotation note in page.Annotations)
            {
                if(!markup.Contains(note.Elements.GetName("/Subtype"))) continue;
                total++;
                if(note.Title==author) continue;
                // PDFsharp's Title setter also updates /M. This operation
                // changes attribution only; keep the original comment dates.
                var modifiedDate=note.Elements["/M"];
                var createdDate=note.Elements["/CreationDate"];
                note.Title=author;
                if(modifiedDate is null) note.Elements.Remove("/M"); else note.Elements["/M"]=modifiedDate;
                if(createdDate is null) note.Elements.Remove("/CreationDate"); else note.Elements["/CreationDate"]=createdDate;
                changed++;
            }
        }
        bool exportUnchanged=outputDirectory is not null && !Path.TrimEndingDirectorySeparator(destination).Equals(Path.GetDirectoryName(path),StringComparison.OrdinalIgnoreCase);
        if(changed==0 && !exportUnchanged) return new Prepared(path,author,total,changed,null,length,modified,destination);
        // Stage in the destination, so a different drive/network share also
        // supports an atomic final rename and read-only source folders work.
        var temporary=Path.Combine(destination,".obsidopf-authors-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            // A folder export includes unchanged PDFs too. Preserve their exact
            // bytes instead of rewriting their metadata through the PDF library.
            if(changed==0) File.WriteAllBytes(temporary,original); else pdf.Save(temporary);
            cancellation.ThrowIfCancellationRequested();
            return new Prepared(path,author,total,changed,temporary,length,modified,destination);
        }
        catch { File.Delete(temporary); throw; }
    }
    internal sealed class Prepared : IDisposable
    {
        readonly string source,author,destination;
        readonly long length;
        readonly DateTime modified;
        string? temporary;
        public int Total { get; }
        public int Changed { get; }
        public bool HasOutput=>temporary is not null;
        internal Prepared(string source,string author,int total,int changed,string? temporary,long length,DateTime modified,string destination)
        { this.source=source; this.author=author; Total=total; Changed=changed; this.temporary=temporary; this.length=length; this.modified=modified; this.destination=destination; }
        // Only the atomic commit follows the UI's final unsaved-edits check.
        internal string? Commit(bool copies,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var pending=temporary; if(pending is null) return null;
            if(!copies)
            {
                if(!Path.TrimEndingDirectorySeparator(destination).Equals(Path.GetDirectoryName(source),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Выбранная папка доступна для копий. Исходные PDF изменяются на месте.");
                var current=new FileInfo(source);
                if(!current.Exists || current.Length!=length || current.LastWriteTimeUtc!=modified) throw new IOException("PDF изменился во время обработки. Повторите действие.");
                File.Move(pending,source,true); temporary=null; return source;
            }
            var invalid=Path.GetInvalidFileNameChars().ToHashSet();
            string suffix=new(author.Where(c=>!invalid.Contains(c) && !char.IsControl(c)).Take(50).ToArray());
            suffix=suffix.Trim(' ','.'); if(suffix.Length==0) suffix="author";
            string stem=new(Path.GetFileNameWithoutExtension(source).Take(140).ToArray());
            for(int i=1;;i++)
            {
                cancellation.ThrowIfCancellationRequested();
                string target=Path.Combine(destination,stem+"_"+suffix+(i==1 ? "" : "_"+i)+".pdf");
                try { File.Move(pending,target,false); temporary=null; return target; }
                catch(IOException) when(File.Exists(target)) { }
            }
        }
        public void Dispose()
        {
            if(temporary is null) return;
            try { File.Delete(temporary); } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
    }
}
