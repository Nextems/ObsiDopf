using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ObsidianDocs;

// The real RichTextBox remains visible and handles input itself. This layer
// draws only non-interactive page guides and search highlights; it never hides,
// slices, moves or repaints the document's text.
internal sealed class PagedWordView : Grid
{
    readonly DocxEditor editor;
    readonly double paperWidth,paperHeight;
    readonly Thickness margins;
    readonly Border sourceHost;
    readonly GuideCanvas guides;
    readonly Func<IEnumerable<TextPointer>> hardBreaks;
    readonly List<(double Start,double End)> slices=[];
    readonly List<(TextPointer Start,TextPointer End)> findRanges=[];
    readonly List<(Rect Rect,bool Active)> findRects=[];
    readonly WordPageGaps pageGaps;
    readonly List<double> gapPositions=[];
    readonly DispatcherTimer paginationTimer;
    readonly DispatcherTimer miniatureTimer;
    readonly Dictionary<int,BitmapSource> miniatures=[];
    readonly HashSet<int> staleMiniatures=[];
    readonly Queue<int> miniatureQueue=[];
    readonly HashSet<int> pendingMiniatures=[];
    readonly HashSet<int> dirtyPages=[];
    readonly List<TextPointer> pageStarts=[];
    bool viewportLocked;
    double lockedVertical,lockedHorizontal;
    int viewportRevision;
    const int RenderRadius=10;
    int firstRendered,lastRendered=int.MaxValue;
    ScrollViewer? scroller;
    bool layoutDirty=true,updating;
    double sourceHeight=-1;
    int activeFind=-1;
    public int PageCount=>Math.Max(1,slices.Count);
    public double PaperWidth=>paperWidth;
    public double PaperHeight=>paperHeight;
    public event Action? PagesChanged;
    public event Action? MiniaturesChanged;

    public PagedWordView(DocxEditor editor,double height,Thickness pageMargins,Func<IEnumerable<TextPointer>> breaks)
    {
        this.editor=editor; paperWidth=editor.Width; paperHeight=height; margins=pageMargins; hardBreaks=breaks;
        Width=paperWidth; Margin=new Thickness(24); VerticalAlignment=VerticalAlignment.Top;
        Background=Brushes.White;
        editor.Margin=new Thickness(0); editor.VerticalAlignment=VerticalAlignment.Top;
        editor.CaretBrush=Brushes.DarkViolet;
        sourceHost=new Border { Background=Brushes.White,Child=editor,Width=paperWidth,VerticalAlignment=VerticalAlignment.Top };
        Children.Add(sourceHost);
        pageGaps=new WordPageGaps(editor);
        // Initial presentation styles are prepared before the user has an undo
        // history. Later gap changes switch style values, not local formatting.
        editor.IsUndoEnabled=false;
        pageGaps.GetTargets();
        editor.IsUndoEnabled=true;
        paginationTimer=new DispatcherTimer(DispatcherPriority.Background,Dispatcher)
        { Interval=TimeSpan.FromMilliseconds(800) };
        paginationTimer.Tick+=(_,_)=> { paginationTimer.Stop(); UpdatePages(); };
        miniatureTimer=new DispatcherTimer(DispatcherPriority.Background,Dispatcher)
        { Interval=TimeSpan.FromMilliseconds(40) };
        miniatureTimer.Tick+=(_,_)=>RenderNextMiniature();
        AddHandler(RequestBringIntoViewEvent,new RequestBringIntoViewEventHandler((_,e)=>
        {
            if(viewportLocked) e.Handled=true;
        }),true);
        guides=new GuideCanvas(this) { IsHitTestVisible=false,Focusable=false }; Children.Add(guides);
        editor.TextChanged+=(_,e)=>
        {
            if(editor.AdjustingPageGaps) return;
            MarkChangedPages(e);
            layoutDirty=true; findRanges.Clear(); findRects.Clear(); activeFind=-1;
            miniatureTimer.Stop();
            paginationTimer.Stop(); if(IsLoaded) paginationTimer.Start();
            guides.InvalidateVisual();
        };
        // LayoutUpdated also fires for scrolling, undo caret movement and the
        // page styles below. Scheduling from it creates a self-sustaining
        // reformat loop. Only real edits and initial loading request pagination.
        editor.SizeChanged+=(_,e)=>
        {
            // Native background formatting may finish after the edit event.
            // Observe real extent changes, never unrelated layout passes.
            if(!IsLoaded || updating || !e.HeightChanged || Math.Abs(sourceHeight-editor.ActualHeight)<.1) return;
            layoutDirty=true; paginationTimer.Stop(); paginationTimer.Start();
        };
        Loaded+=(_,_)=>
        {
            scroller=Parent as ScrollViewer;
            if(scroller is not null) scroller.ScrollChanged+=ViewportChanged;
            layoutDirty=true; UpdatePages(); if(layoutDirty) paginationTimer.Start();
        };
        Unloaded+=(_,_)=> { paginationTimer.Stop(); miniatureTimer.Stop(); if(scroller is not null) scroller.ScrollChanged-=ViewportChanged; scroller=null; };
    }
    void ViewportChanged(object sender,ScrollChangedEventArgs e)
    {
        if(viewportLocked)
        {
            RestoreViewport();
            return;
        }
        UpdateRenderWindow(); guides.InvalidateVisual();
    }
    public void KeepViewport(Action action)
    {
        if(scroller is null) { action(); return; }
        if(!viewportLocked)
        {
            lockedVertical=scroller.VerticalOffset; lockedHorizontal=scroller.HorizontalOffset;
            viewportLocked=true;
        }
        int revision=++viewportRevision;
        try { action(); }
        finally
        {
            RestoreViewport();
            // WPF requests caret visibility after its undo command returns.
            // Suppress that request until the queued caret/layout work finishes.
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>
            {
                if(revision!=viewportRevision) return;
                RestoreViewport();
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>
                {
                    if(revision!=viewportRevision) return;
                    viewportLocked=false; UpdateRenderWindow(); guides.InvalidateVisual();
                }));
            }));
        }
    }
    void RestoreViewport()
    {
        if(scroller is null) return;
        double vertical=Math.Min(lockedVertical,scroller.ScrollableHeight);
        double horizontal=Math.Min(lockedHorizontal,scroller.ScrollableWidth);
        if(Math.Abs(scroller.VerticalOffset-vertical)>.1) scroller.ScrollToVerticalOffset(vertical);
        if(Math.Abs(scroller.HorizontalOffset-horizontal)>.1) scroller.ScrollToHorizontalOffset(horizontal);
    }
    int PageAt(TextPointer pointer)
    {
        int lo=0,hi=pageStarts.Count;
        while(lo<hi)
        {
            int mid=(lo+hi)/2;
            if(pageStarts[mid].CompareTo(pointer)<0) lo=mid+1; else hi=mid;
        }
        // Include the preceding page for edits exactly at a page boundary.
        return Math.Max(0,lo-1);
    }
    void MarkChangedPages(TextChangedEventArgs args)
    {
        int length=editor.Document.ContentStart.GetOffsetToPosition(editor.Document.ContentEnd);
        foreach(var change in args.Changes)
        {
            var start=editor.Document.ContentStart.GetPositionAtOffset(Math.Clamp(change.Offset,0,length),LogicalDirection.Backward);
            var end=editor.Document.ContentStart.GetPositionAtOffset((int)Math.Clamp((long)change.Offset+change.AddedLength,0,length),LogicalDirection.Forward);
            if(start is null || end is null) continue;
            int first=PageAt(start),last=PageAt(end);
            if(last+1<pageStarts.Count && pageStarts[last+1].CompareTo(end)<=0) last++;
            for(int page=first;page<=last;page++) dirtyPages.Add(page);
        }
        if(args.Changes.Count==0)
            for(int page=PageAt(editor.Selection.Start);page<=PageAt(editor.Selection.End);page++) dirtyPages.Add(page);
    }
    int PageAt(double y)
    {
        int lo=0,hi=slices.Count;
        while(lo<hi) { int mid=(lo+hi)/2; if(slices[mid].End<y) lo=mid+1; else hi=mid; }
        return Math.Clamp(lo,0,Math.Max(0,slices.Count-1));
    }
    void UpdateRenderWindow()
    {
        if(scroller is null || slices.Count==0) return;
        double zoom=LayoutTransform is ScaleTransform transform ? transform.ScaleY : 1;
        // Translate the viewport into document coordinates (the paper has an
        // outer margin). Keep the full extent so any page remains reachable.
        double top=Math.Max(0,scroller.VerticalOffset/zoom-Margin.Top);
        firstRendered=Math.Max(0,PageAt(top)-RenderRadius);
        lastRendered=Math.Min(slices.Count-1,PageAt(top+scroller.ViewportHeight/zoom)+RenderRadius);
        foreach(int page in miniatures.Keys.Where(p=>p<firstRendered || p>lastRendered).ToList())
        { miniatures.Remove(page); staleMiniatures.Remove(page); }
        double start=slices[firstRendered].Start,end=slices[lastRendered].End;
        var bounds=new Rect(0,start,paperWidth,Math.Max(1,end-start));
        if(sourceHost.Clip is RectangleGeometry existing && existing.Rect==bounds) return;
        var clip=new RectangleGeometry(bounds); clip.Freeze(); sourceHost.Clip=clip;
    }
    static Rect CharacterRect(TextPointer pointer,LogicalDirection direction)
    {
        try { return pointer.GetCharacterRect(direction); }
        catch(InvalidOperationException) { return Rect.Empty; }
        catch(ArgumentException) { return Rect.Empty; }
    }
    public void SetZoom(double value) { LayoutTransform=new ScaleTransform(value,value); UpdateRenderWindow(); guides.InvalidateVisual(); }
    public void SetFindRanges(IEnumerable<(TextPointer Start,TextPointer End)> ranges,int active)
    {
        findRanges.Clear(); findRanges.AddRange(ranges); activeFind=active; RebuildFindRects(); guides.InvalidateVisual();
    }
    double GuideBetweenLines(double target,double start,double end)
    {
        // Move the guide into the gap below a text line instead of crossing it.
        double adjusted=target;
        for(int pass=0;pass<4;pass++)
        {
            double next=adjusted;
            for(int column=0;column<7;column++)
            {
                double x=margins.Left+(paperWidth-margins.Left-margins.Right)*column/6;
                var pointer=editor.GetPositionFromPoint(new Point(x,adjusted),true);
                if(pointer is null) continue;
                var rect=CharacterRect(pointer,LogicalDirection.Forward);
                if(!rect.IsEmpty && rect.Height<80 && rect.Top<adjusted && rect.Bottom>=adjusted) next=Math.Max(next,rect.Bottom+1);
            }
            if(next<=adjusted+.1 || next>=end) break;
            adjusted=next;
        }
        return Math.Clamp(adjusted,start+1,end);
    }
    void UpdatePages()
    {
        if(updating || !IsLoaded || !editor.IsArrangeValid || editor.ActualHeight<=0) return;
        if(!layoutDirty && Math.Abs(sourceHeight-editor.ActualHeight)<.1) return;
        updating=true;
        try
        {
            editor.AdjustingPageGaps=true;
            var targets=pageGaps.GetTargets();
            // Measure the unspaced coordinates without first removing all page
            // styles. Undo often leaves the same boundaries in place; resetting
            // and reapplying them forced two full reflows on every undo.
            var paddingBefore=new double[targets.Count];
            double reserved=0;
            for(int i=0;i<targets.Count;i++)
            {
                if(pageGaps.IsSpaced(targets[i])) reserved+=WordPageGaps.Size;
                paddingBefore[i]=reserved;
            }
            double BasePosition(int i)=>WordPageGaps.Position(targets[i])-paddingBefore[i];
            double baseHeight=editor.ActualHeight-reserved;
            var chosen=new List<WordPageGaps.Target>();
            double previous=0;
            int firstCandidate=0;
            while(previous+paperHeight<baseHeight-1)
            {
                double desired=previous+paperHeight;
                // Targets are already in document order. Probe around each
                // page boundary instead of measuring every paragraph/cell.
                int lo=firstCandidate,hi=targets.Count;
                while(lo<hi)
                {
                    int mid=(lo+hi)/2;
                    if(BasePosition(mid)<desired) lo=mid+1; else hi=mid;
                }
                int best=-1; double bestY=0,distance=double.PositiveInfinity;
                for(int i=Math.Max(firstCandidate,lo-1);i<=Math.Min(targets.Count-1,lo);i++)
                {
                    double y=BasePosition(i);
                    if(!double.IsFinite(y) || y<=previous+paperHeight*.25 || y>=baseHeight-1 || Math.Abs(y-desired)>=distance) continue;
                    best=i; bestY=y; distance=Math.Abs(y-desired);
                }
                if(best<0) break;
                chosen.Add(targets[best]); previous=bestY; firstCandidate=best+1;
            }
            KeepViewport(()=> { if(pageGaps.Apply(chosen)) editor.UpdateLayout(); });
            gapPositions.Clear();
            gapPositions.AddRange(chosen.Select(t=>WordPageGaps.Position(t)-WordPageGaps.Size).Where(double.IsFinite).OrderBy(y=>y));
            sourceHeight=editor.ActualHeight; layoutDirty=false;
            var next=new List<(double Start,double End)>();
            double begin=0;
            foreach(double gap in gapPositions) { next.Add((begin,gap)); begin=gap+WordPageGaps.Size; }
            next.Add((begin,Math.Max(begin+1,sourceHeight)));
            bool changed=!slices.SequenceEqual(next);
            var starts=new List<TextPointer> { editor.Document.ContentStart };
            starts.AddRange(chosen.Select(t=>t.Anchors.Aggregate((a,b)=>a.CompareTo(b)<=0 ? a : b)));
            foreach(int page in miniatures.Keys.ToList())
            {
                // A moved page can keep its pixels when both text boundaries
                // and height are unchanged. Only edits/reflow invalidate it.
                bool same=page<next.Count && page<pageStarts.Count && page<starts.Count &&
                    pageStarts[page].CompareTo(starts[page])==0 &&
                    (page+1==pageStarts.Count && page+1==starts.Count ||
                     page+1<pageStarts.Count && page+1<starts.Count && pageStarts[page+1].CompareTo(starts[page+1])==0) &&
                    Math.Abs((slices[page].End-slices[page].Start)-(next[page].End-next[page].Start))<.1;
                if(page>=next.Count) { miniatures.Remove(page); staleMiniatures.Remove(page); }
                else if(dirtyPages.Contains(page) || !same) staleMiniatures.Add(page);
            }
            slices.Clear(); slices.AddRange(next);
            pageStarts.Clear(); pageStarts.AddRange(starts); dirtyPages.Clear();
            if(miniatureQueue.Count>0) miniatureTimer.Start();
            UpdateRenderWindow();
            RebuildFindRects(); guides.InvalidateVisual();
            MiniaturesChanged?.Invoke();
            if(changed) PagesChanged?.Invoke();
        }
        finally { editor.AdjustingPageGaps=false; updating=false; }
    }
    void RebuildFindRects()
    {
        findRects.Clear();
        for(int index=0;index<findRanges.Count;index++)
        {
            var range=findRanges[index]; Rect line=Rect.Empty;
            for(var pointer=range.Start;pointer is not null && pointer.CompareTo(range.End)<0;)
            {
                var next=pointer.GetNextInsertionPosition(LogicalDirection.Forward);
                if(next is null || next.CompareTo(range.End)>0) next=range.End;
                var a=CharacterRect(pointer,LogicalDirection.Forward); var b=CharacterRect(next,LogicalDirection.Backward);
                if(!a.IsEmpty && !b.IsEmpty)
                {
                    if(!line.IsEmpty && Math.Abs(line.Top-a.Top)>Math.Max(line.Height,a.Height)*.4) { findRects.Add((line,index==activeFind)); line=Rect.Empty; }
                    a.Union(b); if(line.IsEmpty) line=a; else line.Union(a);
                }
                if(next.CompareTo(pointer)<=0) break;
                pointer=next;
            }
            if(!line.IsEmpty) findRects.Add((line,index==activeFind));
        }
    }
    public void DrawMiniature(DrawingContext context,Rect target,int page)
    {
        if(page<0 || page>=slices.Count || target.IsEmpty) return;
        context.DrawRectangle(Brushes.White,new Pen(Brushes.DimGray,.5),target);
        if(page<firstRendered || page>lastRendered) return;
        if(miniatures.TryGetValue(page,out var bitmap))
        {
            // Keep the last image visible until its replacement is ready.
            context.DrawImage(bitmap,target);
            if(!staleMiniatures.Contains(page)) return;
        }
        if(layoutDirty || !IsLoaded || !pendingMiniatures.Add(page)) return;
        miniatureQueue.Enqueue(page); miniatureTimer.Start();
    }
    void RenderNextMiniature()
    {
        if(layoutDirty || updating || !IsLoaded) { miniatureTimer.Stop(); return; }
        if(!miniatureQueue.TryDequeue(out int page)) { miniatureTimer.Stop(); return; }
        pendingMiniatures.Remove(page);
        if(page<firstRendered || page>lastRendered || page>=slices.Count) return;
        var slice=slices[page];
        const int width=160;
        int height=Math.Max(1,(int)Math.Ceiling(width*paperHeight/paperWidth));
        var visual=new DrawingVisual();
        var image=new VisualBrush(sourceHost)
        {
            ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,slice.Start,paperWidth,slice.End-slice.Start),
            Stretch=Stretch.Fill,AutoLayoutContent=false,AlignmentX=AlignmentX.Left,AlignmentY=AlignmentY.Top
        };
        using(var drawing=visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));
            drawing.DrawRectangle(image,null,new Rect(0,0,width,height*Math.Min(1,(slice.End-slice.Start)/paperHeight)));
        }
        // Store pixels, not a live VisualBrush. Otherwise every scroll/undo
        // redraws the editor again for each visible miniature.
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); miniatures[page]=bitmap; staleMiniatures.Remove(page);
        MiniaturesChanged?.Invoke();
    }
    void DrawGuides(DrawingContext drawing)
    {
        double zoom=LayoutTransform is ScaleTransform transform ? transform.ScaleY : 1;
        double top=(scroller?.VerticalOffset ?? 0)/zoom-paperHeight;
        double bottom=top+(scroller?.ViewportHeight ?? paperHeight*2)/zoom+2*paperHeight;
        var gapBrush=new SolidColorBrush(Color.FromRgb(30,30,36));
        foreach(double y in gapPositions)
        {
            if(y+WordPageGaps.Size<top || y>bottom) continue;
            drawing.DrawRectangle(gapBrush,null,new Rect(0,y,paperWidth,WordPageGaps.Size));
        }
        foreach(var found in findRects)
        {
            if(found.Rect.Bottom<top || found.Rect.Top>bottom) continue;
            drawing.DrawRectangle(found.Active ? new SolidColorBrush(Color.FromArgb(150,255,150,30)) : new SolidColorBrush(Color.FromArgb(110,255,225,40)),found.Active ? new Pen(Brushes.DarkOrange,1) : null,found.Rect);
        }
    }
    sealed class GuideCanvas(PagedWordView owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext context)=>owner.DrawGuides(context);
    }
}
