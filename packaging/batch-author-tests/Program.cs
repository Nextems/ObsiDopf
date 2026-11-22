using System.IO;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Drawing;
using ObsidianDocs;

static class Program
{
    sealed class Annotation:PdfAnnotation
    {
        public Annotation(PdfDocument pdf,string subtype):base(pdf) { Elements.SetName("/Subtype",subtype); }
    }
    static void Check(bool condition,string label) { if(!condition) throw new Exception(label); }
    static void Reject(Action action,string label)
    {
        bool failed=false; try { action(); } catch(Exception) { failed=true; }
        Check(failed,label);
    }
    static string Fixture(string root,string name,bool comments=true)
    {
        var path=Path.Combine(root,name+".pdf");
        using var pdf=new PdfDocument(); pdf.Info.Title="Keep document metadata";
        var page=pdf.AddPage(); pdf.AddPage();
        using(var graphics=XGraphics.FromPdfPage(page)) graphics.DrawString("Page text must survive",new XFont("Arial",18),XBrushes.Black,new XPoint(60,100));
        if(comments)
        {
            foreach(var subtype in new[] { "/Text","/Highlight","/Text","/Link","/Widget" })
            {
                var note=new Annotation(pdf,subtype);
                note.Title=subtype=="/Widget" ? "Form field name" : "Original author";
                note.Contents="Не менять текст "+subtype;
                note.Rectangle=new PdfRectangle(new XPoint(60,500),new XPoint(160,520));
                note.Elements.SetString("/NM","id-"+page.Annotations.Count);
                note.Elements.SetString("/M","D:20260101000000Z");
                note.Elements.SetString("/CreationDate","D:20250102000000Z");
                if(subtype=="/Highlight") { note.Elements["/QuadPoints"]=new PdfArray(pdf,new PdfReal(60),new PdfReal(520),new PdfReal(160),new PdfReal(520),new PdfReal(60),new PdfReal(500),new PdfReal(160),new PdfReal(500)); note.Elements.SetString("/ODQuote","Selected original text"); }
                if(subtype=="/Widget") note.Elements.SetName("/FT","/Tx");
                page.Annotations.Add(note);
            }
            page.Annotations[2].Title=""; page.Annotations[2].Elements["/IRT"]=page.Annotations[0].Reference!;
        }
        pdf.Save(path); return path;
    }
    static string Shape(PdfDocument pdf)
    {
        var rows=new List<string> { pdf.PageCount.ToString(),pdf.Info.Title };
        foreach(var page in pdf.Pages)
        {
            rows.Add(page.MediaBox.ToString());
            for(int i=0;i<page.Contents.Elements.Count;i++) rows.Add(Convert.ToBase64String(page.Contents.Elements.GetDictionary(i)!.Stream.UnfilteredValue));
            if(!page.Elements.ContainsKey("/Annots")) continue;
            foreach(PdfAnnotation note in page.Annotations)
                rows.Add(string.Join("|",note.Elements.GetName("/Subtype"),note.Contents,note.Rectangle,note.Elements.GetString("/NM"),note.Elements.GetString("/M"),note.Elements.GetString("/CreationDate"),note.Elements.GetString("/ODQuote"),note.Elements.GetArray("/QuadPoints")?.ToString(),note.Elements.ContainsKey("/IRT")));
        }
        return string.Join("\n",rows);
    }
    [STAThread]
    static void Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"ObsiDopfBatchTests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string path=Fixture(root,"original"); byte[] original=File.ReadAllBytes(path);
        string shape; using(var input=PdfReader.Open(path,PdfDocumentOpenMode.Import)) shape=Shape(input);
        string copy;
        using(var change=PdfAuthorBatch.Prepare(path,"Новый автор",default))
        {
            Check(change.Total==3 && change.Changed==3,"Count all markup, including unnamed reply; exclude links/widgets");
            copy=change.Commit(true,default)!;
        }
        Check(original.SequenceEqual(File.ReadAllBytes(path)),"Copy modified original");
        using(var result=PdfReader.Open(copy,PdfDocumentOpenMode.Import))
        {
            Check(Shape(result)==shape,"Page text, contents, dates, quote, geometry or reply metadata changed\nBEFORE:\n"+shape+"\nAFTER:\n"+Shape(result));
            Check(result.Pages[0].Annotations[0].Title=="Новый автор" && result.Pages[0].Annotations[1].Title=="Новый автор" && result.Pages[0].Annotations[2].Title=="Новый автор","Authors not changed");
            Check(result.Pages[0].Annotations[3].Title=="Original author" && result.Pages[0].Annotations[4].Title=="Form field name","Link/widget metadata was renamed");
        }
        Console.WriteLine("PASS: markup and replies renamed; document text, comment text, dates, geometry, links and widgets preserved");
        using(var again=PdfAuthorBatch.Prepare(path,"Новый автор",default)) { string next=again.Commit(true,default)!; Check(next!=copy,"Copy collision overwrote a file"); }
        Check(original.SequenceEqual(File.ReadAllBytes(path)),"Second copy changed original");
        using(var same=PdfAuthorBatch.Prepare(copy,"Новый автор",default)) Check(same.Changed==0 && same.Commit(false,default)==null,"Already-correct authors rewrote file");
        Console.WriteLine("PASS: copy name collisions and unchanged authors");
        string destination=Path.Combine(root,"chosen-output"); Directory.CreateDirectory(destination);
        string sourceA=Path.Combine(root,"source-a"),sourceB=Path.Combine(root,"source-b"); Directory.CreateDirectory(sourceA); Directory.CreateDirectory(sourceB);
        string exportA=Fixture(sourceA,"same-name"),exportB=Fixture(sourceB,"same-name"); byte[] exportBytes=File.ReadAllBytes(exportA);
        string exportShape; using(var input=PdfReader.Open(exportA,PdfDocumentOpenMode.Import)) exportShape=Shape(input);
        string destinationCopy;
        File.SetAttributes(exportA,File.GetAttributes(exportA)|FileAttributes.ReadOnly);
        try
        {
            using(var change=PdfAuthorBatch.Prepare(exportA,"Folder author",default,destination))
            {
                Check(Directory.GetFiles(sourceA,".obsidopf-authors-*.tmp").Length==0,"Temporary result was written beside source instead of chosen destination");
                Check(Directory.GetFiles(destination,".obsidopf-authors-*.tmp").Length==1,"Result not staged in destination");
                destinationCopy=change.Commit(true,default)!;
            }
        }
        finally { File.SetAttributes(exportA,File.GetAttributes(exportA)&~FileAttributes.ReadOnly); }
        Check(Path.GetDirectoryName(destinationCopy)==destination,"Chosen output directory ignored");
        Check(exportBytes.SequenceEqual(File.ReadAllBytes(exportA)),"Export changed read-only original");
        using(var output=PdfReader.Open(destinationCopy,PdfDocumentOpenMode.Import)) Check(output.Pages[0].Annotations[0].Title=="Folder author" && Shape(output)==exportShape,"Chosen-folder export lost comments or content");
        byte[] existingCopy=File.ReadAllBytes(destinationCopy);
        using(var change=PdfAuthorBatch.Prepare(exportB,"Folder author",default,destination)) Check(change.Commit(true,default)!=destinationCopy,"Same-name inputs from different folders overwrote each other");
        Check(existingCopy.SequenceEqual(File.ReadAllBytes(destinationCopy)),"Existing output copy was overwritten");
        using(var cancel=new CancellationTokenSource())
        using(var change=PdfAuthorBatch.Prepare(exportB,"Cancelled",cancel.Token,destination)) { cancel.Cancel(); Reject(()=>change.Commit(true,cancel.Token),"Destination export ignored cancellation"); }
        using(var change=PdfAuthorBatch.Prepare(exportB,"Wrong mode",default,destination)) Reject(()=>change.Commit(false,default),"Original replacement accepted a different staging directory");
        Reject(()=>PdfAuthorBatch.Prepare(exportB,"Missing",default,Path.Combine(root,"missing-directory")),"Missing destination silently fell back to source");
        Check(Directory.GetFiles(destination,".obsidopf-authors-*.tmp").Length==0,"Destination temporary files leaked");
        Console.WriteLine("PASS: selected destination, read-only input, same-name inputs, destination cancellation and missing-folder errors");
        byte[] sameAuthorBytes=File.ReadAllBytes(copy);
        using(var same=PdfAuthorBatch.Prepare(copy,"Новый автор",default,destination))
        {
            Check(same.Changed==0 && same.HasOutput,"Unchanged PDF was omitted from folder export");
            string result=same.Commit(true,default)!;
            Check(Path.GetDirectoryName(result)==destination && sameAuthorBytes.SequenceEqual(File.ReadAllBytes(result)),"Already-correct authors were not copied byte-for-byte");
        }
        Check(sameAuthorBytes.SequenceEqual(File.ReadAllBytes(copy)),"Unchanged copy export modified source");
        string noNotes=Fixture(root,"no-notes-for-export",false); byte[] noNotesBytes=File.ReadAllBytes(noNotes);
        string firstUnchangedCopy;
        using(var same=PdfAuthorBatch.Prepare(noNotes,"Author",default,destination))
        {
            Check(same.Total==0 && same.Changed==0 && same.HasOutput,"Comment-free PDF was omitted from folder export");
            firstUnchangedCopy=same.Commit(true,default)!;
            Check(noNotesBytes.SequenceEqual(File.ReadAllBytes(firstUnchangedCopy)),"Comment-free PDF was rewritten instead of copied");
        }
        using(var same=PdfAuthorBatch.Prepare(noNotes,"Author",default,destination)) Check(same.Commit(true,default)!=firstUnchangedCopy,"Unchanged export overwrote an existing output");
        using(var cancel=new CancellationTokenSource())
        using(var same=PdfAuthorBatch.Prepare(noNotes,"Author",cancel.Token,destination)) { cancel.Cancel(); Reject(()=>same.Commit(true,cancel.Token),"Unchanged export ignored cancellation"); }
        Check(noNotesBytes.SequenceEqual(File.ReadAllBytes(noNotes)) && Directory.GetFiles(destination,".obsidopf-authors-*.tmp").Length==0,"Cancelled unchanged export changed source or leaked temporary data");
        Console.WriteLine("PASS: unchanged and comment-free PDFs exported byte-for-byte, including collisions and cancellation");
        using(var change=PdfAuthorBatch.Prepare(path,"Replaced author",default)) Check(change.Commit(false,default)==path,"Overwrite output path");
        using(var changed=PdfReader.Open(path,PdfDocumentOpenMode.Import)) Check(changed.Pages[0].Annotations[0].Title=="Replaced author" && Shape(changed)==shape,"Overwrite lost data");
        Console.WriteLine("PASS: atomic overwrite");
        string locked=Fixture(root,"locked"); byte[] lockedBytes=File.ReadAllBytes(locked);
        using(var change=PdfAuthorBatch.Prepare(locked,"New",default))
        using(var file=new FileStream(locked,FileMode.Open,FileAccess.Read,FileShare.Read)) Reject(()=>change.Commit(false,default),"Locked source was overwritten");
        Check(lockedBytes.SequenceEqual(File.ReadAllBytes(locked)),"Locked source changed");
        using(var change=PdfAuthorBatch.Prepare(locked,"New",default)) { File.SetLastWriteTimeUtc(locked,File.GetLastWriteTimeUtc(locked).AddSeconds(5)); Reject(()=>change.Commit(false,default),"Externally modified source overwritten"); }
        using(var cancel=new CancellationTokenSource())
        using(var change=PdfAuthorBatch.Prepare(locked,"New",cancel.Token)) { cancel.Cancel(); Reject(()=>change.Commit(false,cancel.Token),"Cancellation ignored"); }
        Check(lockedBytes.SequenceEqual(File.ReadAllBytes(locked)),"Failed/cancelled action changed source");
        Check(Directory.GetFiles(root,".obsidopf-authors-*.tmp").Length==0,"Temporary files left after failure/cancellation");
        Console.WriteLine("PASS: locked/changed sources and cancellation preserve originals and clean temporary files");
        string empty=Fixture(root,"empty",false); byte[] emptyBytes=File.ReadAllBytes(empty);
        using(var change=PdfAuthorBatch.Prepare(empty,"Author",default)) Check(change.Total==0 && change.Commit(false,default)==null,"PDF without comments changed");
        Check(emptyBytes.SequenceEqual(File.ReadAllBytes(empty)),"Comment-free PDF was rewritten");
        Reject(()=>PdfAuthorBatch.Author("   "),"Blank author accepted"); Reject(()=>PdfAuthorBatch.Author("A\nB"),"Control character accepted"); Reject(()=>PdfAuthorBatch.Author(new string('A',101)),"Long author accepted");
        string bad=Path.Combine(root,"corrupt.pdf"); File.WriteAllText(bad,"not a PDF"); Reject(()=>PdfAuthorBatch.Prepare(bad,"Author",default),"Corrupt PDF accepted");
        Console.WriteLine("PASS: empty PDFs, invalid names and malformed PDFs");
    }
}
