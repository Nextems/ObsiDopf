using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Drawing;
namespace ObsidianDocs;
public partial class MainWindow
{
    internal async Task TestTextCommentsAsync()
    {
        var folder=FindArtifacts();
        var pdf=new PdfDocument();
        for (int i=0;i<3;i++)
        {
            var p=pdf.AddPage();
            using (var g=XGraphics.FromPdfPage(p))
            {
                var font=new XFont("Arial",18);
                g.DrawString("Alpha beta gamma",font,XBrushes.Black,new XPoint(60,100));
                g.DrawString("Second line text",font,XBrushes.Black,new XPoint(60,140));
            }
            if (i==1) p.Rotate=90;
            if (i==2) { p.Rotate=270; p.CropBox=new PdfRectangle(new XPoint(20,30),new XPoint(p.MediaBox.X2-30,p.MediaBox.Y2-30)); }
        }
        pdf.Pages[0].Annotations.Add(new PdfTextAnnotation { Title="Original",Contents="Unrelated sticky",Rectangle=new PdfRectangle(new XPoint(300,400),new XPoint(320,420)) });
        var session=new DocumentSession { Path=Path.Combine(folder,"text-comments-source.pdf"),Pdf=pdf }; session.Snapshot();
        AddSessionTab(session); await RenderAsync(); await LayoutReady();
        var before=session.Render(0); XPoint? reference=null;
        for (int i=0;i<3;i++)
        {
            await NavigateAsync(i); await LayoutReady();
            SetTool(); TextNoteMode_Click(this,new RoutedEventArgs()); activeSurface=surfaces[i];
            wordsTask=LoadTextWordsAsync(activeSurface); await wordsTask;
            var words=activeSurface.Words!;
            if (words.Count!=6)
            {
                using var debug=UglyToad.PdfPig.PdfDocument.Open(session.RenderBytes!); var p=debug.GetPage(i+1);
                throw new Exception($"Unexpected fixture word count on page {i}: {words.Count}; dims {p.Width}x{p.Height}; "+string.Join("; ",p.GetWords().Select(w=>$"{w.Text}: {w.BoundingBox}")));
            }
            static Point Center(Rect r) => new(r.X+r.Width/2,r.Y+r.Height/2);
            var raw=PdfTextComments.ToPdf(session.Pdf!.Pages[i],Center(words[0].Bounds));
            reference ??= raw;
            if (Math.Abs(raw.X-reference.Value.X)>1 || Math.Abs(raw.Y-reference.Value.Y)>1) throw new Exception($"Text coordinates mismatch on rotated/cropped page {i}: {raw} versus {reference}");
            start=new Point(Center(words[0].Bounds).X*activeSurface.Width,Center(words[0].Bounds).Y*activeSurface.Height);
            var end=Center(words[^1].Bounds);
            await FinishTextSelectionAsync(new Point(end.X*activeSurface.Width,end.Y*activeSurface.Height));
            int bands=PdfTextComments.HighlightBands(selectedWords,activeSurface.Ratio).Count;
            if (selectedWords.Count!=6 || bands<2 || bands>=6 || OcrCard.Visibility!=Visibility.Visible || activeSurface.TextHighlights.Children.Count!=bands) throw new Exception("Multiline text selection failed");
            OcrText.Text="Проверка комментария к тексту"; await ApplyCardAsync();
            var note=session.Pdf!.Pages[i].Annotations[session.Pdf.Pages[i].Annotations.Count-1];
            if (note.Elements.GetName("/Subtype")!="/Highlight" || note.Elements.GetArray("/QuadPoints")?.Elements.Count!=8*bands || note.Elements.GetString("/ODQuote")!="Alpha beta gamma Second line text") throw new Exception("Text comment geometry/quote was not persisted");
        }
        session.SaveTo(Path.Combine(folder,"text-comments.pdf"));
        using (var reopened=DocumentSession.Open(Path.Combine(folder,"text-comments.pdf")))
            if (reopened.Pdf!.Pages[0].Annotations[1].Contents!="Проверка комментария к тексту") throw new Exception("Text comment roundtrip failed");
        var marked=session.Render(0);
        var originalAnchor=session.Pdf!.Pages[0].Annotations[1].Elements.GetArray("/QuadPoints")!.ToString();
        var copiedAnchor=session.Pdf.Pages[1].Annotations[0].Elements.GetArray("/QuadPoints")!.ToString();
        static byte[] Pixels(BitmapSource image) { var bytes=new byte[image.PixelHeight*image.PixelWidth*4]; image.CopyPixels(bytes,image.PixelWidth*4,0); return bytes; }
        if (Pixels(before).SequenceEqual(Pixels(marked))) throw new Exception("Saved PDF highlight is not visible");
        await NavigateAsync(0); await OpenCommentAsync(0,1);
        if (DeleteCommentButton.Visibility!=Visibility.Visible) throw new Exception("Delete button is missing");
        OcrText.Text="Изменённый комментарий"; await ApplyCardAsync();
        if (session.Pdf!.Pages[0].Annotations[1].Elements.GetArray("/QuadPoints")?.ToString()!=originalAnchor) throw new Exception("Editing lost text anchor");
        session.DuplicateWithAuthor(Path.Combine(folder,"text-comments-copy.pdf"),"Copy reviewer");
        using (var copy=DocumentSession.Open(Path.Combine(folder,"text-comments-copy.pdf")))
            if (copy.Pdf!.Pages[1].Annotations[0].Title!="Copy reviewer" || copy.Pdf.Pages[1].Annotations[0].Elements.GetArray("/QuadPoints")?.ToString()!=copiedAnchor) throw new Exception("Copy lost text comment");
        await NavigateAsync(0); await LayoutReady();
        var shot=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32); shot.Render(this);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(shot)); using(var f=File.Create(Path.Combine(folder,"text-comments-ui.png"))) encoder.Save(f);
        await DeleteCommentAsync(0,1);
        if (session.Pdf!.Pages[0].Annotations.Count!=1 || session.Pdf.Pages[0].Annotations[0].Contents!="Unrelated sticky") throw new Exception("Delete removed wrong comment");
        session.SaveTo(Path.Combine(folder,"text-comments-deleted.pdf")); session.SaveTo(Path.Combine(folder,"text-comments-deleted.pdf"));
        using (var deleted=DocumentSession.Open(Path.Combine(folder,"text-comments-deleted.pdf")))
        {
            if (deleted.Pdf!.Pages[0].Annotations.Count!=1 || !Pixels(before).SequenceEqual(Pixels(deleted.Render(0)))) throw new Exception("Highlight remained after deletion");
        }
        await DeleteCommentAsync(0,0);
        if (session.Pdf!.Pages[0].Annotations.Count!=0) throw new Exception("Sticky deletion failed");
        current!.Dirty=false;
    }
}
