using System.IO;
using System.Windows;
using ObsidianDocs;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        var directory=Path.Combine(Path.GetTempPath(),"obsidopf-comment-drag-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach(int rotation in new[] { 0,90,180,270 })
            foreach(bool cropped in new[] { false,true })
            {
                string path=Path.Combine(directory,$"notes-{rotation}-{cropped}.pdf");
                CreatePdf(path,rotation,cropped);
                using var session=DocumentSession.Open(path);
                var page=session.Pdf!.Pages[0];
                var box=PdfTextComments.VisibleBox(page);
                var size=rotation%180==0 ? new Size(800,800*box.Height/box.Width) : new Size(800,800*box.Width/box.Height);
                var originalBytes=File.ReadAllBytes(path);
                var rectangle=page.Annotations[0].Rectangle;
                var quote=page.Annotations[1].Elements.GetString("/ODQuote");
                var highlightRect=page.Annotations[1].Rectangle;
                var quad=page.Annotations[1].Elements.GetArray("/QuadPoints")!.ToString();
                var appearance=page.Annotations[1].Elements.GetDictionary("/AP")!.ToString();
                var originalMarker=PdfCommentPosition.Get(page,page.Annotations[0],size);

                var target=new Point(211,173);
                session.ApplyPdfEdit(pdf=>PdfCommentPosition.Move(pdf.Pages[0],0,target,size));
                Check(session.Dirty && session.CanUndoPdf,"Move records an undo entry and marks document dirty");
                Check(File.ReadAllBytes(path).SequenceEqual(originalBytes),"Dragging does not write the file before Save");
                PointNear(PdfCommentPosition.Get(session.Pdf!.Pages[0],session.Pdf.Pages[0].Annotations[0],size),target,"Sticky note position");
                var moved=session.Pdf.Pages[0].Annotations[0];
                Near(moved.Rectangle.Width,rectangle.Width,"Native width preserved");
                Near(moved.Rectangle.Height,rectangle.Height,"Native height preserved");
                var nativeAnchor=PdfTextComments.ToPdf(session.Pdf.Pages[0],new Point(target.X/size.Width,target.Y/size.Height));
                Near(moved.Rectangle.X1,nativeAnchor.X,"Native annotation moved horizontally");
                Near(moved.Rectangle.Y2,nativeAnchor.Y,"Native annotation moved vertically");
                var popup=moved.Elements.GetDictionary("/Popup")!;
                Near(popup.Elements.GetRectangle("/Rect").X1-400,moved.Rectangle.X1-rectangle.X1,"Linked popup moves with sticky note");

                session.UndoPdfEdit();
                PointNear(PdfCommentPosition.Get(session.Pdf!.Pages[0],session.Pdf.Pages[0].Annotations[0],size),originalMarker,"Undo restores position");
                Check(!session.CanUndoPdf,"One drop creates one undo entry");

                // Markup bubbles move independently; words and native highlight stay fixed.
                session.ApplyPdfEdit(pdf=>PdfCommentPosition.Move(pdf.Pages[0],1,new Point(143,225),size));
                var highlight=session.Pdf!.Pages[0].Annotations[1];
                Check(highlight.Rectangle==highlightRect,"Highlight bounds unchanged");
                Check(highlight.Elements.GetArray("/QuadPoints")!.ToString()==quad,"Quad points unchanged");
                Check(highlight.Elements.GetDictionary("/AP")!.ToString()==appearance,"Highlight appearance unchanged");
                Check(highlight.Elements.GetString("/ODQuote")==quote,"Quoted text unchanged");
                Check(highlight.Title=="Original author" && highlight.Contents=="Text comment","Author and contents unchanged");
                session.SaveChanges();
                using(var reopened=DocumentSession.Open(path))
                    PointNear(PdfCommentPosition.Get(reopened.Pdf!.Pages[0],reopened.Pdf.Pages[0].Annotations[1],size),new Point(143,225),"Position persists after save/reopen");

                // Repeated drags use the latest annotation and clamp to page bounds.
                session.ApplyPdfEdit(pdf=>PdfCommentPosition.Move(pdf.Pages[0],0,new Point(-500,50000),size));
                PointNear(PdfCommentPosition.Get(session.Pdf!.Pages[0],session.Pdf.Pages[0].Annotations[0],size),new Point(0,size.Height-24),"Clamped to page bounds");
                session.ApplyPdfEdit(pdf=>PdfCommentPosition.Move(pdf.Pages[0],0,new Point(50000,-500),size));
                PointNear(PdfCommentPosition.Get(session.Pdf!.Pages[0],session.Pdf.Pages[0].Annotations[0],size),new Point(size.Width-24,0),"Clamped to opposite bounds");
                session.UndoPdfEdit();
                PointNear(PdfCommentPosition.Get(session.Pdf!.Pages[0],session.Pdf.Pages[0].Annotations[0],size),new Point(0,size.Height-24),"Undo repeated move");
                var stable=session.RenderBytes;
                try { session.ApplyPdfEdit(pdf=>PdfCommentPosition.Move(pdf.Pages[0],0,new Point(double.NaN,0),size)); throw new Exception("Invalid position accepted"); }
                catch(ArgumentException) { Check(ReferenceEquals(stable,session.RenderBytes),"Failed move preserves document"); }
                Console.WriteLine($"PASS rotation={rotation}, crop={cropped}: native/markup movement, save, undo, clamping");
            }
        }
        finally { Directory.Delete(directory,true); }
    }

    static void CreatePdf(string path,int rotation,bool cropped)
    {
        using var pdf=new PdfDocument(); var page=pdf.AddPage();
        page.MediaBox=new PdfRectangle(new XPoint(10,20),new XPoint(610,820));
        if(cropped) page.CropBox=new PdfRectangle(new XPoint(50,80),new XPoint(550,760));
        page.Rotate=rotation;
        var sticky=new PdfTextAnnotation(pdf) { Title="Original author",Contents="Sticky comment",Rectangle=new PdfRectangle(new XPoint(120,600),new XPoint(140,620)) };
        var popup=new PdfDictionary(pdf); popup.Elements.SetName("/Subtype","/Popup"); popup.Elements.SetRectangle("/Rect",new PdfRectangle(new XPoint(400,400),new XPoint(500,600)));
        pdf.Internals.AddObject(popup); sticky.Elements["/Popup"]=popup.Reference!; page.Annotations.Add(sticky);
        PdfTextComments.Add(pdf,0,new[] { new TextWord("Selected",new Rect(.15,.25,.1,.025)),new TextWord("words",new Rect(.26,.25,.08,.025)) },"Text comment","Original author");
        pdf.Save(path);
    }
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    static void Near(double actual,double expected,string message)=>Check(Math.Abs(actual-expected)<.02,$"{message}: {actual} != {expected}");
    static void PointNear(Point actual,Point expected,string message) { Near(actual.X,expected.X,message+" X"); Near(actual.Y,expected.Y,message+" Y"); }
}
