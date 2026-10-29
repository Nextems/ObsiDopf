using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf.Annotations;
namespace ObsidianDocs;
public partial class MainWindow
{
    internal async Task TestWheelAsync()
    {
        var folder=FindArtifacts();
        var pdf=DocumentSession.TextPdf(["CONTINUOUS DOCUMENT", "Page 1 of 14"]);
        for (int i=1;i<14;i++)
        {
            var page=pdf.AddPage();
            using var g=XGraphics.FromPdfPage(page);
            g.DrawString($"PAGE {i+1} OF 14",new XFont("Arial",24),XBrushes.DarkSlateBlue,new XPoint(60,100));
        }
        pdf.Pages[0].Annotations.Add(new PdfTextAnnotation { Title=Environment.UserName,Contents="First comment",Rectangle=new PdfSharp.Pdf.PdfRectangle(new XPoint(70,500),new XPoint(90,520)) });
        pdf.Pages[5].Annotations.Add(new PdfTextAnnotation { Title="Reviewer A",Contents="Comment on page six",Rectangle=new PdfSharp.Pdf.PdfRectangle(new XPoint(80,400),new XPoint(100,420)) });
        var session=new DocumentSession { Path=Path.Combine(folder,"continuous-original.pdf"),Pdf=pdf };
        session.Snapshot(); session.SaveTo(session.Path); AddSessionTab(session);
        zoom=.7; await RenderAsync(); await LayoutReady(); await thumbnailTask;
        if (surfaces.Count!=14 || SideList.Items.Count!=14 || CommentList.Children.Count!=2) throw new Exception("Missing page or comment views");
        for (int i=0;i<14;i++)
        {
            var stack=(StackPanel)((ListBoxItem)SideList.Items[i]).Content;
            var image=(Image)((Border)stack.Children[1]).Child;
            if (image.Source is null || image.Height<50) throw new Exception($"Thumbnail {i+1} is missing");
        }
        // Exercise the real WPF bubbling wheel handler, not a custom page switch.
        double before=Viewer.VerticalOffset;
        Viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120) { RoutedEvent=Mouse.MouseWheelEvent });
        await LayoutReady();
        if (Viewer.VerticalOffset<=before) throw new Exception("Actual wheel event did not scroll the document");
        Viewer.ScrollToVerticalOffset(surfaces[1].Top-100); await LayoutReady(); await RenderVisibleAsync();
        if (surfaces[0].Image.Source is null || surfaces[1].Image.Source is null) throw new Exception("Adjacent pages are not rendered across seam");
        if (Math.Abs(surfaces[1].Top-(surfaces[0].Top+surfaces[0].Height+24))>.1) throw new Exception("Noncontinuous page layout");
        await NavigateAsync(5); await LayoutReady();
        if (current!.Page!=5 || surfaces[5].Image.Source is null) throw new Exception("Thumbnail navigation failed");
        Viewer.ScrollToEnd(); await LayoutReady(); await RenderVisibleAsync();
        if (current.Page!=13) throw new Exception("Current page did not track scrolling");
        await NavigateAsync(0); await LayoutReady();
        // A comment must attach to the clicked page even when the viewport changes.
        activeSurface=surfaces[1]; await EnsurePageAsync(1,viewGeneration);
        selection=new Rect(40,50,1,1);
        ShowCard(selection,true); OcrText.Text="New comment on page two";
        await ApplyCardAsync();
        if (session.Pdf!.Pages[1].Annotations[0].Title!=authors.Selected || session.Pdf.Pages[0].Annotations.Count!=1) throw new Exception("New comment attached to wrong page or author");
        await OpenCommentAsync(5,0); OcrText.Text="Comment on page six"; await ApplyCardAsync();
        if (CommentList.Children.Count!=3 || surfaces[1].Markers.Children.Count!=1) throw new Exception("Comment column or page markers failed");
        session.SaveTo(session.Path);
        var original=File.ReadAllBytes(session.Path);
        var copyPath=Path.Combine(folder,"continuous-copy.pdf");
        session.DuplicateWithAuthor(copyPath,"Другой автор");
        using (var copy=DocumentSession.Open(copyPath))
        {
            if (copy.Count!=14) throw new Exception("Copy lost pages");
            int notes=0;
            foreach (var page in copy.Pdf!.Pages)
                foreach (PdfAnnotation a in page.Annotations)
                    if (!string.IsNullOrWhiteSpace(a.Contents)) { if (a.Title!="Другой автор") throw new Exception("Copy author was not replaced"); notes++; }
            if (notes!=3 || copy.Pdf.Pages[5].Annotations[0].Contents!="Comment on page six") throw new Exception("Copy changed comments");
            using var text=UglyToad.PdfPig.PdfDocument.Open(copy.RenderBytes!);
            for (int p=2;p<=14;p++) if (!text.GetPage(p).Text.Contains($"PAGE {p} OF 14")) throw new Exception("Copy changed page contents");
        }
        if (!original.SequenceEqual(File.ReadAllBytes(session.Path)) || session.Pdf!.Pages[0].Annotations[0].Title!=Environment.UserName) throw new Exception("Copy changed original");
        bool rejected=false; try { session.DuplicateWithAuthor(session.Path,"Other"); } catch (InvalidOperationException) { rejected=true; }
        if (!rejected) throw new Exception("Copy allowed overwriting original");
        var profilesFile=Path.Combine(folder,"test-authors.json");
        var profiles=new AuthorProfiles(profilesFile); profiles.Choose("Custom reviewer");
        var reload=new AuthorProfiles(profilesFile);
        if (!reload.Names.Contains(Environment.UserName) || reload.Selected!="Custom reviewer") throw new Exception("Author profiles did not persist");
        await NavigateAsync(0); await LayoutReady(); await RenderVisibleAsync();
        var shot=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32); shot.Render(this);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(shot)); using(var f=File.Create(Path.Combine(folder,"continuous-interface.png"))) encoder.Save(f);
        current.Dirty=false;
    }
    async Task LayoutReady() => await Dispatcher.InvokeAsync(()=>Viewer.UpdateLayout(),System.Windows.Threading.DispatcherPriority.ContextIdle);
    internal static string FindArtifacts()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName,"build.ps1"))) root=root.Parent;
        var path=File.Exists(Path.Combine(root.FullName,"build.ps1")) ? Path.Combine(root.FullName,"artifacts") : Path.Combine(Path.GetTempPath(),"ObsidianDocs","tests");
        Directory.CreateDirectory(path); return path;
    }
}
