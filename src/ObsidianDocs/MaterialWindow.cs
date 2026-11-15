using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace ObsidianDocs;

internal sealed class MaterialPage
{
    public required string Label { get; init; }
    public required BitmapSource Image { get; set; }
    public List<MaterialMark> Marks { get; set; }=[];
    public Stack<(BitmapSource Image,List<MaterialMark> Marks)> Undo { get; }=[];
    public MaterialPage Copy()=>new() { Label=Label,Image=Image,Marks=Marks.Select(m=>m.Copy()).ToList() };
    public void Remember()
    {
        Undo.Push((Image,Marks.Select(m=>m.Copy()).ToList()));
        if(Undo.Count>12) { var keep=Undo.Take(12).Reverse().ToList(); Undo.Clear(); foreach(var item in keep) Undo.Push(item); }
    }
}
internal sealed record MaterialMark(bool Brush,List<Point> Points)
{
    public MaterialMark Copy()=>new(Brush,Points.ToList());
}
internal sealed class MaterialChoice : INotifyPropertyChanged
{
    public required int Page { get; init; }
    public string Label=>$"Страница {Page+1}";
    public BitmapSource? Thumbnail { get; set; }
    public bool Loading { get; set; }
    public required Func<MaterialChoice,Task> Load { get; init; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()=>PropertyChanged?.Invoke(this,new(nameof(Thumbnail)));
}
internal sealed class MaterialThumbnail : Image
{
    public MaterialThumbnail()
    {
        Loaded+=async (_,_)=>await Ensure();
        DataContextChanged+=async (_,_)=>await Ensure();
    }
    async Task Ensure() { if(IsLoaded && DataContext is MaterialChoice choice && choice.Thumbnail is null && !choice.Loading) await choice.Load(choice); }
}

internal sealed class MaterialWindow : Window
{
    public const int MaximumPages=12;
    readonly DocumentSession? source;
    readonly CancellationTokenSource lifetime=new();
    readonly SemaphoreSlim thumbnailGate=new(1);
    readonly Queue<MaterialChoice> thumbnails=[];
    readonly List<MaterialPage> pages;
    readonly ListBox picker=new() { SelectionMode=SelectionMode.Multiple };
    readonly ListBox selected=new() { DisplayMemberPath=nameof(MaterialPage.Label),MinWidth=160 };
    readonly TabControl tabs=new();
    readonly TextBlock status=new() { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8),Foreground=Brushes.LightSteelBlue };
    readonly Image image=new() { Stretch=Stretch.Fill };
    readonly Canvas overlay=new() { Background=Brushes.Transparent,ClipToBounds=true };
    readonly Grid paper=new();
    readonly Viewbox view=new() { Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top };
    readonly ScrollViewer canvasScroll=new() { VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto };
    readonly ComboBox tools=new() { ItemsSource=new[] { "Красный пунктир", "Красная кисть", "Обрезать область" },SelectedIndex=0,Width=190 };
    readonly Button next=new() { Content="Редактировать выбранные →" };
    readonly Button attach=new() { Content="Прикрепить к чату",Background=new SolidColorBrush(Color.FromRgb(105,81,142)) };
    Shape? draft;
    Point start;
    bool drawing,closed,loading;
    MaterialPage? Current=>selected.SelectedItem as MaterialPage;
    public List<MaterialPage> Result { get; private set; }=[];
    public MaterialWindow(Window owner,DocumentSession? source,IEnumerable<MaterialPage> existing)
    {
        this.source=source; pages=existing.Select(p=>p.Copy()).ToList();
        Owner=owner; Resources=owner.Resources; Title="Материал для Gemini · выбор и разметка";
        Width=1050; Height=820; MinWidth=760; MinHeight=580;
        Background=new SolidColorBrush(Color.FromRgb(30,30,36)); Foreground=Brushes.White;
        tabs.Background=Brush("#25252D"); tabs.Foreground=Brush("#E8E5F0");
        tabs.BorderBrush=Brush("#403948"); tabs.Padding=new Thickness(10);
        tabs.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        tabs.VerticalContentAlignment=VerticalAlignment.Stretch;
        // The standard TabControl content border otherwise uses a white theme
        // surface while our inherited tab labels already use light text.
        tabs.Template=(ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TabControl">
             <Grid KeyboardNavigation.TabNavigation="Local">
              <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
              <TabPanel IsItemsHost="True" Background="#1E1E24" Panel.ZIndex="1"/>
              <Border Grid.Row="1" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" Padding="{TemplateBinding Padding}">
               <ContentPresenter x:Name="PART_SelectedContentHost" ContentSource="SelectedContent" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
              </Border>
             </Grid>
            </ControlTemplate>
            """);
        picker.Background=Brush("#1E1E24"); picker.Foreground=Brush("#E8E5F0");
        picker.BorderThickness=new Thickness(0);
        picker.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled);
        picker.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,ScrollBarVisibility.Auto);
        picker.SetValue(ScrollViewer.CanContentScrollProperty,true);
        picker.ItemContainerStyle=PageCardStyle();
        selected.Background=Brush("#25252D"); selected.Foreground=Brush("#E8E5F0");
        canvasScroll.Background=Brush("#1E1E24");
        paper.Background=Brushes.White; paper.Visibility=Visibility.Collapsed;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SourceInitialized+=(_,_)=>WindowTheme.Apply(this);
        var root=new DockPanel { Margin=new Thickness(12) }; Content=root;
        var actions=new WrapPanel(); DockPanel.SetDock(actions,Dock.Top); root.Children.Add(actions);
        void Button(string label,RoutedEventHandler action) { var b=new Button { Content=label }; b.Click+=action; actions.Children.Add(b); }
        Button("＋ Фотографии",AddPhotos); actions.Children.Add(next); actions.Children.Add(attach);
        var cancel=new Button { Content="Отмена",IsCancel=true }; cancel.Click+=(_,_)=>Close(); actions.Children.Add(cancel);
        DockPanel.SetDock(status,Dock.Bottom); root.Children.Add(status); root.Children.Add(tabs);
        var choose=new TabItem { Header="1 · Выбрать страницы",HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch }; tabs.Items.Add(choose);
        var choosePanel=new DockPanel { Background=Brush("#25252D") }; choose.Content=choosePanel;
        var hint=new TextBlock { Text="Отмечайте страницы кликом (без Ctrl). Можно выбрать до 12 страниц или добавить фотографии. Затем перейдите в редактор.",Foreground=Brush("#C0B6D1"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,4,4,12) };
        DockPanel.SetDock(hint,Dock.Top); choosePanel.Children.Add(hint); choosePanel.Children.Add(picker);
        VirtualizingPanel.SetIsVirtualizing(picker,true); VirtualizingPanel.SetVirtualizationMode(picker,VirtualizationMode.Recycling);
        var template=new DataTemplate(typeof(MaterialChoice));
        var panel=new FrameworkElementFactory(typeof(StackPanel));
        var label=new FrameworkElementFactory(typeof(CheckBox));
        label.SetBinding(ContentControl.ContentProperty,new Binding(nameof(MaterialChoice.Label)));
        label.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,new Binding(nameof(ListBoxItem.IsSelected))
        { RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(ListBoxItem),1),Mode=BindingMode.OneWay });
        label.SetValue(UIElement.IsHitTestVisibleProperty,false); label.SetValue(UIElement.FocusableProperty,false);
        label.SetValue(Control.ForegroundProperty,Brush("#E8E5F0")); label.SetValue(Control.FontSizeProperty,14d);
        label.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,0,10)); panel.AppendChild(label);
        var sheet=new FrameworkElementFactory(typeof(Border));
        sheet.SetValue(Border.BackgroundProperty,Brushes.White); sheet.SetValue(Border.BorderBrushProperty,Brush("#61596C"));
        sheet.SetValue(Border.BorderThicknessProperty,new Thickness(1)); sheet.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        var preview=new FrameworkElementFactory(typeof(MaterialThumbnail)); preview.SetValue(FrameworkElement.HeightProperty,260d); preview.SetValue(FrameworkElement.WidthProperty,190d);
        preview.SetValue(Image.StretchProperty,Stretch.Uniform); preview.SetBinding(Image.SourceProperty,new Binding(nameof(MaterialChoice.Thumbnail)));
        sheet.AppendChild(preview); panel.AppendChild(sheet);
        template.VisualTree=panel; picker.ItemTemplate=template;
        if(source is not null) picker.ItemsSource=Enumerable.Range(0,source.Count).Select(i=>new MaterialChoice { Page=i,Load=LoadThumbnail }).ToList();
        else hint.Text="Добавьте фотографии. Для страниц PDF откройте документ в основном окне и нажмите «Выбрать материал».";
        picker.SelectionChanged+=(_,e)=>
        {
            if(picker.SelectedItems.Count+pages.Count>MaximumPages)
            {
                foreach(var item in e.AddedItems.Cast<object>().ToList()) picker.SelectedItems.Remove(item);
                status.Text="За один раз можно приложить до 12 изображений.";
            }
            else status.Text=$"Выбрано страниц: {picker.SelectedItems.Count} · в материале: {pages.Count}";
        };
        var edit=new TabItem { Header="2 · Разметить материал",HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch }; tabs.Items.Add(edit);
        var editRoot=new DockPanel { Background=Brush("#25252D") }; edit.Content=editRoot;
        var toolbar=new WrapPanel(); DockPanel.SetDock(toolbar,Dock.Top); editRoot.Children.Add(toolbar); toolbar.Children.Add(tools);
        void EditButton(string text,Action action) { var b=new Button { Content=text }; b.Click+=(_,_)=>action(); toolbar.Children.Add(b); }
        EditButton("Отменить штрих",Undo); EditButton("Очистить разметку",()=> { if(Current is { } p) { p.Remember(); p.Marks.Clear(); Redraw(); } });
        EditButton("Убрать страницу",()=> { if(Current is { } p) { pages.Remove(p); RefreshPages(); } });
        var grid=new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(180) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); editRoot.Children.Add(grid);
        grid.Children.Add(selected); Grid.SetColumn(canvasScroll,1); grid.Children.Add(canvasScroll);
        paper.Children.Add(image); paper.Children.Add(overlay); view.Child=paper; canvasScroll.Content=view;
        canvasScroll.SizeChanged+=(_,_)=>view.Width=Math.Max(100,canvasScroll.ActualWidth-35);
        selected.SelectionChanged+=(_,_)=>Redraw();
        overlay.MouseLeftButtonDown+=DrawStart; overlay.MouseMove+=DrawMove; overlay.MouseLeftButtonUp+=DrawEnd;
        overlay.LostMouseCapture+=(_,_)=> { if(drawing) { drawing=false; draft=null; Redraw(); } };
        next.Click+=LoadSelected;
        attach.Click+=(_,_)=>Attach();
        tabs.SelectionChanged+=(_,e)=>
        {
            if(e.Source==tabs && tabs.SelectedIndex==1 && picker.SelectedItems.Count>0 && !loading) LoadSelected(this,new RoutedEventArgs());
        };
        Closed+=(_,_)=> { closed=true; lifetime.Cancel(); lifetime.Dispose(); };
        RefreshPages(); if(pages.Count>0) tabs.SelectedIndex=1;
    }
    static SolidColorBrush Brush(string color)=>(SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    static Style PageCardStyle()
    {
        var style=new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty,Brush("#E8E5F0")));
        style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(12)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(6,5,6,9)));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty,240d));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.TemplateProperty,(ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBoxItem">
             <Border x:Name="Card" Background="#2C2935" BorderBrush="#45404F" BorderThickness="1" CornerRadius="8" Padding="{TemplateBinding Padding}">
              <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"/>
             </Border>
             <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Card" Property="Background" Value="#383141"/></Trigger>
              <Trigger Property="IsSelected" Value="True"><Setter TargetName="Card" Property="Background" Value="#40314F"/><Setter TargetName="Card" Property="BorderBrush" Value="#B99AEF"/></Trigger>
              <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Card" Property="BorderBrush" Value="#D0BBEF"/></Trigger>
             </ControlTemplate.Triggers>
            </ControlTemplate>
            """)));
        return style;
    }
    async Task LoadThumbnail(MaterialChoice choice)
    {
        if(source is null || choice.Loading || closed) return;
        choice.Loading=true;
        try
        {
            await thumbnailGate.WaitAsync(lifetime.Token);
            try
            {
                if(closed) return;
                choice.Thumbnail=await Task.Run(()=>source.Render(choice.Page,48));
                if(closed) return;
                choice.Refresh(); thumbnails.Enqueue(choice);
                while(thumbnails.Count>64) { var old=thumbnails.Dequeue(); old.Thumbnail=null; old.Refresh(); }
            }
            finally { thumbnailGate.Release(); }
        }
        catch(OperationCanceledException) { }
        catch(Exception error) { if(!closed) status.Text="Миниатюра: "+error.Message; }
        finally { choice.Loading=false; }
    }
    async void LoadSelected(object sender,RoutedEventArgs e)
    {
        if(source is null || loading) return;
        var chosen=picker.SelectedItems.Cast<MaterialChoice>().OrderBy(p=>p.Page).ToList();
        if(chosen.Count+pages.Count>MaximumPages) { status.Text="Не больше 12 изображений."; return; }
        loading=true; next.IsEnabled=attach.IsEnabled=false;
        try
        {
            var added=new List<MaterialPage>();
            foreach(var choice in chosen)
            {
                status.Text=$"Подготавливаю страницу {choice.Page+1}…";
                if(closed) return;
                var bitmap=await Task.Run(()=>source.Render(choice.Page,160));
                if(closed) return;
                added.Add(new MaterialPage { Label=choice.Label,Image=FitImage(bitmap) });
            }
            pages.AddRange(added); picker.SelectedItems.Clear(); RefreshPages(); tabs.SelectedIndex=1;
        }
        catch(Exception error) { status.Text=error.Message; }
        finally { loading=false; next.IsEnabled=attach.IsEnabled=true; }
    }
    async void AddPhotos(object sender,RoutedEventArgs e)
    {
        if(loading) return;
        var dialog=new OpenFileDialog { Filter="Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",Multiselect=true };
        if(dialog.ShowDialog(this)!=true) return;
        if(pages.Count+dialog.FileNames.Length>MaximumPages) { status.Text="Не больше 12 изображений."; return; }
        loading=true; attach.IsEnabled=next.IsEnabled=false;
        try
        {
            foreach(string path in dialog.FileNames)
            {
                var bitmap=await Task.Run(()=>
                {
                    if(new FileInfo(path).Length>64L*1024*1024) throw new InvalidOperationException("Файл изображения больше 64 МБ.");
                    using var input=File.OpenRead(path);
                    var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);
                    int width=decoder.Frames[0].PixelWidth,height=decoder.Frames[0].PixelHeight;
                    int target=Math.Max(1,(int)(width*Math.Min(1,2200d/Math.Max(width,height))));
                    input.Position=0;
                    var photo=new BitmapImage(); photo.BeginInit(); photo.CacheOption=BitmapCacheOption.OnLoad; photo.DecodePixelWidth=target; photo.StreamSource=input; photo.EndInit(); photo.Freeze(); return photo;
                });
                if(closed) return;
                pages.Add(new MaterialPage { Label=System.IO.Path.GetFileName(path),Image=bitmap });
            }
            RefreshPages(); tabs.SelectedIndex=1;
        }
        catch(Exception error) { status.Text=error.Message; RefreshPages(); }
        finally { loading=false; attach.IsEnabled=next.IsEnabled=true; }
    }
    static BitmapSource FitImage(BitmapSource source)
    {
        double scale=Math.Min(1,2200d/Math.Max(source.PixelWidth,source.PixelHeight));
        if(scale==1) return source;
        var result=new TransformedBitmap(source,new ScaleTransform(scale,scale)); result.Freeze(); return result;
    }
    void RefreshPages()
    {
        int index=selected.SelectedIndex; selected.ItemsSource=null; selected.ItemsSource=pages;
        selected.SelectedIndex=pages.Count>0 ? Math.Clamp(index,0,pages.Count-1) : -1; Redraw();
        status.Text=$"Материал: {pages.Count} изображений. Рисуйте поверх копий; исходный документ не меняется.";
    }
    void Redraw()
    {
        overlay.Children.Clear(); image.Source=Current?.Image;
        if(Current is not { } p) { paper.Visibility=Visibility.Collapsed; return; }
        paper.Visibility=Visibility.Visible;
        paper.Width=overlay.Width=p.Image.PixelWidth; paper.Height=overlay.Height=p.Image.PixelHeight;
        foreach(var mark in p.Marks) overlay.Children.Add(MakeShape(mark,p.Image.PixelWidth));
    }
    static Shape MakeShape(MaterialMark mark,double width)
    {
        double thickness=Math.Max(3,width/420);
        if(mark.Brush) return new Polyline { Points=new PointCollection(mark.Points),Stroke=Brushes.Red,StrokeThickness=thickness,StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round };
        var rect=new Rect(mark.Points[0],mark.Points[^1]);
        var shape=new Rectangle { Width=rect.Width,Height=rect.Height,Stroke=Brushes.Red,StrokeThickness=thickness,StrokeDashArray=new DoubleCollection { 5,3 },Fill=Brushes.Transparent };
        Canvas.SetLeft(shape,rect.Left); Canvas.SetTop(shape,rect.Top); return shape;
    }
    Point Position(MouseEventArgs e)=>new(Math.Clamp(e.GetPosition(overlay).X,0,overlay.Width),Math.Clamp(e.GetPosition(overlay).Y,0,overlay.Height));
    void DrawStart(object sender,MouseButtonEventArgs e)
    {
        if(Current is null) return;
        start=Position(e); drawing=true; overlay.CaptureMouse();
        draft=tools.SelectedIndex==1 ? new Polyline { Stroke=Brushes.Red,StrokeThickness=Math.Max(3,overlay.Width/420),Points=new PointCollection { start },StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round }
            : new Rectangle { Stroke=tools.SelectedIndex==2 ? Brushes.MediumPurple : Brushes.Red,StrokeThickness=Math.Max(3,overlay.Width/420),StrokeDashArray=new DoubleCollection { 5,3 },Fill=Brushes.Transparent };
        overlay.Children.Add(draft); e.Handled=true;
    }
    void DrawMove(object sender,MouseEventArgs e)
    {
        if(!drawing || draft is null) return;
        var point=Position(e);
        if(draft is Polyline line) { if((point-line.Points[^1]).Length>2) line.Points.Add(point); }
        else { var rect=new Rect(start,point); draft.Width=rect.Width; draft.Height=rect.Height; Canvas.SetLeft(draft,rect.Left); Canvas.SetTop(draft,rect.Top); }
    }
    void DrawEnd(object sender,MouseButtonEventArgs e)
    {
        if(!drawing || draft is null || Current is not { } p) return;
        var end=Position(e); drawing=false; overlay.ReleaseMouseCapture();
        if(tools.SelectedIndex==2)
        {
            var rect=new Rect(start,end);
            int x=(int)rect.X,y=(int)rect.Y,w=(int)rect.Width,h=(int)rect.Height;
            if(w>5 && h>5)
            {
                p.Remember(); var crop=new CroppedBitmap(p.Image,new Int32Rect(x,y,w,h)); crop.Freeze(); p.Image=crop;
                p.Marks=p.Marks.Select(m=>new MaterialMark(m.Brush,m.Points.Select(point=>new Point(point.X-x,point.Y-y)).ToList())).ToList();
            }
        }
        else if(draft is Polyline line)
        {
            if((end-line.Points[^1]).Length>1) line.Points.Add(end);
            if(line.Points.Count>1) { p.Remember(); p.Marks.Add(new(true,line.Points.ToList())); }
        }
        else if((end-start).Length>5) { p.Remember(); p.Marks.Add(new(false,[start,end])); }
        draft=null; Redraw(); e.Handled=true;
    }
    void Undo() { if(Current is { } p && p.Undo.TryPop(out var state)) { p.Image=state.Image; p.Marks=state.Marks; Redraw(); } }
    void Attach()
    {
        if(loading) return;
        if(picker.SelectedItems.Count>0) { status.Text="Сначала нажмите «Редактировать выбранные», чтобы подготовить отмеченные страницы."; return; }
        if(pages.Count==0) { status.Text="Выберите хотя бы одну страницу или фотографию."; return; }
        Result=pages.Select(p=>p.Copy()).ToList(); DialogResult=true;
    }
    public static GeminiClient.ImageInput Encode(MaterialPage page)
    {
        var visual=new DrawingVisual();
        using(var drawing=visual.RenderOpen())
        {
            drawing.PushClip(new RectangleGeometry(new Rect(0,0,page.Image.PixelWidth,page.Image.PixelHeight)));
            drawing.DrawRectangle(Brushes.White,null,new Rect(0,0,page.Image.PixelWidth,page.Image.PixelHeight));
            drawing.DrawImage(page.Image,new Rect(0,0,page.Image.PixelWidth,page.Image.PixelHeight));
            var pen=new Pen(Brushes.Red,Math.Max(3,page.Image.PixelWidth/420d)) { StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round };
            foreach(var mark in page.Marks)
            {
                if(mark.Brush) for(int i=1;i<mark.Points.Count;i++) drawing.DrawLine(pen,mark.Points[i-1],mark.Points[i]);
                else drawing.DrawRectangle(null,new Pen(Brushes.Red,pen.Thickness) { DashStyle=new DashStyle([5d,3d],0) },new Rect(mark.Points[0],mark.Points[^1]));
            }
            drawing.Pop();
        }
        var result=new RenderTargetBitmap(page.Image.PixelWidth,page.Image.PixelHeight,96,96,PixelFormats.Pbgra32); result.Render(visual); result.Freeze();
        var encoder=new JpegBitmapEncoder { QualityLevel=88 }; encoder.Frames.Add(BitmapFrame.Create(result));
        using var output=new MemoryStream(); encoder.Save(output);
        return new(page.Label,output.ToArray());
    }
}
