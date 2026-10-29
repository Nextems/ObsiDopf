using System.IO;
using System.Windows;
using System.Windows.Input;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf;
using PdfSharp.Drawing;
namespace ObsidianDocs;
public partial class MainWindow
{
    internal async Task TestSaveShortcutAsync()
    {
        var path=Path.Combine(FindArtifacts(),"ctrl-s-original.pdf");
        var session=new DocumentSession { Path=path,Pdf=DocumentSession.TextPdf(["Save shortcut fixture"]) };
        session.Snapshot(); session.SaveTo(path); var before=File.ReadAllBytes(path);
        AddSessionTab(session); await RenderAsync();
        session.Pdf!.Pages[0].Annotations.Add(new PdfTextAnnotation { Title="Reviewer",Contents="Saved by shortcut",Rectangle=new PdfRectangle(new XPoint(100,500),new XPoint(120,520)) });
        session.Snapshot(); session.Dirty=true;
        ApplicationCommands.Save.Execute(null,this); await saveTask;
        if (session.Dirty || before.SequenceEqual(File.ReadAllBytes(path))) throw new Exception("Save command did not overwrite active file");
        using (var reopened=DocumentSession.Open(path))
            if (reopened.Pdf!.Pages[0].Annotations[0].Contents!="Saved by shortcut") throw new Exception("Save command lost comment");
        await OpenCommentAsync(0,0); OcrText.Text="Draft saved with Ctrl+S";
        ApplicationCommands.Save.Execute(null,this); await saveTask;
        using (var reopened=DocumentSession.Open(path))
            if (reopened.Pdf!.Pages[0].Annotations[0].Contents!="Draft saved with Ctrl+S") throw new Exception("Uncommitted draft was not saved");
        ApplicationCommands.Save.Execute(null,this); await saveTask;
        session.Pdf!.Pages[0].Annotations[0].Contents="Changed while locked"; session.Snapshot(); session.Dirty=true;
        var lockedBytes=File.ReadAllBytes(path);
        bool failed=false;
        using (var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            try { await SaveCurrentAsync(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed=true; }
            if (!failed || !session.Dirty || !lockedBytes.SequenceEqual(File.ReadAllBytes(path))) throw new Exception("Failed save changed file or lost dirty state");
        }
        await SaveCurrentAsync();
        if (session.Dirty) throw new Exception("Retry after file unlock did not succeed");
        await DeleteCommentAsync(0,0); ApplicationCommands.Save.Execute(null,this); await saveTask;
        using (var reopened=DocumentSession.Open(path))
            if (reopened.Pdf!.Pages[0].Annotations.Count!=0) throw new Exception("Deleted comment was not saved to original");
        var docxPath=Path.Combine(FindArtifacts(),"ctrl-s-protected.docx"); var sentinel=new byte[] {1,2,3,4}; File.WriteAllBytes(docxPath,sentinel);
        using (var docx=new DocumentSession { Path=docxPath,Pdf=DocumentSession.TextPdf(["Readonly DOCX preview"]) })
        {
            failed=false; try { docx.SaveChanges(); } catch (InvalidOperationException) { failed=true; }
            if (!failed || !sentinel.SequenceEqual(File.ReadAllBytes(docxPath))) throw new Exception("PDF bytes overwrote DOCX");
        }
        if (!InputBindings.OfType<KeyBinding>().Any(b=>b.Command==ApplicationCommands.Save && b.Key==Key.S && b.Modifiers==ModifierKeys.Control)) throw new Exception("Ctrl+S binding missing");
        session.Dirty=false;
    }
}
