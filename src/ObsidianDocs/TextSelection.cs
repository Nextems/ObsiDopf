using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ObsidianDocs;
public partial class MainWindow
{
    bool ocrMode;
    void SetTool(bool ocr=false,bool note=false,bool textNote=false,bool addText=false)
    {
        HideCard(); ocrMode=ocr; noteMode=note; textNoteMode=textNote; pdfTextMode=addText;
        OcrButton.Background=Brush(ocr ? "#69518E" : "#303039");
        NoteButton.Background=Brush(note ? "#69518E" : "#303039");
        TextNoteButton.Background=Brush(textNote ? "#69518E" : "#303039");
        Viewer.Cursor=ocr || note || addText ? Cursors.Cross : Cursors.IBeam;
    }
    void CopySelectedText()
    {
        if(selectedWords.Count==0) return;
        try { Clipboard.SetText(string.Join(" ",selectedWords.Select(w=>w.Text))); Status.Text="Выделенный текст скопирован"; }
        catch(Exception error) { Error(error); }
    }
    ContextMenu CreateTextSelectionMenu(PageSurface surface)
    {
        var menu=new ContextMenu { Background=Brush("#2C2935"),Foreground=Brush("#E8E5F0") };
        var copy=new MenuItem { Header="Копировать · Ctrl+C" }; menu.Items.Add(copy);
        copy.Click+=(_,_)=>CopySelectedText();
        menu.Opened+=(_,_)=>copy.IsEnabled=surface==activeSurface && selectedWords.Count>0;
        return menu;
    }
}
