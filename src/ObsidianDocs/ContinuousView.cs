using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using PdfSharp.Pdf.Annotations;

namespace ObsidianDocs;
public partial class MainWindow
{
    readonly AuthorProfiles authors = new();
    readonly List<PageSurface> surfaces = [];
    PageSurface activeSurface = new(0,1.414);
    Image PageImage => activeSurface.Image;
    Canvas SelectionCanvas => activeSurface.Selection;
    Rectangle SelectionBox => activeSurface.Box;
    FrameworkElement PageHost => PageStack;
    int viewGeneration;
    int thumbnailGeneration;
    bool buildingView, renderingVisible, renderAgain;
    Task thumbnailTask = Task.CompletedTask;
    Task visibleRenderTask = Task.CompletedTask;

    async Task RenderAsync()
    {
        if (current is null) return;
        ConfigureDocumentUi();
        ChangeSearchDocument();
        if (current.IsWordDocument) { ++viewGeneration; ++thumbnailGeneration; surfaces.Clear(); PageStack.Children.Clear(); HideCard(); return; }
        HideCard(); var session=current; int requested=session.Page;
        buildingView=true; ++viewGeneration;
        try
        {
            PageStack.Children.Clear(); surfaces.Clear(); windowEnd=-1;
            for (int index=0;index<session.Count;index++)
            {
                double ratio;
                if (session.Photo is { } photo) ratio=(double)photo.PixelHeight/photo.PixelWidth;
                else
                {
                    var page=session.Pdf!.Pages[index];
                    var box=PdfTextComments.VisibleBox(page);
                    ratio=box.Height/box.Width;
                    if (page.Rotate%180 != 0) ratio=1/ratio;
                }
                var surface=new PageSurface(index,ratio);
                surface.Selection.MouseLeftButtonDown+=Select_Start;
                surface.Selection.MouseMove+=Select_Move;
                surface.Selection.MouseLeftButtonUp+=Select_End;
                surface.Selection.ContextMenu=CreateTextSelectionMenu(surface);
                surfaces.Add(surface);
            }
            activeSurface=surfaces[requested]; bitmap=null;
            PageStack.Visibility=Visibility.Visible; Welcome.Visibility=Visibility.Collapsed;
            SetPageWindow(requested,false);
            ResizePage(); Viewer.UpdateLayout();
            // A vertical scrollbar can reduce the viewport after first layout.
            ResizePage(); Viewer.UpdateLayout();
            Viewer.ScrollToVerticalOffset(surfaces[requested].Top);
            UpdateCurrentPageUi();
            await RefreshSideAsync(deferLoading:true); RefreshComments();
        }
        finally { buildingView=false; }
        await EnsurePageAsync(requested,viewGeneration);
        await RenderVisibleAsync();
        thumbnailTask=LoadThumbnailsAsync(session,thumbnailGeneration);
    }
    void ResizePage()
    {
        if(current?.IsWordDocument==true) { current.Editor?.SetZoom(zoom); ZoomStatus.Text=$"{zoom:P0}"; return; }
        if (surfaces.Count==0) return;
        double available=Viewer.ViewportWidth>80 ? Viewer.ViewportWidth : Math.Max(300,Workspace.ActualWidth-CommentColumn.ActualWidth-18);
        double width=Math.Max(180,Math.Min(800,available-48))*zoom;
        if(commentDrag is { } drag && Math.Abs(drag.Size.Width-width)>.01) CancelCommentDrag();
        double top=24;
        foreach (var surface in WindowSurfaces) { surface.Size(width); surface.Top=top; top+=surface.Height+24; BuildMarkers(surface); }
        PaintSearchHighlights();
        PaintTextSelection();
        ZoomStatus.Text=$"{zoom:P0}";
    }
    async Task EnsurePageAsync(int index,int generation,int dpi=144)
    {
        if (current is null || index<0 || index>=surfaces.Count || !InPageWindow(index)) return;
        var session=current; var surface=surfaces[index];
        if (surface.Image.Source is null || surface.RenderDpi<dpi)
        {
            var image=await Task.Run(()=>session.Render(index,dpi,includeNoteIcons:false));
            if (generation!=viewGeneration || current!=session) return;
            if(surface.RenderDpi<=dpi) { surface.Image.Source=image; surface.RenderDpi=dpi; }
        }
        if (surface==activeSurface) bitmap=(BitmapSource?)surface.Image.Source;
        BuildMarkers(surface);
    }
    Task RenderVisibleAsync()
    {
        renderAgain=true;
        if (renderingVisible || buildingView || current is null) return visibleRenderTask;
        visibleRenderTask=RenderVisibleCoreAsync();
        return visibleRenderTask;
    }
    async Task RenderVisibleCoreAsync()
    {
        renderingVisible=true;
        try
        {
            while (renderAgain && current is not null)
            {
                renderAgain=false; var generation=viewGeneration;
                double height=Math.Max(400,Viewer.ViewportHeight), offset=Viewer.VerticalOffset;
                var near=WindowSurfaces.Where(p=>p.Top+p.Height>=offset-height && p.Top<=offset+2*height).ToArray();
                foreach (var surface in near)
                {
                    await EnsurePageAsync(surface.Index,generation);
                    if (generation!=viewGeneration) { renderAgain=true; break; }
                }
                if (generation!=viewGeneration) continue;
            }
            StartPagePrefetch();
        }
        catch (Exception error) { Status.Text="Ошибка отображения: "+error.Message; }
        finally { renderingVisible=false; }
    }
    async void Side_Changed(object sender,SelectionChangedEventArgs e)
    {
        if (refreshing || busy || current is null || SideList.SelectedItem is not ListBoxItem { Tag: int page }) return;
        try { await NavigateAsync(page); } catch (Exception error) { Error(error); }
    }
    async Task NavigateAsync(int page,bool enterAtBottom=false)
    {
        if (busy || current is null || page<0 || page>=surfaces.Count) return;
        HideCard(); current.Page=page; activeSurface=surfaces[page];
        SetPageWindow(page,false);
        Viewer.UpdateLayout();
        Viewer.ScrollToVerticalOffset(surfaces[page].Top + (enterAtBottom ? Math.Max(0,surfaces[page].Height-Viewer.ViewportHeight) : 0));
        UpdateCurrentPageUi(); await EnsurePageAsync(page,viewGeneration);
    }
    void UpdateCurrentPageUi()
    {
        if (current is null) return;
        PageStatus.Text=$"{current.Page+1} / {current.Count}";
        if(!PageJump.IsKeyboardFocusWithin) PageJump.Text=(current.Page+1).ToString();
        if(!buildingView && (thumbStart!=Math.Max(0,current.Page-PageWindowRadius) || thumbEnd!=Math.Min(current.Count-1,current.Page+PageWindowRadius))) _=RefreshSideAsync();
        refreshing=true; SideList.SelectedIndex=current.Page>=thumbStart && current.Page<=thumbEnd ? current.Page-thumbStart : -1;
        if (SideList.SelectedItem is { } item) SideList.ScrollIntoView(item);
        refreshing=false;
    }
    async void Viewer_ScrollChanged(object sender,ScrollChangedEventArgs e)
    {
        if (buildingView || shiftingWindow || current is null || surfaces.Count==0) return;
        if (Math.Abs(e.ViewportWidthChange)>1) ResizePage();
        double line=Viewer.VerticalOffset+Math.Min(200,Viewer.ViewportHeight*.35);
        int page=(WindowSurfaces.LastOrDefault(p=>p.Top<=line) ?? WindowSurfaces.First()).Index;
        if (page!=current.Page) { current.Page=page; if(!draggingDocumentThumb) SetPageWindow(page,true); UpdateCurrentPageUi(); }
        if (OcrCard.Visibility==Visibility.Visible) PositionCard(selection);
        await RenderVisibleAsync();
    }
    async void Workspace_SizeChanged(object sender,SizeChangedEventArgs e)
    {
        if (PageStack is null || Viewer is null) return;
        ResizePage(); if (OcrCard is not null) PositionCard(selection);
        await RenderVisibleAsync();
    }
    void Pages_Click(object sender,RoutedEventArgs e)
    {
        SetCommentPanel(false);
    }
    void Comments_Click(object sender,RoutedEventArgs e)
    {
        if (current?.IsWordDocument==true) return;
        SetCommentPanel(true); RefreshComments();
    }

    void RefreshComments()
    {
        CommentList.Children.Clear();
        if (current?.Pdf is null || !current.CanEditPdf) { CommentHint.Text="Комментарии доступны для PDF"; return; }
        int count=0;
        for (int page=0;page<current.Count;page++)
        {
            var annotations=current.Pdf.Pages[page].Annotations;
            for (int index=0;index<annotations.Count;index++)
            {
                var note=annotations[index]; if (string.IsNullOrWhiteSpace(note.Contents)) continue;
                int pageIndex=page,noteIndex=index;
                var content=new StackPanel();
                content.Children.Add(new TextBlock { Text=string.IsNullOrWhiteSpace(note.Title)?"Без имени":note.Title,Foreground=Brush("#A9CFFB"),FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap });
                content.Children.Add(new TextBlock { Text=$"Страница {page+1}",Foreground=Brush("#8E9DB5"),FontSize=11,Margin=new Thickness(0,4,0,8) });
                var quote=note.Elements.GetString("/ODQuote");
                if (!string.IsNullOrWhiteSpace(quote)) content.Children.Add(new TextBlock { Text="«"+quote+"»",Foreground=Brush("#A5BADA"),TextWrapping=TextWrapping.Wrap,MaxHeight=75,Margin=new Thickness(0,0,0,8) });
                content.Children.Add(new TextBlock { Text=note.Contents,Foreground=Brush("#E1E7F2"),TextWrapping=TextWrapping.Wrap });
                var button=new Button { Content=content,HorizontalContentAlignment=HorizontalAlignment.Stretch,Background=Brush("#26354B"),Padding=new Thickness(11),Margin=new Thickness(0,0,0,10) };
                button.Click+=async (_,_)=> { if (busy) return; try { await OpenCommentAsync(pageIndex,noteIndex); } catch (Exception error) { Error(error); } };
                var card=new StackPanel(); card.Children.Add(button);
                var delete=new Button { Content="Удалить",Background=Brush("#583440"),HorizontalAlignment=HorizontalAlignment.Right,Padding=new Thickness(10,5,10,5),Margin=new Thickness(0,0,0,10) };
                delete.Click+=async (_,_)=> { if (busy) return; try { await DeleteCommentAsync(pageIndex,noteIndex); } catch (Exception error) { Error(error); } };
                card.Children.Add(delete); CommentList.Children.Add(card); count++;
            }
        }
        CommentHint.Text=count==0 ? "Нет комментариев. Выберите инструмент «Комментарий» и щёлкните на странице PDF." : $"Комментариев: {count} · нажмите для изменения";
    }
    async Task OpenCommentAsync(int page,int index)
    {
        await NavigateAsync(page); activeSurface=surfaces[page];
        editingNote=current!.Pdf!.Pages[page].Annotations[index];
        selection=new Rect(24,24,1,1); ShowCard(selection,true); OcrText.Text=editingNote.Contents;
        var quote=editingNote.Elements.GetString("/ODQuote");
        CardStatus.Text=$"Автор: {editingNote.Title}"+(string.IsNullOrWhiteSpace(quote)?"":" · К тексту: «"+quote+"»");
    }
    void BuildMarkers(PageSurface surface)
    {
        if(commentDrag?.Surface==surface) return;
        surface.Markers.Children.Clear();
        if (current?.Pdf is null || !current.CanEditPdf) return;
        var page=current.Pdf.Pages[surface.Index];
        for (int i=0;i<page.Annotations.Count;i++)
        {
            var note=page.Annotations[i]; if (string.IsNullOrWhiteSpace(note.Contents)) continue;
            var position=PdfCommentPosition.Get(page,note,new Size(surface.Width,surface.Height));
            int index=i;
            var marker=CommentIcon.CreateButton($"{note.Title}: {note.Contents}");
            Canvas.SetLeft(marker,position.X); Canvas.SetTop(marker,position.Y);
            EnableCommentDragging(marker,surface,index);
            marker.Click+=async (_,_)=> { if (busy) return; try { await OpenCommentAsync(surface.Index,index); } catch (Exception error) { Error(error); } };
            surface.Markers.Children.Add(marker);
        }
    }
    async Task RefreshEditedPageAsync(int index)
    {
        ++viewGeneration;
        surfaces[index].Image.Source=null;
        await EnsurePageAsync(index,viewGeneration);
        RefreshComments(); await RefreshSideAsync();
    }
    void Author_Changed(object sender,SelectionChangedEventArgs e)
    {
        if (AuthorPicker?.SelectedItem is not string name) return;
        try { authors.Choose(name); } catch (Exception error) { Status.Text="Имя выбрано, но настройки не записаны: "+error.Message; }
    }
    void AddAuthor_Click(object sender,RoutedEventArgs e)
    {
        try { authors.Choose(NewAuthor.Text); AuthorPicker.SelectedItem=authors.Selected; NewAuthor.Clear(); Status.Text="Автор: "+authors.Selected; }
        catch (Exception error) { Error(error); }
    }
    async void Duplicate_Click(object sender,RoutedEventArgs e)
    {
        if (busy || current?.Pdf is null) return;
        if (current.IsWordDocument) { Status.Text="Сначала сохраните документ как PDF"; return; }
        var session=current; var author=authors.Selected;
        string safe=string.Concat(author.Select(c=>System.IO.Path.GetInvalidFileNameChars().Contains(c)?'_':c));
        var dialog=new SaveFileDialog { Filter="PDF|*.pdf",FileName=System.IO.Path.GetFileNameWithoutExtension(session.Name)+"_"+safe+".pdf",Title="Копия комментариев от имени: "+author };
        if (dialog.ShowDialog(this)!=true) return;
        try
        {
            SetBusy(true,"Создаю копию комментариев…");
            session.DuplicateWithAuthor(dialog.FileName,author);
            var copy=await Task.Run(()=>DocumentSession.Open(dialog.FileName));
            AddSessionTab(copy); await RenderAsync();
            await RememberRecentFileAsync(copy.Path);
            Status.Text="Копия сохранена. Автор комментариев: "+author;
        }
        catch (Exception error) { Error(error); }
        finally { SetBusy(false,Status.Text); }
    }
    void AddSessionTab(DocumentSession session)
    {
        sessions.Add(session); current=session;
        var header=new StackPanel { Orientation=Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text=session.Name,VerticalAlignment=VerticalAlignment.Center });
        var close=new Button { Content="×",Padding=new Thickness(6,0,6,0),Margin=new Thickness(12,0,0,0) };
        var tab=new TabItem { Header=header,Tag=session }; close.Click+=(_,_)=>CloseSession(session,tab);
        header.Children.Add(close); Tabs.Items.Add(tab); Tabs.SelectedItem=tab;
    }
}
