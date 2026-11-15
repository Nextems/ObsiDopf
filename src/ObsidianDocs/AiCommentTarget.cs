namespace ObsidianDocs;

internal sealed record AiCommentTarget(DocumentSession Session,int Page,TextWord[] Words,TextWord[] PageWords,byte[]? PdfBytes);

public partial class MainWindow
{
    AiCommentTarget? chatCommentTarget;
    AiCommentTarget? CapturePdfAiTarget()
    {
        if(current?.CanEditPdf!=true || selectedWords.Count==0) return null;
        return new(current,activeSurface.Index,selectedWords.ToArray(),activeSurface.Words?.ToArray() ?? [],current.RenderBytes);
    }
    async Task AddAiReplyCommentAsync(AiCommentTarget target,string text,string author)
    {
        var session=target.Session;
        if(!sessions.Contains(session)) throw new InvalidOperationException("Исходный документ закрыт.");
        if(!session.CanEditPdf || session.RenderBytes is null || target.Page>=session.Count) throw new InvalidOperationException("Страница документа больше недоступна.");
        if(!ReferenceEquals(session.RenderBytes,target.PdfBytes))
        {
            var bytes=session.RenderBytes;
            var words=await Task.Run(()=>PdfTextComments.ReadWords(bytes,target.Page));
            if(!ReferenceEquals(bytes,session.RenderBytes) || !words.SequenceEqual(target.PageWords))
                throw new InvalidOperationException("Содержимое страницы изменилось. Комментарий не добавлен к другому фрагменту.");
        }
        if(!sessions.Contains(session)) throw new InvalidOperationException("Исходный документ закрыт.");
        session.ApplyPdfEdit(pdf=>PdfTextComments.Add(pdf,target.Page,target.Words,text,author));
        if(current==session)
        {
            try { RefreshComments(); await RefreshEditedPageAsync(target.Page); }
            catch(Exception error) { Status.Text="Комментарий добавлен, но не удалось обновить просмотр: "+error.Message; }
        }
    }
}
