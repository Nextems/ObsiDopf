using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ObsidianDocs;
public partial class MainWindow
{
    int thumbStart,thumbEnd=-1,thumbRevision;
    bool shiftingThumbnails,draggingThumbnailThumb;
    DocumentSession? thumbnailSession;
    byte[]? thumbnailBytes;
    readonly Dictionary<int,BitmapSource> thumbnailCache=[];
    static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is ScrollViewer viewer) return viewer;
            if(FindScroller(child) is { } nested) return nested;
        }
        return null;
    }
    async Task RefreshSideAsync(bool deferLoading=false,int? center=null,bool preserveViewport=false)
    {
        if(current is null || current.IsWordDocument || surfaces.Count==0) return;
        var session=current; int page=center ?? session.Page;
        var anchor=preserveViewport ? SideList.Items.Cast<ListBoxItem>().LastOrDefault(item=>item.TranslatePoint(new Point(),SideList).Y<=0) ?? SideList.Items.Cast<ListBoxItem>().FirstOrDefault() : null;
        int anchorPage=anchor?.Tag is int index ? index : page;
        double anchorY=anchor?.TranslatePoint(new Point(),SideList).Y ?? 0;
        shiftingThumbnails=true; int revision=++thumbRevision; int generation=++thumbnailGeneration;
        if(thumbnailSession!=session || !ReferenceEquals(thumbnailBytes,session.RenderBytes))
        {
            thumbnailCache.Clear(); thumbnailSession=session; thumbnailBytes=session.RenderBytes;
        }
        thumbStart=Math.Max(0,page-PageWindowRadius); thumbEnd=Math.Min(session.Count-1,page+PageWindowRadius);
        refreshing=true;
        try
        {
            SideList.Items.Clear();
            for(int i=thumbStart;i<=thumbEnd;i++)
            {
                var stack=new StackPanel();
                stack.Children.Add(new TextBlock { Text=$"Страница {i+1}",Margin=new Thickness(0,0,0,8) });
                var image=new Image { Width=160,Height=160*surfaces[i].Ratio,Stretch=Stretch.Uniform,Tag=i };
                if(thumbnailCache.TryGetValue(i,out var cached)) image.Source=cached;
                stack.Children.Add(new Border { Background=Brushes.White,Child=image,HorizontalAlignment=HorizontalAlignment.Center });
                SideList.Items.Add(new ListBoxItem { Content=stack,Tag=i });
            }
            SideHint.Text=$"Всего: {session.Count} · диапазон {thumbStart+1}–{thumbEnd+1}";
            SideList.SelectedIndex=session.Page>=thumbStart && session.Page<=thumbEnd ? session.Page-thumbStart : -1;
            SideList.UpdateLayout();
            if(preserveViewport && anchorPage>=thumbStart && anchorPage<=thumbEnd && FindScroller(SideList) is { } scroller)
            {
                var next=(ListBoxItem)SideList.Items[anchorPage-thumbStart];
                scroller.ScrollToVerticalOffset(scroller.VerticalOffset+next.TranslatePoint(new Point(),SideList).Y-anchorY);
                scroller.UpdateLayout();
            }
            else if(SideList.SelectedItem is { } selected) SideList.ScrollIntoView(selected);
            foreach(int old in thumbnailCache.Keys.Where(i=>(i<thumbStart || i>thumbEnd) && !InPageWindow(i)).ToArray()) thumbnailCache.Remove(old);
        }
        finally
        {
            refreshing=false;
            _=Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=> { if(revision==thumbRevision) shiftingThumbnails=false; }));
        }
        if(!deferLoading) thumbnailTask=LoadThumbnailsAsync(session,generation);
        await Task.CompletedTask;
    }
    async Task LoadThumbnailsAsync(DocumentSession session,int generation)
    {
        int first=thumbStart,last=thumbEnd;
        foreach(int index in Enumerable.Range(first,Math.Max(0,last-first+1)).OrderBy(i=>Math.Abs(i-(first+last)/2)))
        {
            if(generation!=thumbnailGeneration || current!=session) return;
            if(thumbnailCache.ContainsKey(index)) continue;
            try
            {
                var image=await Task.Run(()=>session.Render(index,40,includeNoteIcons:false));
                if(generation!=thumbnailGeneration || current!=session) return;
                thumbnailCache[index]=image;
                var stack=(StackPanel)((ListBoxItem)SideList.Items[index-first]).Content;
                ((Image)((Border)stack.Children[1]).Child).Source=image;
            }
            catch(Exception error) { Status.Text=$"Не удалось отобразить миниатюру {index+1}: {error.Message}"; }
        }
    }
    async void Side_ScrollChanged(object sender,ScrollChangedEventArgs e)
    {
        if(Math.Abs(e.VerticalChange)<.1) return;
        if(e.OriginalSource is not ScrollViewer scroller) return;
        try { await RecenterThumbnailsAsync(scroller); }
        catch(Exception error) { Status.Text="Ошибка прокрутки миниатюр: "+error.Message; }
    }
    async Task RecenterThumbnailsAsync(ScrollViewer? scroller)
    {
        if(scroller is null || draggingThumbnailThumb || refreshing || shiftingThumbnails || busy || current is null || current.IsWordDocument || SideList.Items.Count==0) return;
        double line=Math.Min(200,scroller.ViewportHeight*.35);
        var item=SideList.Items.Cast<ListBoxItem>().LastOrDefault(i=>i.TranslatePoint(new Point(),SideList).Y<=line) ?? (ListBoxItem)SideList.Items[0];
        if(item.Tag is not int page || (Math.Max(0,page-PageWindowRadius)==thumbStart && Math.Min(current.Count-1,page+PageWindowRadius)==thumbEnd)) return;
        await RefreshSideAsync(center:page,preserveViewport:true);
    }
}
