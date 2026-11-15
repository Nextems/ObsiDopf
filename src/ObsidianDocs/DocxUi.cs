using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ObsidianDocs;
public partial class MainWindow
{
    void ConfigureDocumentUi()
    {
        bool docx=current?.IsWordDocument==true;
        bool editing=docx;
        var pdfVisibility=docx ? Visibility.Collapsed : Visibility.Visible;
        NoteButton.Visibility=TextNoteButton.Visibility=SideCommentsButton.Visibility=pdfVisibility;
        OcrButton.Visibility=OcrLanguage.Visibility=docx ? Visibility.Collapsed : Visibility.Visible;
        UpdateCommentPanel();
        PdfEditButton.Visibility=current?.CanEditPdf==true ? Visibility.Visible : Visibility.Collapsed;
        DocxHost.Visibility=editing ? Visibility.Visible : Visibility.Collapsed;
        DocxTools.Visibility=Visibility.Collapsed;
        Viewer.Visibility=editing ? Visibility.Collapsed : Visibility.Visible;
        PdfNavigation.Visibility=editing ? Visibility.Collapsed : Visibility.Visible;
        SetTool();
        // Detach before changing tabs so an editor never has two visual parents.
        DocxHost.Content=null; DocxTools.Content=null;
        WordMapHost.Content=null;
        WordMapHost.Visibility=docx ? Visibility.Visible : Visibility.Collapsed;
        SideList.Visibility=docx ? Visibility.Collapsed : Visibility.Visible;
        if (!docx) return;
        var session=current!;
        if(session.Editor is null)
        {
            session.Editor=new WordEditor(session.DocxBytes!);
            session.Editor.Changed+=()=>
            {
                session.Dirty=true;
                // Word search has its own native highlights. Avoid touching
                // every PDF surface for each keystroke in another document.
                if(current==session) InvalidateWordTextSearch();
            };
            session.Editor.PagesChanged+=()=> { if(current==session) UpdateWordPageInfo(); };
            session.Editor.Error+=message=> { if(current==session) Status.Text=message; };
            session.Editor.AskAi+=async (text,prompt)=> { if(current==session) await SendChatAsync(prompt,text,comment:false); };
            session.Editor.ZoomDelta+=delta=> { if(current==session) ChangeZoom(zoom+delta); };
            session.Editor.ZoomChanged+=value=> { if(current==session) { zoom=value; ZoomStatus.Text=$"{zoom:P0}"; } };
            session.Editor.Shortcut+=async (key,shift)=>
            {
                if(current!=session || busy) return;
                try
                {
                    switch(key)
                    {
                        case "s": if(shift) Save_Click(this,new RoutedEventArgs()); else await SaveCurrentAsync(); break;
                        case "o": Open_Click(this,new RoutedEventArgs()); break;
                        case "n": NewDocx_Click(this,new RoutedEventArgs()); break;
                        case "f": FindBar.Visibility=Visibility.Visible; FindText.Focus(); FindText.SelectAll(); break;
                        case "f3": if(FindBar.Visibility==Visibility.Visible) await StepSearchAsync(shift ? -1 : 1); else { FindBar.Visibility=Visibility.Visible; FindText.Focus(); } break;
                        case "escape": if(FindBar.Visibility==Visibility.Visible) CloseFind_Click(this,new RoutedEventArgs()); break;
                        case "+": case "=": ChangeZoom(zoom+.1); break;
                        case "-": ChangeZoom(zoom-.1); break;
                        case "0": ChangeZoom(1); break;
                    }
                }
                catch(Exception error) { Error(error); }
            };
        }
        if(!editing) return;
        DocxHost.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        DocxHost.VerticalContentAlignment=VerticalAlignment.Stretch;
        DocxHost.Content=session.Editor.View; DocxTools.Content=session.Editor.Tools;
        WordMapHost.Content=session.Editor.MiniMap;
        session.Editor.SetZoom(zoom); ZoomStatus.Text=$"{zoom:P0}";
        Welcome.Visibility=Visibility.Collapsed;
        PageStack.Visibility=Visibility.Collapsed;
        SideList.Items.Clear(); CommentList.Children.Clear();
        SideHint.Text=(session.IsDoc ? "DOC → DOCX" : "DOCX")+" · редактирование текста и таблиц. PDF создаётся только при экспорте.";
        UpdateWordPageInfo();
        PageStatus.Text=session.IsDoc ? "DOC → DOCX" : "DOCX";
        Status.Text="DOCX · Ctrl+S — сохранить · Ctrl+Z — отменить";
    }
    void UpdateWordPageInfo()
    {
        if(current?.Editor is not { } editor || !current.IsWordDocument) return;
        SideHint.Text=$"{(current.IsDoc ? "DOC → DOCX" : "DOCX")} · страниц: {editor.PageCount}. Разметка печати · выберите страницу для перехода.";
    }
    async Task SaveDocxAsync(string destination)
    {
        var session=current!;
        var editor=session.Editor!;
        long revision=editor.Revision;
        var bytes=await WriteDocxCopyAsync(destination);
        session.DocxBytes=bytes;
        session.IsUntitled=false;
        session.Dirty=editor.Revision!=revision;
        Status.Text="Сохранено: "+destination;
    }
    async Task<byte[]> WriteDocxCopyAsync(string destination)
    {
        if(!Path.GetExtension(destination).Equals(".docx",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выберите расширение .docx. Исходный DOC сохраняется без изменений.");
        var bytes=await current!.Editor!.SnapshotAsync();
        var temp=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!,Guid.NewGuid()+".tmp");
        try { File.WriteAllBytes(temp,bytes); File.Move(temp,destination,true); }
        finally { if(File.Exists(temp)) File.Delete(temp); }
        return bytes;
    }
    async Task ExportDocxAsync(string destination)
    {
        var bytes=await current!.Editor!.SnapshotAsync();
        SetBusy(true,"Подготовка PDF через LibreOffice…");
        DocxHost.IsEnabled=false;
        try
        {
            var pdf=await Task.Run(()=>RenderDocxBytes(bytes));
            var temp=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!,Guid.NewGuid()+".tmp");
            try { await File.WriteAllBytesAsync(temp,pdf); File.Move(temp,destination,true); }
            finally { if(File.Exists(temp)) File.Delete(temp); }
            Status.Text="Экспорт завершён: "+destination;
        }
        finally { DocxHost.IsEnabled=true; SetBusy(false,Status.Text); }
    }
    static byte[] RenderDocxBytes(byte[] bytes)
    {
        var folder=Path.Combine(Path.GetTempPath(),"ObsidianDocs",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { var path=Path.Combine(folder,"document.docx"); File.WriteAllBytes(path,bytes); return DocxRenderer.Render(path); }
        finally { try { Directory.Delete(folder,true); } catch(IOException) { } catch(UnauthorizedAccessException) { } }
    }
}
