using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ObsidianDocs;
public partial class MainWindow
{
    bool commentsExpanded=true;
    void ConfigureDocumentCommands()
    {
        PreviewKeyDown+=Document_PreviewKeyDown;
        PreviewMouseWheel+=(_,e)=>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || !(Viewer.IsMouseOver || DocxHost.IsMouseOver)) return;
            e.Handled=true;
            if(!busy && current is not null) ChangeZoom(zoom+Math.Sign(e.Delta)*.1,e.GetPosition(Viewer));
        };
    }
    async void Document_PreviewKeyDown(object sender,KeyEventArgs e)
    {
        if(AiChatPanel.IsKeyboardFocusWithin && e.Key is Key.Z or Key.Y) return;
        if(!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        if(e.Key==Key.N) { e.Handled=true; NewDocx_Click(this,new RoutedEventArgs()); return; }
        if(e.Key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0)
        {
            e.Handled=true;
            if(busy || current is null) return;
            ChangeZoom(e.Key is Key.D0 or Key.NumPad0 ? 1 : zoom+(e.Key is Key.OemPlus or Key.Add ? .1 : -.1));
        }
        else if(e.Key==Key.Z && current?.CanEditPdf==true && !busy)
        {
            e.Handled=true;
            try { await UndoPdfEditAsync(); } catch(Exception error) { Error(error); }
        }
    }
    void ChangeZoom(double value,Point? mouse=null)
    {
        if(busy || current is null) return;
        value=Math.Clamp(Math.Round(value,2),.3,4);
        if(Math.Abs(value-zoom)<.001) return;
        HideCard();
        double ratio=value/zoom;
        if(current.IsWordDocument)
        {
            zoom=value; current.Editor!.SetZoom(zoom);
        }
        else
        {
            var point=mouse ?? new Point(Viewer.ViewportWidth/2,Viewer.ViewportHeight/2);
            var surface=WindowSurfaces.LastOrDefault(p=>p.Top<=Viewer.VerticalOffset+point.Y) ?? WindowSurfaces.FirstOrDefault();
            double fraction=surface is null ? 0 : (Viewer.VerticalOffset+point.Y-surface.Top)/surface.Height;
            double x=(Viewer.HorizontalOffset+point.X)*ratio-point.X;
            zoom=value; ResizePage(); Viewer.UpdateLayout();
            if(surface is not null) Viewer.ScrollToVerticalOffset(surface.Top+fraction*surface.Height-point.Y);
            Viewer.ScrollToHorizontalOffset(x);
            _=RenderVisibleAsync();
        }
        ZoomStatus.Text=$"{zoom:P0}";
    }
    void UpdateCommentPanel()
    {
        bool visible=current?.CanEditPdf==true && commentsExpanded;
        CommentPanel.Visibility=visible ? Visibility.Visible : Visibility.Collapsed;
        CommentColumn.Width=new GridLength(visible ? 270 : 0);
        SideCommentsButton.ToolTip=visible ? "Панель комментариев открыта" : "Открыть панель комментариев";
    }
    void SetCommentPanel(bool expanded)
    {
        if(busy) return;
        HideCard(); commentsExpanded=expanded; UpdateCommentPanel(); ResizePage();
    }
    void HideComments_Click(object sender,RoutedEventArgs e)=>SetCommentPanel(false);
    async void NewDocx_Click(object sender,RoutedEventArgs e)
    {
        if(busy) return;
        try
        {
            SetBusy(true,"Создаю DOCX…"); HideCard();
            using var stream=new MemoryStream();
            using(var package=WordprocessingDocument.Create(stream,WordprocessingDocumentType.Document,true))
            {
                var main=package.AddMainDocumentPart();
                DocxCompatibility.AddMissingParts(main);
                main.Document=new W.Document(new W.Body(new W.Paragraph(),new W.SectionProperties(
                    new W.PageSize { Width=11906,Height=16838 },
                    new W.PageMargin { Top=1134,Bottom=1134,Left=1134,Right=1134,Header=567,Footer=567,Gutter=0 })));
                main.Document.Save();
            }
            int number=1; string name;
            do { name=number==1 ? "Новый документ.docx" : $"Новый документ {number}.docx"; number++; }
            while(sessions.Any(s=>s.Name.Equals(name,StringComparison.OrdinalIgnoreCase)));
            var session=new DocumentSession { Path=name,DocxBytes=stream.ToArray(),IsUntitled=true };
            AddSessionTab(session); await RenderAsync(); session.Editor!.Focus();
            Status.Text="Новый DOCX · Ctrl+S — выбрать имя и сохранить";
        }
        catch(Exception error) { Error(error); }
        finally { SetBusy(false,Status.Text); }
        if(current?.IsUntitled==true) current.Editor?.Focus();
    }
}
