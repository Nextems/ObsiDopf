using System.IO;
using System.Text;
using System.Globalization;
using System.Windows;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Drawing;

namespace ObsidianDocs;
public sealed record TextWord(string Text, Rect Bounds);
public static class PdfTextComments
{
    public static List<TextWord> ReadWords(byte[] bytes,int pageIndex)
    {
        using var pdf=UglyToad.PdfPig.PdfDocument.Open(bytes);
        return ReadWords(pdf.GetPage(pageIndex+1));
    }
    public static List<TextWord> ReadWords(UglyToad.PdfPig.Content.Page page)
    {
        if (page.Width<=0 || page.Height<=0) return [];
        var order=new Dictionary<UglyToad.PdfPig.Content.Letter,int>();
        for (int i=0;i<page.Letters.Count;i++) order.TryAdd(page.Letters[i],i);
        return page.GetWords(UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor.NearestNeighbourWordExtractor.Instance).Where(w=>!string.IsNullOrWhiteSpace(w.Text)).OrderBy(w=>w.Letters.Min(l=>order.GetValueOrDefault(l,int.MaxValue))).Select(w=>
        {
            var b=w.BoundingBox;
            var points=new[] { b.TopLeft,b.TopRight,b.BottomLeft,b.BottomRight };
            double left=points.Min(p=>p.X),right=points.Max(p=>p.X),bottom=points.Min(p=>p.Y),top=points.Max(p=>p.Y);
            return new TextWord(w.Text,new Rect(left/page.Width,(page.Height-top)/page.Height,(right-left)/page.Width,(top-bottom)/page.Height));
        }).Where(w=>w.Bounds.Width>0 && w.Bounds.Height>0 && w.Bounds.IntersectsWith(new Rect(0,0,1,1))).ToList();
    }
    public static List<TextWord> Select(IReadOnlyList<TextWord> words,Point start,Point end)
    {
        if (words.Count==0) return [];
        static double Distance(Rect r,Point p) { double dx=Math.Max(0,Math.Max(r.Left-p.X,p.X-r.Right)),dy=Math.Max(0,Math.Max(r.Top-p.Y,p.Y-r.Bottom)); return dx*dx+dy*dy; }
        int first=Enumerable.Range(0,words.Count).MinBy(i=>Distance(words[i].Bounds,start));
        if (Distance(words[first].Bounds,start)>Math.Pow(Math.Max(.012,words[first].Bounds.Height),2)) return [];
        int last=Enumerable.Range(0,words.Count).MinBy(i=>Distance(words[i].Bounds,end));
        return words.Skip(Math.Min(first,last)).Take(Math.Abs(last-first)+1).ToList();
    }
    public static PdfRectangle VisibleBox(PdfPage page)
    {
        var m=page.MediaBox;
        // PdfPage.CropBox's getter creates an empty /CropBox if absent.
        // Reading geometry must never insert a zero-size crop into the PDF.
        var c=page.Elements.GetRectangle("/CropBox",false);
        if (c.Width<=0 || c.Height<=0) return m;
        double l=Math.Max(m.X1,c.X1),b=Math.Max(m.Y1,c.Y1),r=Math.Min(m.X2,c.X2),t=Math.Min(m.Y2,c.Y2);
        return r>l && t>b ? new PdfRectangle(new XPoint(l,b),new XPoint(r,t)) : m;
    }
    public static List<Rect> HighlightBands(IReadOnlyList<TextWord> words,double pageAspect)
    {
        // Word boxes have different heights (capitals, descenders, punctuation).
        // Group by baseline area first, then use one height for the whole row.
        var lines=new List<List<Rect>>();
        foreach(var word in words.OrderBy(w=>w.Bounds.Top).ThenBy(w=>w.Bounds.Left))
        {
            var rect=Rect.Intersect(word.Bounds,new Rect(0,0,1,1));
            if(rect.IsEmpty || rect.Width<=0 || rect.Height<=0) continue;
            List<Rect>? best=null; double distance=double.MaxValue;
            foreach(var line in lines)
            {
                double center=line.Average(r=>(r.Top+r.Bottom)/2),height=line.Average(r=>r.Height);
                double delta=Math.Abs((rect.Top+rect.Bottom)/2-center);
                double overlap=Math.Min(rect.Bottom,center+height/2)-Math.Max(rect.Top,center-height/2);
                if(delta<=Math.Max(height,rect.Height)*.45 && overlap>=Math.Min(height,rect.Height)*.35 && delta<distance) { best=line; distance=delta; }
            }
            if(best is null) { best=[]; lines.Add(best); }
            best.Add(rect);
        }
        var result=new List<Rect>();
        foreach(var line in lines)
        {
            double top=line.Min(r=>r.Top),bottom=line.Max(r=>r.Bottom);
            // Convert the row height to horizontal page units. Bridge normal
            // and justified spaces, while keeping wide column gutters separate.
            double maxGap=Math.Max(.01,(bottom-top)*pageAspect*4);
            Rect band=Rect.Empty;
            foreach(var rect in line.OrderBy(r=>r.Left))
            {
                if(!band.IsEmpty && rect.Left-band.Right>maxGap) { result.Add(band); band=Rect.Empty; }
                if(band.IsEmpty) band=new Rect(rect.Left,top,rect.Width,bottom-top);
                else band.Union(new Rect(rect.Left,top,rect.Width,bottom-top));
            }
            if(!band.IsEmpty) result.Add(band);
        }
        return result;
    }
    public static XPoint ToPdf(PdfPage page,Point point)
    {
        double u=point.X,v=point.Y;
        (u,v)=(((page.Rotate%360)+360)%360) switch { 90=>(v,1-u),180=>(1-u,1-v),270=>(1-v,u),_=>(u,v) };
        var box=VisibleBox(page);
        return new XPoint(box.X1+u*box.Width,box.Y2-v*box.Height);
    }
    public static PdfAnnotation Add(PdfDocument document,int pageIndex,IReadOnlyList<TextWord> selected,string text,string author)
    {
        if (selected.Count==0 || string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Выделите текст и введите комментарий.");
        var page=document.Pages[pageIndex];
        var quad=new PdfArray(document); var boxes=new List<(double X,double Y,double W,double H)>();
        var visible=VisibleBox(page);
        double aspect=page.Rotate%180==0 ? visible.Height/visible.Width : visible.Width/visible.Height;
        foreach (var r in HighlightBands(selected,aspect))
        {
            var points=new[] { ToPdf(page,r.TopLeft),ToPdf(page,r.TopRight),ToPdf(page,r.BottomLeft),ToPdf(page,r.BottomRight) };
            foreach (var p in points) { quad.Elements.Add(new PdfReal(p.X)); quad.Elements.Add(new PdfReal(p.Y)); }
            double l=points.Min(p=>p.X),b=points.Min(p=>p.Y),right=points.Max(p=>p.X),top=points.Max(p=>p.Y);
            boxes.Add((l,b,right-l,top-b));
        }
        double x=boxes.Min(r=>r.X),y=boxes.Min(r=>r.Y),x2=boxes.Max(r=>r.X+r.W),y2=boxes.Max(r=>r.Y+r.H);
        var annotation=new TextHighlight(document) { Title=author,Contents=text,Color=XColors.DodgerBlue,Opacity=.3,Flags=PdfAnnotationFlags.Print,Rectangle=new PdfRectangle(new XPoint(x,y),new XPoint(x2,y2)) };
        annotation.Elements["/QuadPoints"]=quad;
        annotation.Elements["/ODQuote"]=new PdfString(string.Join(" ",selected.Select(w=>w.Text)),PdfStringEncoding.Unicode);
        annotation.Elements.SetString("/NM",Guid.NewGuid().ToString("N"));
        // An explicit appearance makes highlights visible in readers which do
        // not synthesize appearances for new markup annotations.
        var form=new PdfDictionary(document); form.Elements.SetName("/Type","/XObject"); form.Elements.SetName("/Subtype","/Form");
        form.Elements["/BBox"]=new PdfArray(document,new PdfReal(x),new PdfReal(y),new PdfReal(x2),new PdfReal(y2));
        var resources=new PdfDictionary(document); var states=new PdfDictionary(document); var gs=new PdfDictionary(document);
        gs.Elements.SetReal("/ca",.3); gs.Elements.SetName("/BM","/Multiply"); states.Elements["/H"]=gs; resources.Elements["/ExtGState"]=states; form.Elements["/Resources"]=resources;
        var content=new StringBuilder("q /H gs 0.12 0.48 0.95 rg\n");
        foreach (var r in boxes) content.AppendFormat(CultureInfo.InvariantCulture,"{0} {1} {2} {3} re f\n",r.X,r.Y,r.W,r.H);
        content.Append("Q"); form.CreateStream(Encoding.ASCII.GetBytes(content.ToString())); document.Internals.AddObject(form);
        var appearance=new PdfDictionary(document); appearance.Elements["/N"]=form.Reference!; annotation.Elements["/AP"]=appearance;
        page.Annotations.Add(annotation); return annotation;
    }
    sealed class TextHighlight : PdfAnnotation
    {
        public TextHighlight(PdfDocument document):base(document) { Elements.SetName("/Subtype","/Highlight"); }
    }
}
