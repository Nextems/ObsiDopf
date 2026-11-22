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

internal sealed class PdfAuthorBatchWindow : Window
{
    sealed class Row(string path) : INotifyPropertyChanged
    {
        public string Path { get; }=path;
        string status="Готов к обработке";
        public string Status { get=>status; set { status=value; PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Display))); } }
        public string Display=>System.IO.Path.GetFileName(Path)+"\n"+Path+"\n"+Status;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    readonly ObservableCollection<Row> files=[];
    readonly ListBox list=new() { SelectionMode=SelectionMode.Extended };
    readonly ComboBox author=new() { IsEditable=true,IsTextSearchEnabled=false,MinWidth=240 };
    readonly CheckBox copies=new() { Content="Создавать копии",IsChecked=true,Margin=new Thickness(3,10,0,5),Foreground=Brushes.White };
    readonly TextBox destinationPath=new() { IsReadOnly=true,Text="Рядом с исходными PDF",VerticalContentAlignment=VerticalAlignment.Center,ToolTip="Папка для готовых PDF" };
    readonly Button chooseDestination=new() { Content="Выбрать папку…" },resetDestination=new() { Content="Рядом с PDF" };
    readonly DockPanel destinationRow=new() { Margin=new Thickness(3,2,3,8) };
    string? outputDirectory;
    readonly TextBlock description=new() { Foreground=Brush("#B9B0C7"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,0,3,10) };
    readonly TextBlock progress=new() { Text="Выберите PDF и укажите нового автора всех комментариев.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,10,3,5) };
    readonly Button add=new() { Content="＋ Выбрать PDF…" },opened=new() { Content="Добавить открытые PDF" },remove=new() { Content="Убрать выбранные" };
    readonly Button apply=new() { Content="Переименовать авторов",Background=Brush("#69518E") },close=new() { Content="Закрыть" };
    readonly Func<IEnumerable<string>> openFiles;
    readonly Func<string,string?> protect;
    CancellationTokenSource? cancellation;
    public HashSet<string> UpdatedOriginals { get; }=new(StringComparer.OrdinalIgnoreCase);
    public PdfAuthorBatchWindow(Window owner,AuthorProfiles profiles,Func<IEnumerable<string>> openFiles,Func<string,string?> protect)
    {
        Owner=owner; Resources=owner.Resources; this.openFiles=openFiles; this.protect=protect;
        Title="Глобальные действия · Автор комментариев PDF"; Width=840; Height=680; MinWidth=650; MinHeight=500;
        WindowStartupLocation=WindowStartupLocation.CenterOwner; ShowInTaskbar=false;
        Background=Brush("#25252D"); Foreground=Brush("#E8E5F0"); SourceInitialized+=(_,_)=>WindowTheme.Apply(this);
        var root=new Grid { Margin=new Thickness(20) }; Content=root;
        root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        var header=new StackPanel(); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text="Изменить автора комментариев",FontSize=21,Margin=new Thickness(3,0,3,12) });
        var buttons=new WrapPanel(); buttons.Children.Add(add); buttons.Children.Add(opened); buttons.Children.Add(remove); header.Children.Add(buttons);
        var field=new DockPanel { Margin=new Thickness(3,12,3,0) };
        field.Children.Add(new TextBlock { Text="Новый автор",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,14,0) });
        author.ItemsSource=profiles.Names.ToArray(); author.Text=profiles.Selected; field.Children.Add(author); header.Children.Add(field);
        AutomationProperties.SetName(author,"Новый автор всех комментариев");
        header.Children.Add(copies);
        header.Children.Add(new TextBlock { Text="Папка результатов",Margin=new Thickness(3,5,3,3),Foreground=Brush("#B9B0C7") });
        DockPanel.SetDock(chooseDestination,Dock.Right); DockPanel.SetDock(resetDestination,Dock.Right);
        destinationRow.Children.Add(chooseDestination); destinationRow.Children.Add(resetDestination); destinationRow.Children.Add(destinationPath);
        header.Children.Add(destinationRow); header.Children.Add(description); Explain();
        AutomationProperties.SetName(destinationPath,"Папка результатов глобального действия");
        chooseDestination.Click+=(_,_)=>
        {
            var picker=new OpenFolderDialog { Title="Папка для результатов глобального действия",Multiselect=false };
            if(outputDirectory is not null) picker.InitialDirectory=outputDirectory;
            if(picker.ShowDialog(this)==true) { outputDirectory=picker.FolderName; destinationPath.Text=outputDirectory; destinationPath.ToolTip=outputDirectory; Explain(); UpdateButtons(); }
        };
        resetDestination.Click+=(_,_)=> { outputDirectory=null; destinationPath.Text="Рядом с исходными PDF"; destinationPath.ToolTip=destinationPath.Text; Explain(); UpdateButtons(); };
        copies.Checked+=(_,_)=> { Explain(); UpdateButtons(); }; copies.Unchecked+=(_,_)=> { Explain(); UpdateButtons(); };
        var text=new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty,new Binding(nameof(Row.Display))); text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);
        list.ItemTemplate=new DataTemplate { VisualTree=text }; list.ItemsSource=files;
        list.Background=Brush("#1E1E24"); list.BorderBrush=Brush("#45414F"); list.BorderThickness=new Thickness(1);
        AutomationProperties.SetName(list,"Выбранные PDF и результаты обработки"); Grid.SetRow(list,1); root.Children.Add(list);
        var footer=new StackPanel(); Grid.SetRow(footer,2); root.Children.Add(footer); footer.Children.Add(progress);
        var actions=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right }; actions.Children.Add(close); actions.Children.Add(apply); footer.Children.Add(actions);
        add.Click+=(_,_)=> { var picker=new OpenFileDialog { Filter="PDF|*.pdf",Multiselect=true,Title="PDF для смены авторов комментариев" }; if(picker.ShowDialog(this)==true) Add(picker.FileNames); };
        opened.Click+=(_,_)=>Add(openFiles());
        remove.Click+=(_,_)=> { foreach(var row in list.SelectedItems.Cast<Row>().ToArray()) files.Remove(row); UpdateButtons(); };
        list.SelectionChanged+=(_,_)=>UpdateButtons(); author.AddHandler(TextBox.TextChangedEvent,new TextChangedEventHandler((_,_)=>UpdateButtons()));
        apply.Click+=async (_,_)=>await RunAsync();
        close.Click+=(_,_)=> { if(cancellation is null) Close(); else cancellation.Cancel(); };
        Closing+=(_,e)=> { if(cancellation is not null) { e.Cancel=true; cancellation.Cancel(); progress.Text="Останавливаю обработку… Уже обработанные файлы сохранены."; } };
        UpdateButtons();
    }
    static SolidColorBrush Brush(string value)=>(SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    void Explain()=>description.Text=copies.IsChecked==true
        ? "Меняется только автор, не текст комментариев. "+(outputDirectory is null ? "Копии создаются рядом с оригиналами. " : "Копии сохраняются в выбранную папку. ")+"Существующие файлы не перезаписываются. Открытые PDF с несохранёнными правками сначала сохраните."
        : "Автор изменится в исходных PDF. Файлы с несохранёнными правками в программе будут пропущены — сначала сохраните их.";
    void Add(IEnumerable<string> paths)
    {
        foreach(var path in paths)
        {
            string full=Path.GetFullPath(path);
            if(Path.GetExtension(full).Equals(".pdf",StringComparison.OrdinalIgnoreCase) && !files.Any(f=>f.Path.Equals(full,StringComparison.OrdinalIgnoreCase))) files.Add(new Row(full));
        }
        progress.Text=$"Выбрано PDF: {files.Count}"; UpdateButtons();
    }
    void UpdateButtons()
    {
        bool idle=cancellation is null;
        add.IsEnabled=opened.IsEnabled=author.IsEnabled=copies.IsEnabled=idle;
        destinationRow.IsEnabled=idle && copies.IsChecked==true;
        resetDestination.IsEnabled=outputDirectory is not null;
        remove.IsEnabled=idle && list.SelectedItems.Count>0;
        apply.IsEnabled=idle && files.Count>0 && !string.IsNullOrWhiteSpace(author.Text);
        close.Content=idle ? "Закрыть" : "Остановить";
    }
    async Task RunAsync()
    {
        if(cancellation is not null) return;
        string name;
        try { name=PdfAuthorBatch.Author(author.Text); } catch(ArgumentException error) { progress.Text=error.Message; return; }
        bool makeCopies=copies.IsChecked==true; int done=0,unchanged=0,failed=0,skipped=0,totalComments=0;
        string? destination=makeCopies ? outputDirectory : null;
        using var cancel=new CancellationTokenSource(); cancellation=cancel; UpdateButtons();
        var batch=files.ToArray(); foreach(var row in batch) row.Status="Ожидает обработки";
        try
        {
            for(int i=0;i<batch.Length;i++)
            {
                if(cancel.IsCancellationRequested) break;
                var row=batch[i]; progress.Text=$"Обработка {i+1} из {batch.Length}…"; row.Status="Обрабатываю…";
                try
                {
                    if(protect(row.Path) is { } reason) { row.Status="Пропущен: "+reason; skipped++; continue; }
                    using var prepared=await Task.Run(()=>PdfAuthorBatch.Prepare(row.Path,name,cancel.Token,destination),cancel.Token);
                    cancel.Token.ThrowIfCancellationRequested();
                    if(prepared.Changed==0 && (!makeCopies || !prepared.HasOutput)) { row.Status=prepared.Total==0 ? "Нет комментариев" : "У всех комментариев уже указан этот автор"; unchanged++; continue; }
                    if(protect(row.Path) is { } changed) { row.Status="Пропущен: "+changed; skipped++; continue; }
                    string output=prepared.Commit(makeCopies,cancel.Token)!;
                    if(!makeCopies) UpdatedOriginals.Add(row.Path);
                    done++; totalComments+=prepared.Changed;
                    if(prepared.Changed==0) { unchanged++; row.Status="Скопирован без изменений · "+output; }
                    else row.Status=$"Готово · комментариев: {prepared.Changed}"+(makeCopies ? " · "+output : " · исходный PDF обновлён");
                }
                catch(OperationCanceledException) when(cancel.IsCancellationRequested) { row.Status="Отменено · файл не изменён"; break; }
                catch(Exception error) { row.Status="Ошибка: "+error.Message; failed++; }
            }
            if(cancel.IsCancellationRequested) foreach(var row in batch.Where(r=>r.Status=="Ожидает обработки")) row.Status="Не обработан · остановлено";
            progress.Text=$"{(cancel.IsCancellationRequested ? "Остановлено" : "Готово")}. Сохранено PDF: {done}; изменено комментариев: {totalComments}; без смены автора: {unchanged}; пропущено: {skipped}; ошибок: {failed}.";
        }
        finally { cancellation=null; UpdateButtons(); }
    }
}
