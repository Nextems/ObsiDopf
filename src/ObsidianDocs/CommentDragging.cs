using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ObsidianDocs;

public partial class MainWindow
{
    sealed class CommentDrag(DocumentSession session,PageSurface surface,int index,Button marker,Point pointer,Point origin)
    {
        public DocumentSession Session { get; }=session;
        public byte[]? Snapshot { get; }=session.RenderBytes;
        public PageSurface Surface { get; }=surface;
        public Size Size { get; }=new(surface.Width,surface.Height);
        public int Index { get; }=index;
        public Button Marker { get; }=marker;
        public Point Pointer { get; }=pointer;
        public Point Origin { get; }=origin;
        public Point Position { get; set; }=origin;
        public bool Moved { get; set; }
    }
    CommentDrag? commentDrag;

    void EnableCommentDragging(Button marker,PageSurface surface,int index)
    {
        marker.ToolTip+="\nПеретащите значок, чтобы переместить · Esc — отменить";
        marker.PreviewMouseLeftButtonDown+=(_,e)=>
        {
            if(busy || current?.CanEditPdf!=true) return;
            HideCard();
            commentDrag=new CommentDrag(current,surface,index,marker,e.GetPosition(surface.Markers),new Point(Canvas.GetLeft(marker),Canvas.GetTop(marker)));
        };
        marker.PreviewMouseMove+=(_,e)=>
        {
            if(commentDrag is not { } drag || drag.Marker!=marker) return;
            if(e.LeftButton!=MouseButtonState.Pressed) { CancelCommentDrag(); return; }
            if(current!=drag.Session || current.RenderBytes!=drag.Snapshot || drag.Size!=new Size(surface.Width,surface.Height)) { CancelCommentDrag(); return; }
            var delta=e.GetPosition(surface.Markers)-drag.Pointer;
            if(!drag.Moved && Math.Abs(delta.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y)<SystemParameters.MinimumVerticalDragDistance) return;
            drag.Moved=true;
            marker.Cursor=Cursors.SizeAll;
            if(!marker.IsMouseCaptured && !marker.CaptureMouse()) { CancelCommentDrag(); return; }
            drag.Position=PdfCommentPosition.Clamp(drag.Origin+delta,drag.Size);
            Canvas.SetLeft(marker,drag.Position.X); Canvas.SetTop(marker,drag.Position.Y);
            e.Handled=true;
        };
        marker.PreviewMouseLeftButtonUp+=async (_,e)=>
        {
            if(commentDrag is not { } drag || drag.Marker!=marker) return;
            commentDrag=null;
            if(!drag.Moved) return; // A normal click still opens the note.
            e.Handled=true;
            marker.Cursor=Cursors.Hand;
            if(marker.IsMouseCaptured) marker.ReleaseMouseCapture();
            if(busy || current!=drag.Session || current.RenderBytes!=drag.Snapshot || !surfaces.Contains(surface)) { BuildMarkers(surface); return; }
            if((drag.Position-drag.Origin).Length<.5) { BuildMarkers(surface); return; }
            try
            {
                SetBusy(true,"Перемещаю комментарий…");
                current.ApplyPdfEdit(pdf=>PdfCommentPosition.Move(pdf.Pages[surface.Index],index,drag.Position,drag.Size));
                await RefreshEditedPageAsync(surface.Index);
                Status.Text="Комментарий перемещён · Ctrl+S — сохранить · Ctrl+Z — отменить";
            }
            catch(Exception error) { BuildMarkers(surface); Error(error); }
            finally { SetBusy(false,Status.Text); }
        };
        marker.LostMouseCapture+=(_,_)=> { if(commentDrag?.Marker==marker) CancelCommentDrag(); };
    }

    void CancelCommentDrag()
    {
        if(commentDrag is not { } drag) return;
        commentDrag=null;
        Canvas.SetLeft(drag.Marker,drag.Origin.X); Canvas.SetTop(drag.Marker,drag.Origin.Y);
        drag.Marker.Cursor=Cursors.Hand;
        if(drag.Marker.IsMouseCaptured) drag.Marker.ReleaseMouseCapture();
    }
}
