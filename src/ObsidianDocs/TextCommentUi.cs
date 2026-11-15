using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
namespace ObsidianDocs;
public partial class MainWindow
{
    bool textNoteMode;
    int selectionEpoch;
    Task wordsTask=Task.CompletedTask;
    List<TextWord> selectedWords=[];
    void TextNoteMode_Click(object sender,RoutedEventArgs e)
    {
        if(busy) return;
        bool enable=!textNoteMode; SetTool(textNote:enable);
        Status.Text=enable ? "Выделите слова мышью, затем введите комментарий." : "Режим комментариев отключён · выделите текст и нажмите Ctrl+C";
    }
    async Task LoadTextWordsAsync(PageSurface surface)
    {
        if (surface.Words is not null || current?.RenderBytes is null) return;
        var bytes=current.RenderBytes;
        surface.Words=await Task.Run(()=>PdfTextComments.ReadWords(bytes,surface.Index));
    }
    void UpdateTextSelection(Point end)
    {
        selectedWords=PdfTextComments.Select(activeSurface.Words ?? [],new Point(start.X/activeSurface.Width,start.Y/activeSurface.Height),new Point(end.X/activeSurface.Width,end.Y/activeSurface.Height));
        PaintTextSelection();
    }
    void PaintTextSelection()
    {
        activeSurface.TextHighlights.Children.Clear();
        if(selectedWords.Count==0 && (ocrMode || noteMode || pdfTextMode)) return;
        selection=Rect.Empty;
        foreach (var band in PdfTextComments.HighlightBands(selectedWords,activeSurface.Ratio))
        {
            var r=new Rect(band.X*activeSurface.Width,band.Y*activeSurface.Height,band.Width*activeSurface.Width,band.Height*activeSurface.Height);
            var box=new Rectangle { Width=r.Width,Height=r.Height,Fill=Brush("#663A91F1") };
            Canvas.SetLeft(box,r.X); Canvas.SetTop(box,r.Y); activeSurface.TextHighlights.Children.Add(box);
            selection.Union(r);
        }
    }
    async Task FinishTextSelectionAsync(Point end,bool comment=true)
    {
        int epoch=selectionEpoch; var surface=activeSurface;
        try
        {
            await wordsTask;
            if (epoch!=selectionEpoch || surface!=activeSurface || (comment ? !textNoteMode : ocrMode || noteMode || pdfTextMode)) return;
            UpdateTextSelection(end);
            if (selectedWords.Count==0) { Status.Text="В этой области нет выделяемого текста. Для скана создайте PDF с OCR-слоем."; return; }
            if(!comment)
            {
                Viewer.Focus(); Status.Text="Текст выделен · Ctrl+C или правая кнопка мыши → Копировать";
                ShowAiSelectionPopup(string.Join(" ",selectedWords.Select(w=>w.Text)),SelectionCanvas.TranslatePoint(end,Workspace));
                return;
            }
            ShowCard(selection,true); OcrText.Text=""; OcrText.Focus();
            CardStatus.Text="К тексту: «"+string.Join(" ",selectedWords.Select(w=>w.Text))+"»";
        }
        catch (Exception error) { Status.Text="Не удалось выделить текст: "+error.Message; }
    }
    async void DeleteComment_Click(object sender,RoutedEventArgs e)
    {
        if (busy || current?.Pdf is null || editingNote is null) return;
        var notes=current.Pdf.Pages[activeSurface.Index].Annotations;
        int index=Enumerable.Range(0,notes.Count).FirstOrDefault(i=>notes[i]==editingNote,-1);
        if (index<0) return;
        try { await DeleteCommentAsync(activeSurface.Index,index); } catch (Exception error) { Error(error); }
    }
    async Task DeleteCommentAsync(int pageIndex,int index)
    {
        if (busy || current?.Pdf is null) return;
        SetBusy(true,"Удаляю комментарий…");
        try
        {
            var notes=current.Pdf.Pages[pageIndex].Annotations;
            var note=notes[index]; var popup=note.Elements.GetReference("/Popup");
            notes.Remove(note);
            for (int i=notes.Count-1;i>=0;i--)
                if (popup is not null && notes[i].Reference?.ObjectID==popup.ObjectID) notes.Remove(notes[i]);
            current.Dirty=true; current.Snapshot(); HideCard(); await RefreshEditedPageAsync(pageIndex);
            Status.Text="Комментарий удалён · сохраните PDF";
        }
        finally { SetBusy(false,Status.Text); }
    }
}
