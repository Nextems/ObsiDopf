using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace ObsidianDocs;

internal sealed class BatchConversionWindow : Window
{
    sealed class Row(string path) : INotifyPropertyChanged
    {
        public string Path { get; }=path;
        string status="Готов к конвертации";
        public string Status { get=>status; set { status=value; PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Display))); } }
        public string Display=>System.IO.Path.GetFileName(Path)+" → "+BatchConversion.TargetExtension(Path).TrimStart('.').ToUpperInvariant()+"\n"+Path+"\n"+Status;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    readonly ObservableCollection<Row> files=[];
    readonly ListBox list=new() { SelectionMode=SelectionMode.Extended,Background=Brush("#1E1E24"),BorderBrush=Brush("#45414F"),BorderThickness=new Thickness(1) };
    readonly TextBox destinationPath=new() { IsReadOnly=true,Text="Рядом с исходными файлами",VerticalContentAlignment=VerticalAlignment.Center };
    readonly Button chooseDestination=new() { Content="Выбрать папку…" },resetDestination=new() { Content="Рядом с файлами" };
    readonly Button add=new() { Content="＋ Выбрать файлы…" },opened=new() { Content="Добавить открытые" },remove=new() { Content="Убрать выбранные" };
    readonly Button apply=new() { Content="Конвертировать",Background=Brush("#69518E") },close=new() { Content="Закрыть" };
    readonly ComboBox language=new() { MinWidth=100 };
    readonly TextBlock progress=new() { Text="Выберите PDF, DOCX или DOC. Формат результата определяется автоматически.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,10,3,5) };
    readonly Func<IEnumerable<string>> openFiles;
    readonly Func<string,string?> protect;
    CancellationTokenSource? cancellation;
    string? outputDirectory;
    public int SavedCount { get; private set; }

    internal BatchConversionWindow(Window owner,Func<IEnumerable<string>> openFiles,Func<string,string?> protect,string ocrLanguage)
    {
        Owner=owner; Resources=owner.Resources; this.openFiles=openFiles; this.protect=protect;
        Title="Глобальные действия · Конвертация файлов"; Width=880; Height=710; MinWidth=700; MinHeight=550;
        WindowStartupLocation=WindowStartupLocation.CenterOwner; ShowInTaskbar=false;
        Background=Brush("#25252D"); Foreground=Brush("#E8E5F0"); SourceInitialized+=(_,_)=>WindowTheme.Apply(this);
        var root=new Grid { Margin=new Thickness(20) }; Content=root;
        root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        var header=new StackPanel(); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text="Конвертация PDF ⇄ DOCX",FontSize=21,Margin=new Thickness(3,0,3,12) });
        var buttons=new WrapPanel(); buttons.Children.Add(add); buttons.Children.Add(opened); buttons.Children.Add(remove); header.Children.Add(buttons);
        header.Children.Add(new TextBlock { Text="Папка результатов",Margin=new Thickness(3,12,3,3),Foreground=Brush("#B9B0C7") });
        var destinationRow=new DockPanel { Margin=new Thickness(3,2,3,8) };
        DockPanel.SetDock(chooseDestination,Dock.Right); DockPanel.SetDock(resetDestination,Dock.Right);
        destinationRow.Children.Add(chooseDestination); destinationRow.Children.Add(resetDestination); destinationRow.Children.Add(destinationPath); header.Children.Add(destinationRow);
        AutomationProperties.SetName(destinationPath,"Папка результатов конвертации");
        var ocrRow=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(3,0,3,8) };
        ocrRow.Children.Add(new TextBlock { Text="Язык OCR для сканов",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0) });
        language.Items.Add(new ComboBoxItem { Content="Русский",Tag="ru" }); language.Items.Add(new ComboBoxItem { Content="English",Tag="en" });
        language.SelectedIndex=ocrLanguage=="en" ? 1 : 0; ocrRow.Children.Add(language); header.Children.Add(ocrRow);
        AutomationProperties.SetName(language,"Язык OCR для конвертации PDF без текста");
        header.Children.Add(new TextBlock { Text="PDF → DOCX: текст с разделением по страницам, без исходной вёрстки, изображений и комментариев. Страницы без текста распознаются OCR. DOC/DOCX → PDF: экспорт документа через LibreOffice.\nРезультаты сохраняются отдельными файлами. При совпадении имени добавляется номер. Открытые документы с несохранёнными правками сначала сохраните.",TextWrapping=TextWrapping.Wrap,Foreground=Brush("#B9B0C7"),Margin=new Thickness(3,0,3,12) });
        var text=new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty,new Binding(nameof(Row.Display))); text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);
        list.ItemTemplate=new DataTemplate { VisualTree=text }; list.ItemsSource=files;
        AutomationProperties.SetName(list,"Выбранные файлы и результаты конвертации"); Grid.SetRow(list,1); root.Children.Add(list);
        var footer=new StackPanel(); Grid.SetRow(footer,2); root.Children.Add(footer); footer.Children.Add(progress);
        var actions=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right }; actions.Children.Add(close); actions.Children.Add(apply); footer.Children.Add(actions);
        add.Click+=(_,_)=> { var picker=new OpenFileDialog { Filter="PDF и Word|*.pdf;*.docx;*.doc|PDF|*.pdf|Word|*.docx;*.doc",Multiselect=true,Title="Файлы для глобальной конвертации" }; if(picker.ShowDialog(this)==true) Add(picker.FileNames); };
        opened.Click+=(_,_)=>Add(openFiles());
        remove.Click+=(_,_)=> { foreach(var row in list.SelectedItems.Cast<Row>().ToArray()) files.Remove(row); UpdateButtons(); };
        list.SelectionChanged+=(_,_)=>UpdateButtons();
        chooseDestination.Click+=(_,_)=>
        {
            var picker=new OpenFolderDialog { Title="Папка для результатов конвертации",Multiselect=false };
            if(outputDirectory is not null) picker.InitialDirectory=outputDirectory;
            if(picker.ShowDialog(this)==true) { outputDirectory=picker.FolderName; destinationPath.Text=outputDirectory; destinationPath.ToolTip=outputDirectory; UpdateButtons(); }
        };
        resetDestination.Click+=(_,_)=> { outputDirectory=null; destinationPath.Text="Рядом с исходными файлами"; destinationPath.ToolTip=destinationPath.Text; UpdateButtons(); };
        apply.Click+=async (_,_)=>await RunAsync();
        close.Click+=(_,_)=> { if(cancellation is null) Close(); else Stop(); };
        Closing+=(_,e)=> { if(cancellation is not null) { e.Cancel=true; Stop(); } };
        UpdateButtons();
    }
    static SolidColorBrush Brush(string value)=>(SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    void Stop() { cancellation?.Cancel(); progress.Text="Останавливаю конвертацию… Готовые файлы сохранены."; }
    void Add(IEnumerable<string> paths)
    {
        foreach(string path in paths)
        {
            string full=Path.GetFullPath(path);
            if(BatchConversion.Supports(full) && !files.Any(f=>f.Path.Equals(full,StringComparison.OrdinalIgnoreCase))) files.Add(new Row(full));
        }
        progress.Text=$"Выбрано файлов: {files.Count}"; UpdateButtons();
    }
    void UpdateButtons()
    {
        bool idle=cancellation is null;
        add.IsEnabled=opened.IsEnabled=chooseDestination.IsEnabled=language.IsEnabled=idle;
        resetDestination.IsEnabled=idle && outputDirectory is not null;
        remove.IsEnabled=idle && list.SelectedItems.Count>0;
        apply.IsEnabled=idle && files.Count>0; close.Content=idle ? "Закрыть" : "Остановить";
    }
    async Task RunAsync()
    {
        if(cancellation is not null) return;
        using var cancel=new CancellationTokenSource(); cancellation=cancel; UpdateButtons();
        using var converter=new BatchConversion();
        string? destination=outputDirectory;
        string ocrLanguage=((ComboBoxItem)language.SelectedItem).Tag.ToString()!;
        var batch=files.ToArray(); foreach(var row in batch) row.Status="Ожидает конвертации";
        int done=0,failed=0,skipped=0;
        Row? active=null;
        try
        {
            for(int i=0;i<batch.Length;i++)
            {
                if(cancel.IsCancellationRequested) break;
                var row=batch[i]; active=row;
                string label=$"Файл {i+1} из {batch.Length}";
                progress.Text=label+" · "+Path.GetFileName(row.Path); row.Status="Конвертация…";
                var details=new Progress<string>(message=> { if(cancellation==cancel && !cancel.IsCancellationRequested && active==row) { row.Status=message; progress.Text=label+" · "+message; } });
                try
                {
                    if(protect(row.Path) is { } reason) { row.Status="Пропущен: "+reason; skipped++; continue; }
                    using var result=await converter.PrepareAsync(row.Path,destination,ocrLanguage,cancel.Token,details);
                    cancel.Token.ThrowIfCancellationRequested();
                    if(protect(row.Path) is { } changed) { row.Status="Пропущен: "+changed; skipped++; continue; }
                    string output=result.Commit(cancel.Token);
                    done++; SavedCount++; row.Status="Готово · "+output;
                }
                catch(OperationCanceledException) when(cancel.IsCancellationRequested) { row.Status="Отменено · результат не сохранён"; break; }
                catch(Exception error) { row.Status="Ошибка: "+error.Message; failed++; }
                finally { active=null; }
            }
            if(cancel.IsCancellationRequested) foreach(var row in batch.Where(r=>r.Status=="Ожидает конвертации")) row.Status="Не обработан · остановлено";
            progress.Text=$"{(cancel.IsCancellationRequested ? "Остановлено" : "Готово")}. Сохранено: {done}; пропущено: {skipped}; ошибок: {failed}.";
        }
        finally { active=null; cancellation=null; UpdateButtons(); }
    }
}
