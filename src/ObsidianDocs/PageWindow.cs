using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace ObsidianDocs;
public partial class MainWindow
{
    const int PageWindowRadius=15;
    int windowStart,windowEnd=-1,windowRevision;
    bool shiftingWindow,draggingDocumentThumb;
    Task pagePrefetchTask=Task.CompletedTask;
    int prefetchGeneration=-1;
    IEnumerable<PageSurface> WindowSurfaces=>surfaces.Skip(windowStart).Take(Math.Max(0,windowEnd-windowStart+1));
    bool InPageWindow(int page)=>page>=windowStart && page<=windowEnd;
    void ConfigurePageWindow()
    {
        SideList.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((_,_)=>draggingThumbnailThumb=true),true);
        SideList.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler(async (_,_)=>
        {
            draggingThumbnailThumb=false;
            try { await RecenterThumbnailsAsync(FindScroller(SideList)); } catch(Exception error) { Status.Text=error.Message; }
        }),true);
        Viewer.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((_,_)=>draggingDocumentThumb=true),true);
        Viewer.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,_)=>
        {
            draggingDocumentThumb=false;
            if(current is not null && !current.IsWordDocument) SetPageWindow(current.Page,true);
        }),true);
    }
    void StartPagePrefetch()
    {
        if(current is null || current.IsWordDocument || (prefetchGeneration==viewGeneration && !pagePrefetchTask.IsCompleted) || !WindowSurfaces.Any(s=>s.Image.Source is null)) return;
        prefetchGeneration=viewGeneration;
        pagePrefetchTask=PrefetchWindowAsync(current,viewGeneration);
    }
    async Task PrefetchWindowAsync(DocumentSession session,int generation)
    {
        try
        {
            foreach(var surface in WindowSurfaces.OrderBy(s=>Math.Abs(s.Index-session.Page)).ToArray())
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                if(current!=session || generation!=viewGeneration) return;
                if(surface.Image.Source is null) await EnsurePageAsync(surface.Index,generation,48);
            }
        }
        catch(Exception error) { if(current==session && generation==viewGeneration) Status.Text="Не удалось предзагрузить страницу: "+error.Message; }
    }
    void SetPageWindow(int center,bool preserveViewport)
    {
        if(current is null || surfaces.Count==0) return;
        int first=Math.Max(0,center-PageWindowRadius),last=Math.Min(surfaces.Count-1,center+PageWindowRadius);
        if(first==windowStart && last==windowEnd && PageStack.Children.Count>0) return;
        var anchor=preserveViewport ? WindowSurfaces.LastOrDefault(p=>p.Top<=Viewer.VerticalOffset) ?? WindowSurfaces.FirstOrDefault() : null;
        double relative=anchor is null ? 0 : Viewer.VerticalOffset-anchor.Top;
        double horizontal=Viewer.HorizontalOffset;
        shiftingWindow=true; int revision=++windowRevision; ++viewGeneration;
        windowStart=first; windowEnd=last;
        if(!InPageWindow(activeSurface.Index)) { HideCard(); activeSurface=surfaces[center]; bitmap=activeSurface.Image.Source as System.Windows.Media.Imaging.BitmapSource; }
        PageStack.Children.Clear();
        foreach(var surface in surfaces)
        {
            if(InPageWindow(surface.Index)) PageStack.Children.Add(surface);
            else
            {
                surface.Image.Source=null; surface.RenderDpi=0; surface.Words=null; surface.Top=double.NaN;
                surface.Markers.Children.Clear(); surface.TextHighlights.Children.Clear(); surface.SearchHighlights.Children.Clear();
            }
        }
        ResizePage(); Viewer.UpdateLayout(); ResizePage(); Viewer.UpdateLayout();
        Viewer.ScrollToVerticalOffset(anchor is not null && InPageWindow(anchor.Index) ? anchor.Top+relative : surfaces[center].Top);
        Viewer.ScrollToHorizontalOffset(horizontal); Viewer.UpdateLayout();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>
        {
            if(revision!=windowRevision) return;
            shiftingWindow=false;
            if(!buildingView) _=RenderVisibleAsync();
        }));
    }
    async void PageJump_KeyDown(object sender,System.Windows.Input.KeyEventArgs e)
    {
        if(e.Key!=System.Windows.Input.Key.Enter || busy || current is null || current.IsWordDocument) return;
        e.Handled=true;
        if(!int.TryParse(PageJump.Text,out var page) || page<1 || page>current.Count) { Status.Text=$"Укажите страницу от 1 до {current.Count}"; return; }
        try { await NavigateAsync(page-1); Viewer.Focus(); } catch(Exception error) { Error(error); }
    }
}
