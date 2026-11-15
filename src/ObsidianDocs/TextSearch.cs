using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ObsidianDocs;
public partial class MainWindow
{
    sealed record SearchPage(int Page,List<TextWord> Words,string Text,int[] Starts);
    sealed record SearchHit(int Page,List<TextWord>? Words,TextPointer? Start=null,TextPointer? End=null);
    readonly List<SearchHit> searchHits=[];
    List<SearchPage>? searchPages;
    byte[]? searchBytes;
    DocumentSession? searchDocument;
    int searchHit=-1;
    int searchRevision;
    int wordSearchCount;
    string? wordSearchQuery;
    CancellationTokenSource? searchCancellation;
    DispatcherTimer searchTimer=null!;
    void ConfigureTextSearch()
    {
        searchTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(300) };
        searchTimer.Tick+=async (_,_)=> { searchTimer.Stop(); await SearchTextAsync(); };
        PreviewKeyDown+=async (_,e)=>
        {
            if(e.Handled) return;
            if(e.Key==Key.F && Keyboard.Modifiers==ModifierKeys.Control)
            {
                e.Handled=true; if(busy) return;
                FindBar.Visibility=Visibility.Visible; FindText.Focus(); FindText.SelectAll();
                if(searchHits.Count==0) QueueTextSearch();
            }
            else if(e.Key==Key.F3)
            {
                e.Handled=true;
                if(FindBar.Visibility!=Visibility.Visible) { FindBar.Visibility=Visibility.Visible; FindText.Focus(); }
                else await StepSearchAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            }
            else if(e.Key==Key.Escape && FindBar.Visibility==Visibility.Visible) { e.Handled=true; CloseFind_Click(this,new RoutedEventArgs()); }
            else if(e.Key==Key.C && Keyboard.Modifiers==ModifierKeys.Control && Viewer.IsKeyboardFocusWithin && selectedWords.Count>0) { e.Handled=true; CopySelectedText(); }
        };
    }
    void QueueTextSearch()
    {
        if(searchTimer is null) return;
        searchRevision++;
        wordSearchQuery=null; wordSearchCount=0;
        searchCancellation?.Cancel(); searchTimer.Stop();
        searchHits.Clear(); searchHit=-1; PaintSearchHighlights();
        if(FindBar.Visibility==Visibility.Visible && !string.IsNullOrWhiteSpace(FindText.Text)) searchTimer.Start();
        else FindStatus.Text="";
    }
    void FindText_Changed(object sender,TextChangedEventArgs e)=>QueueTextSearch();
    async void FindText_KeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter) { e.Handled=true; await StepSearchAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); }
    }
    void InvalidateTextSearch()
    {
        wordSearchQuery=null; wordSearchCount=0;
        searchRevision++;
        searchCancellation?.Cancel(); searchTimer?.Stop(); searchPages=null; searchBytes=null;
        searchHits.Clear(); searchHit=-1; PaintSearchHighlights();
        if(FindBar.Visibility==Visibility.Visible) FindStatus.Text="Документ изменён · Enter — искать";
    }
    void InvalidateWordTextSearch()
    {
        // Nothing to invalidate while the find bar is closed. Its next query
        // reads directly from the live Word document, never a PDF text cache.
        if(wordSearchQuery is null && wordSearchCount==0 && searchCancellation is null && !searchTimer.IsEnabled) return;
        wordSearchQuery=null; wordSearchCount=0; searchRevision++;
        searchCancellation?.Cancel(); searchTimer.Stop();
        if(FindBar.Visibility==Visibility.Visible) FindStatus.Text="Документ изменён · Enter — искать";
    }
    void ChangeSearchDocument()
    {
        searchDocument?.Editor?.ClearSearch();
        InvalidateTextSearch(); searchDocument=current;
        if(FindBar.Visibility==Visibility.Visible) QueueTextSearch();
    }
    async Task SearchTextAsync()
    {
        if(busy || current is null || string.IsNullOrWhiteSpace(FindText.Text)) return;
        searchTimer.Stop(); searchCancellation?.Cancel();
        using var cancellation=new CancellationTokenSource(); searchCancellation=cancellation;
        var session=current; var query=FindText.Text.Trim();
        FindStatus.Text="Поиск…";
        try
        {
            var pattern=string.Join(@"\s+",Regex.Split(query,@"\s+").Select(Regex.Escape));
            var regex=new Regex(pattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2));
            List<SearchHit> hits;
            if(session.IsWordDocument && session.Editor is { } editor)
            {
                int revision=searchRevision;
                var result=await editor.SearchAsync(query);
                if(cancellation.IsCancellationRequested || current!=session || revision!=searchRevision) return;
                searchDocument=session; wordSearchQuery=query; wordSearchCount=result.Total; searchHit=result.Index;
                FindStatus.Text=result.Total==0 ? "Совпадений нет" : $"{result.Index+1} / {result.Total}";
                return;
            }
            else if(session.RenderBytes is { } bytes)
            {
                var pages=ReferenceEquals(searchBytes,bytes) ? searchPages : null;
                if(pages is null) pages=await Task.Run(()=>BuildSearchPages(bytes,cancellation.Token));
                cancellation.Token.ThrowIfCancellationRequested();
                if(current!=session) return;
                searchBytes=bytes; searchPages=pages;
                hits=await Task.Run(()=>FindPdfHits(pages,regex,cancellation.Token));
            }
            else { FindStatus.Text="Нет текстового слоя · нужен OCR"; return; }
            if(cancellation.IsCancellationRequested || current!=session) return;
            searchDocument=session; searchHits.Clear(); searchHits.AddRange(hits); searchHit=-1;
            if(hits.Count==0) FindStatus.Text="Совпадений нет";
            else await StepSearchAsync(1);
        }
        catch(OperationCanceledException) { }
        catch(Exception error) { if(!cancellation.IsCancellationRequested) FindStatus.Text="Ошибка поиска: "+error.Message; }
        finally { if(ReferenceEquals(searchCancellation,cancellation)) searchCancellation=null; }
    }
    static List<SearchPage> BuildSearchPages(byte[] bytes,CancellationToken cancellation)
    {
        using var pdf=UglyToad.PdfPig.PdfDocument.Open(bytes);
        var result=new List<SearchPage>();
        for(int i=1;i<=pdf.NumberOfPages;i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var words=PdfTextComments.ReadWords(pdf.GetPage(i));
            var text=new StringBuilder(); var starts=new int[words.Count];
            for(int w=0;w<words.Count;w++) { if(w>0) text.Append(' '); starts[w]=text.Length; text.Append(words[w].Text); }
            result.Add(new SearchPage(i-1,words,text.ToString(),starts));
        }
        return result;
    }
    static List<SearchHit> FindPdfHits(List<SearchPage> pages,Regex regex,CancellationToken cancellation)
    {
        var hits=new List<SearchHit>();
        foreach(var page in pages)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach(Match match in regex.Matches(page.Text))
            {
                cancellation.ThrowIfCancellationRequested();
                var words=page.Words.Where((w,i)=>page.Starts[i]<match.Index+match.Length && page.Starts[i]+w.Text.Length>match.Index).ToList();
                if(words.Count>0) hits.Add(new SearchHit(page.Page,words));
            }
        }
        return hits;
    }
    async Task StepSearchAsync(int direction)
    {
        if(busy || current is null) return;
        if(current.IsWordDocument && current.Editor is { } wordEditor)
        {
            if(wordSearchQuery!=FindText.Text.Trim() || wordSearchCount==0) { await SearchTextAsync(); return; }
            var wordSession=current;
            int wordRevision=searchRevision;
            try
            {
                var result=await wordEditor.SearchAsync(wordSearchQuery,direction);
                if(current!=wordSession || wordRevision!=searchRevision) return;
                wordSearchCount=result.Total; searchHit=result.Index;
                FindStatus.Text=result.Total==0 ? "Совпадений нет" : $"{result.Index+1} / {result.Total}";
            }
            catch(Exception error) { FindStatus.Text="Ошибка поиска: "+error.Message; }
            return;
        }
        if(searchHits.Count==0) { if(!string.IsNullOrWhiteSpace(FindText.Text)) await SearchTextAsync(); return; }
        if(current!=searchDocument) return;
        searchHit=(searchHit+direction+searchHits.Count)%searchHits.Count;
        var hit=searchHits[searchHit]; var session=current; int revision=searchRevision,index=searchHit;
        FindStatus.Text=$"{searchHit+1} / {searchHits.Count}";
        {
            try { await NavigateAsync(hit.Page); }
            catch(Exception error) { FindStatus.Text="Не удалось показать совпадение: "+error.Message; return; }
            if(current!=session || revision!=searchRevision || index!=searchHit || hit.Page>=surfaces.Count) return;
            var surface=surfaces[hit.Page];
            if(hit.Words is { Count:>0 } words) Viewer.ScrollToVerticalOffset(surface.Top+words.Min(w=>w.Bounds.Top)*surface.Height-Viewer.ViewportHeight*.3);
            PaintSearchHighlights();
        }
    }
    void PaintSearchHighlights()
    {
        if(current?.IsWordDocument==true) return;
        foreach(var surface in surfaces) surface.SearchHighlights.Children.Clear();
        if(current!=searchDocument) return;
        for(int i=0;i<searchHits.Count;i++)
        {
            var hit=searchHits[i];
            if(hit.Words is null || hit.Page<0 || hit.Page>=surfaces.Count || !InPageWindow(hit.Page)) continue;
            var surface=surfaces[hit.Page];
            foreach(var rect in PdfTextComments.HighlightBands(hit.Words,surface.Ratio))
            {
                var box=new Rectangle { Width=rect.Width*surface.Width,Height=rect.Height*surface.Height,Fill=Brush(i==searchHit ? "#A0FF9D30" : "#90FFE260"),Stroke=i==searchHit ? Brush("#D58A18") : null,StrokeThickness=i==searchHit ? 1 : 0 };
                Canvas.SetLeft(box,rect.X*surface.Width); Canvas.SetTop(box,rect.Y*surface.Height); surface.SearchHighlights.Children.Add(box);
            }
        }
    }
    async void FindNext_Click(object sender,RoutedEventArgs e)=>await StepSearchAsync(1);
    async void FindPrevious_Click(object sender,RoutedEventArgs e)=>await StepSearchAsync(-1);
    void CloseFind_Click(object sender,RoutedEventArgs e)
    {
        searchCancellation?.Cancel(); searchTimer.Stop(); FindBar.Visibility=Visibility.Collapsed;
        searchRevision++;
        searchHits.Clear(); searchHit=-1; PaintSearchHighlights();
        wordSearchQuery=null; wordSearchCount=0; current?.Editor?.ClearSearch();
        if(current?.IsWordDocument==true) current.Editor?.Focus(); else Viewer.Focus();
    }
}
