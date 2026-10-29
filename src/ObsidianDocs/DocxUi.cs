using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ObsidianDocs;
public partial class MainWindow
{
    double savedCommentWidth=270;
    bool showingDocx;
    void ConfigureDocumentUi()
    {
        bool docx=current?.IsDocx==true;
        bool editing=docx;
        var pdfVisibility=docx ? Visibility.Collapsed : Visibility.Visible;
        NoteButton.Visibility=TextNoteButton.Visibility=SideCommentsButton.Visibility=pdfVisibility;
        OcrButton.Visibility=OcrLanguage.Visibility=pdfVisibility;
        CommentPanel.Visibility=pdfVisibility;
        DocxHost.Visibility=DocxTools.Visibility=editing ? Visibility.Visible : Visibility.Collapsed;
        Viewer.Visibility=editing ? Visibility.Collapsed : Visibility.Visible;
        if(docx && !showingDocx) savedCommentWidth=CommentColumn.Width.Value;
        if(docx) CommentColumn.Width=new GridLength(0);
        else if(showingDocx) CommentColumn.Width=new GridLength(savedCommentWidth);
        showingDocx=docx;
        PdfNavigation.Visibility=editing ? Visibility.Collapsed : Visibility.Visible;
        // Detach before changing tabs so an editor never has two visual parents.
        DocxHost.Content=null; DocxTools.Content=null;
        if (!docx) return;
        var session=current!;
        if(session.Editor is null)
        {
            session.Editor=new DocxEditor(session.DocxBytes!);
            session.Editor.Changed+=()=> { session.Dirty=true; };
        }
        if(!editing) return;
        DocxHost.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        DocxHost.VerticalContentAlignment=VerticalAlignment.Stretch;
        DocxHost.Content=session.Editor.View; DocxTools.Content=session.Editor.Tools;
        Welcome.Visibility=Visibility.Collapsed;
        PageStack.Visibility=Visibility.Collapsed;
        SideList.Items.Clear(); CommentList.Children.Clear();
        foreach(var item in session.Editor.Outline()) SideList.Items.Add(new ListBoxItem { Content=new TextBlock { Text=item.Label,TextWrapping=TextWrapping.Wrap },Tag=item.Block });
        SideHint.Text="DOCX · редактирование текста и таблиц. Точная печатная верстка формируется при экспорте в PDF.";
        PageStatus.Text="DOCX";
        Status.Text="DOCX · Ctrl+S — сохранить · Ctrl+Z — отменить";
    }
    void SaveDocx(string destination)
    {
        var session=current!;
        var bytes=session.Editor!.Snapshot();
        var temp=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!,Guid.NewGuid()+".tmp");
        try { File.WriteAllBytes(temp,bytes); File.Move(temp,destination,true); }
        finally { if(File.Exists(temp)) File.Delete(temp); }
        session.DocxBytes=bytes;
        session.Dirty=false;
        Status.Text="Сохранено: "+destination;
    }
    async Task ExportDocxAsync(string destination)
    {
        var bytes=current!.Editor!.Snapshot();
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
