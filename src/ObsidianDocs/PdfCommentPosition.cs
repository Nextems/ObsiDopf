using System.Windows;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;

namespace ObsidianDocs;

internal static class PdfCommentPosition
{
    const string PositionKey="/ODMarkerPosition";
    internal const double IconSize=24;

    internal static Point Get(PdfPage page,PdfAnnotation note,Size size)
    {
        var box=PdfTextComments.VisibleBox(page);
        var stored=note.Elements.GetArray(PositionKey);
        bool custom=stored?.Elements.Count==2;
        double x=custom ? stored!.Elements.GetReal(0) : note.Rectangle.X1;
        double y=custom ? stored!.Elements.GetReal(1) : note.Rectangle.Y2;
        if(!double.IsFinite(x) || !double.IsFinite(y)) { custom=false; x=note.Rectangle.X1; y=note.Rectangle.Y2; }
        double u=(x-box.X1)/box.Width,v=(box.Y2-y)/box.Height;
        (u,v)=(((page.Rotate%360)+360)%360) switch { 90=>(1-v,u),180=>(1-u,1-v),270=>(v,1-u),_=>(u,v) };
        // Until moved, text-linked notes sit in the right margin.
        return Clamp(new Point(!custom && note.Elements.GetName("/Subtype")=="/Highlight" ? size.Width-28 : u*size.Width,v*size.Height),size);
    }

    internal static Point Clamp(Point position,Size size)=>new(
        Math.Clamp(position.X,0,Math.Max(0,size.Width-IconSize)),
        Math.Clamp(position.Y,0,Math.Max(0,size.Height-IconSize)));

    internal static void Move(PdfPage page,int annotationIndex,Point position,Size size)
    {
        if(size.Width<=0 || size.Height<=0 || !double.IsFinite(size.Width) || !double.IsFinite(size.Height) || !double.IsFinite(position.X) || !double.IsFinite(position.Y))
            throw new ArgumentException("Некорректная позиция комментария.");
        var note=page.Annotations[annotationIndex];
        position=Clamp(position,size);
        var anchor=PdfTextComments.ToPdf(page,new Point(position.X/size.Width,position.Y/size.Height));
        if(note.Elements.GetName("/Subtype")=="/Text")
        {
            // Move the native sticky note as well, so other PDF readers see it.
            var rectangle=note.Rectangle;
            double dx=anchor.X-rectangle.X1,dy=anchor.Y-rectangle.Y2;
            note.Rectangle=Shift(rectangle,dx,dy);
            if(note.Elements.GetDictionary("/Popup") is { } popup && popup.Elements.ContainsKey("/Rect"))
                popup.Elements.SetRectangle("/Rect",Shift(popup.Elements.GetRectangle("/Rect"),dx,dy));
        }
        // Markup rectangles/quad points stay anchored to the original text.
        var value=new PdfArray(page.Owner);
        value.Elements.Add(new PdfReal(anchor.X)); value.Elements.Add(new PdfReal(anchor.Y));
        note.Elements[PositionKey]=value;
    }

    static PdfRectangle Shift(PdfRectangle rectangle,double dx,double dy)=>new(
        new XPoint(rectangle.X1+dx,rectangle.Y1+dy),new XPoint(rectangle.X2+dx,rectangle.Y2+dy));
}
