using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ObsidianDocs;
internal sealed class WordMiniMap : FrameworkElement
{
    const double Padding=10;
    readonly PagedWordView pages;
    readonly ScrollViewer scroll;
    readonly DocxEditor editor;
    readonly DispatcherTimer edgeScroll;
    public ScrollViewer View { get; }
    Rect map,viewport;
    bool dragging;
    double grab,scale;
    public WordMiniMap(PagedWordView pages,ScrollViewer scroll,DocxEditor editor)
    {
        this.pages=pages; this.scroll=scroll; this.editor=editor;
        Cursor=Cursors.Hand;
        ToolTip="Тяните рамку для прокрутки документа. Полоса справа и колесо прокручивают мини-карту.";
        View=new ScrollViewer
        {
            Content=this,HorizontalContentAlignment=HorizontalAlignment.Stretch,
            VerticalContentAlignment=VerticalAlignment.Top,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            CanContentScroll=false,Background=new SolidColorBrush(Color.FromRgb(30,30,36))
        };
        View.ScrollChanged+=(_,e)=>
        {
            if(Math.Abs(e.ViewportWidthChange)>.1 || Math.Abs(e.ViewportHeightChange)>.1) RefreshMetrics();
            InvalidateVisual();
        };
        edgeScroll=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(30) };
        edgeScroll.Tick+=(_,_)=>
        {
            if(!dragging) return;
            double y=Mouse.GetPosition(View).Y;
            double delta=y<24 ? -18 : y>View.ViewportHeight-24 ? 18 : 0;
            if(delta==0) return;
            View.ScrollToVerticalOffset(View.VerticalOffset+delta);
            Seek(Mouse.GetPosition(this).Y);
        };
        Loaded+=(_,_)=>
        {
            scroll.ScrollChanged+=ScrollChanged; pages.PagesChanged+=PagesChanged; pages.MiniaturesChanged+=MiniaturesChanged;
            RefreshMetrics(); EnsureFrameVisible();
        };
        Unloaded+=(_,_)=>
        {
            scroll.ScrollChanged-=ScrollChanged; pages.PagesChanged-=PagesChanged; pages.MiniaturesChanged-=MiniaturesChanged;
            edgeScroll.Stop(); dragging=false;
        };
        SizeChanged+=(_,_)=>RefreshMetrics();
    }
    void RefreshMetrics()
    {
        double width=View.ViewportWidth>25 ? View.ViewportWidth : ActualWidth;
        double height=View.ViewportHeight>25 ? View.ViewportHeight : View.ActualHeight;
        if(width<25 || height<25) return;
        // Five sheets at a time, independently of the total document length.
        scale=Math.Min((width-2*Padding)/pages.PaperWidth,(height-2*Padding)/(5*(pages.PaperHeight+24)-24));
        scale=Math.Max(.005,scale);
        double total=pages.PageCount*(pages.PaperHeight+24)-24;
        map=new Rect((width-pages.PaperWidth*scale)/2,Padding,pages.PaperWidth*scale,total*scale);
        double desired=map.Bottom+Padding;
        if(double.IsNaN(Height) || Math.Abs(Height-desired)>.1) Height=desired;
        InvalidateVisual();
    }
    void ScrollChanged(object sender,ScrollChangedEventArgs e)
    {
        if(!dragging) EnsureFrameVisible();
        InvalidateVisual();
    }
    void PagesChanged() { RefreshMetrics(); EnsureFrameVisible(); }
    void MiniaturesChanged()=>InvalidateVisual();
    void UpdateFrame()
    {
        if(map.IsEmpty || map.Height<=0) return;
        double height=Math.Min(map.Height,Math.Max(22,map.Height*scroll.ViewportHeight/Math.Max(1,scroll.ExtentHeight)));
        double ratio=scroll.ScrollableHeight>0 ? scroll.VerticalOffset/scroll.ScrollableHeight : 0;
        viewport=new Rect(map.Left-5,map.Top+ratio*(map.Height-height),map.Width+10,height);
    }
    void EnsureFrameVisible()
    {
        UpdateFrame();
        if(viewport.IsEmpty || View.ViewportHeight<=0) return;
        if(viewport.Top<View.VerticalOffset) View.ScrollToVerticalOffset(Math.Max(0,viewport.Top-Padding));
        else if(viewport.Bottom>View.VerticalOffset+View.ViewportHeight) View.ScrollToVerticalOffset(viewport.Bottom-View.ViewportHeight+Padding);
    }
    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(30,30,36)),null,new Rect(RenderSize));
        if(ActualWidth<25 || scale<=0 || map.Height<=0) return;
        double stride=(pages.PaperHeight+24)*scale;
        int first=Math.Max(0,(int)((View.VerticalOffset-map.Top)/stride)-1);
        int last=Math.Min(pages.PageCount-1,(int)((View.VerticalOffset+View.ViewportHeight-map.Top)/stride)+1);
        for(int page=first;page<=last;page++)
        {
            var target=new Rect(map.X,map.Y+page*stride,map.Width,pages.PaperHeight*scale);
            pages.DrawMiniature(drawing,target,page);
        }
        UpdateFrame();
        drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40,179,154,245)),new Pen(new SolidColorBrush(Color.FromRgb(179,154,245)),2),viewport,3,3);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if(map.IsEmpty || map.Height<=0) return;
        var point=e.GetPosition(this); UpdateFrame();
        grab=viewport.Contains(point) ? point.Y-viewport.Top : viewport.Height/2;
        dragging=true; CaptureMouse(); edgeScroll.Start(); Seek(point.Y); e.Handled=true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if(!dragging || e.LeftButton!=MouseButtonState.Pressed) return;
        Seek(e.GetPosition(this).Y); e.Handled=true;
    }
    void Seek(double y)
    {
        double travel=map.Height-viewport.Height;
        double ratio=travel>0 ? Math.Clamp((y-grab-map.Top)/travel,0,1) : 0;
        scroll.ScrollToVerticalOffset(ratio*scroll.ScrollableHeight);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e); dragging=false; edgeScroll.Stop(); ReleaseMouseCapture(); e.Handled=true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e); dragging=false; edgeScroll.Stop();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        View.ScrollToVerticalOffset(View.VerticalOffset-e.Delta*2); e.Handled=true;
    }
}
