using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Pdf;

namespace ObsidianDocs;
public partial class MainWindow
{
    bool pdfTextMode;
    void PdfEdit_Click(object sender,RoutedEventArgs e)
    {
        if(busy || current?.CanEditPdf!=true) return;
        var menu=new ContextMenu { Background=Brush("#2C2935"),Foreground=Brush("#E8E5F0"),PlacementTarget=PdfEditButton,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string title,Func<Task> action,bool enabled=true)
        {
            var item=new MenuItem { Header=title,IsEnabled=enabled,Background=Brush("#2C2935"),Foreground=Brush("#E8E5F0") };
            item.Click+=async (_,_)=> { try { await action(); } catch(Exception error) { Error(error); } }; menu.Items.Add(item);
        }
        Item("Добавить текст…",()=>
        {
            SetTool(addText:!pdfTextMode);
            OcrButton.Background=NoteButton.Background=TextNoteButton.Background=Brush("#303039");
            Status.Text=pdfTextMode ? "Щёлкните по странице в месте вставки текста · Esc — отменить" : "Режим вставки отключён · выделите текст и нажмите Ctrl+C";
            return Task.CompletedTask;
        });
        menu.Items.Add(new Separator());
        Item("Повернуть страницу вправо",()=>RotatePdfPageAsync(90));
        Item("Повернуть страницу влево",()=>RotatePdfPageAsync(-90));
        Item("Добавить пустую страницу после текущей",AddBlankPdfPageAsync);
        Item("Удалить текущую страницу",DeletePdfPageAsync,current.Count>1);
        menu.Items.Add(new Separator());
        Item("Отменить правку PDF · Ctrl+Z",UndoPdfEditAsync,current.CanUndoPdf);
        menu.IsOpen=true;
    }
    async Task RunPdfEditAsync(Action<PdfDocument> edit,string message,int? targetPage=null)
    {
        if(busy || current?.CanEditPdf!=true) return;
        try
        {
            SetBusy(true,"Применяю правку PDF…"); HideCard();
            current.ApplyPdfEdit(edit,targetPage);
            await RenderAsync(); Status.Text=message+" · Ctrl+S — сохранить · Ctrl+Z — отменить";
        }
        finally { SetBusy(false,Status.Text); }
    }
    Task RotatePdfPageAsync(int angle)
    {
        int index=current!.Page;
        return RunPdfEditAsync(pdf=>pdf.Pages[index].Rotate=((pdf.Pages[index].Rotate+angle)%360+360)%360,"Страница повёрнута");
    }
    Task AddBlankPdfPageAsync()
    {
        int index=current!.Page;
        return RunPdfEditAsync(pdf=>
        {
            var source=pdf.Pages[index]; var box=PdfTextComments.VisibleBox(source);
            bool rotated=source.Rotate%180!=0;
            var page=pdf.Pages.Insert(index+1);
            page.Width=XUnit.FromPoint(rotated ? box.Height : box.Width);
            page.Height=XUnit.FromPoint(rotated ? box.Width : box.Height);
        },"Добавлена пустая страница",index+1);
    }
    Task DeletePdfPageAsync()
    {
        if(current is null || current.Count<=1) return Task.CompletedTask;
        int index=current.Page;
        return RunPdfEditAsync(pdf=>pdf.Pages.RemoveAt(index),"Страница удалена",Math.Min(index,current.Count-2));
    }
    async Task UndoPdfEditAsync()
    {
        if(busy || current?.CanUndoPdf!=true) return;
        try
        {
            SetBusy(true,"Отменяю правку PDF…"); HideCard(); current.UndoPdfEdit();
            await RenderAsync(); Status.Text="Правка PDF отменена · Ctrl+S — сохранить";
        }
        finally { SetBusy(false,Status.Text); }
    }
    async void AddPdfTextAtAsync(Point location)
    {
        if(busy || current?.CanEditPdf!=true) return;
        SetTool();
        try
        {
            var dialog=new Window { Owner=this,Title="Добавить текст в PDF",Width=480,Height=370,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,Background=Brush("#25252D"),Foreground=Brush("#E8E5F0"),Resources=Resources,ShowInTaskbar=false };
            dialog.SourceInitialized+=(_,_)=>WindowTheme.Apply(dialog);
            var panel=new StackPanel { Margin=new Thickness(18) }; dialog.Content=panel;
            panel.Children.Add(new TextBlock { Text="Новый текст в выбранном месте страницы",Margin=new Thickness(0,0,0,12) });
            var text=new TextBox { Height=160,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxLength=10000 };
            panel.Children.Add(text);
            var row=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(0,12,0,0) }; panel.Children.Add(row);
            var font=new ComboBox { Width=210,Height=32,ItemsSource=Fonts.SystemFontFamilies.Select(f=>f.Source).OrderBy(n=>n).ToList(),SelectedItem="Arial" }; row.Children.Add(font);
            var size=new TextBox { Text="12",Width=55,Height=32,Margin=new Thickness(8,0,0,0),Padding=new Thickness(6),ToolTip="Размер в пунктах" }; row.Children.Add(size);
            var warning=new TextBlock { Foreground=Brush("#DF9DAA"),Margin=new Thickness(0,8,0,0) }; panel.Children.Add(warning);
            var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right }; panel.Children.Add(buttons);
            var cancel=new Button { Content="Отмена",IsCancel=true }; buttons.Children.Add(cancel);
            var insert=new Button { Content="Вставить",Background=Brush("#69518E") }; buttons.Children.Add(insert);
            double fontSize=12;
            insert.Click+=(_,_)=>
            {
                if(string.IsNullOrWhiteSpace(text.Text)) { warning.Text="Введите текст."; return; }
                if(!double.TryParse(size.Text.Replace(',','.'),CultureInfo.InvariantCulture,out fontSize) || fontSize<1 || fontSize>144) { warning.Text="Размер шрифта: от 1 до 144 пунктов."; return; }
                dialog.DialogResult=true;
            };
            dialog.Loaded+=(_,_)=>text.Focus();
            if(dialog.ShowDialog()!=true) return;
            int index=current.Page; string content=text.Text; string family=font.SelectedItem as string ?? "Arial";
            await RunPdfEditAsync(pdf=>DrawPdfText(pdf.Pages[index],location,content,family,fontSize),"Текст добавлен");
        }
        catch(Exception error) { Error(error); }
    }
    static void DrawPdfText(PdfPage page,Point location,string text,string family,double fontSize)
    {
        var anchor=PdfTextComments.ToPdf(page,location);
        var box=PdfTextComments.VisibleBox(page); int rotation=((page.Rotate%360)+360)%360;
        double width=(rotation%180==0 ? box.Width : box.Height)*(1-location.X);
        double height=(rotation%180==0 ? box.Height : box.Width)*(1-location.Y);
        var font=new XFont(family,fontSize);
        if(width<fontSize || height<font.GetHeight()) throw new InvalidOperationException("Для текста мало места. Выберите точку дальше от края страницы.");
        // Work in unrotated media coordinates, then compensate for /Rotate so
        // text stays horizontal in the displayed (possibly cropped) page.
        int originalRotation=page.Rotate;
        try
        {
            page.Rotate=0;
            using var graphics=XGraphics.FromPdfPage(page,XGraphicsPdfPageOptions.Append);
            graphics.TranslateTransform(anchor.X-page.MediaBox.X1,page.MediaBox.Y2-anchor.Y);
            graphics.RotateTransform(-rotation);
            var rectangle=new XRect(0,0,width,height);
            graphics.IntersectClip(rectangle);
            var formatter=new XTextFormatter(graphics);
            formatter.DrawString(text,font,XBrushes.Black,rectangle,XStringFormats.TopLeft);
        }
        finally { page.Rotate=originalRotation; }
    }
}
