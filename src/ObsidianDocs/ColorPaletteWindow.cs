using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path=System.IO.Path;

namespace ObsidianDocs;

internal sealed class ColorPaletteWindow : Window
{
    readonly Grid square=new() { Height=190,Focusable=true,ClipToBounds=true };
    readonly Canvas markerLayer=new() { IsHitTestVisible=false };
    readonly Ellipse marker=new() { Width=12,Height=12,Stroke=Brushes.White,StrokeThickness=2,Fill=Brushes.Transparent };
    readonly Border colorPreview=new() { Width=48,Height=36,CornerRadius=new CornerRadius(5),BorderBrush=Brushes.Gray,BorderThickness=new Thickness(1) };
    readonly Slider hue=new() { Minimum=0,Maximum=360,SmallChange=1,LargeChange=15,IsMoveToPointEnabled=true,Height=26 };
    readonly TextBox hex=new() { MaxLength=7,Width=105 };
    readonly TextBox[] rgb=[new() { Width=65,MaxLength=3 },new() { Width=65,MaxLength=3 },new() { Width=65,MaxLength=3 }];
    readonly TextBlock message=new() { Foreground=new SolidColorBrush(Color.FromRgb(197,175,224)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,8,3,0) };
    readonly Button apply=new() { Content="Применить",IsDefault=true,Background=new SolidColorBrush(Color.FromRgb(105,81,142)) };
    Color color;
    double h,s,v;
    bool updating,picking;
    public Color? SelectedColor { get; private set; }

    public ColorPaletteWindow(Window owner,Color initial,bool highlight,bool startWithEyedropper=false)
    {
        Owner=owner; Resources=owner.Resources; Title=highlight ? "Цвет маркера" : "Цвет текста";
        Width=510; Height=Math.Min(650,SystemParameters.WorkArea.Height-30); MinHeight=390;
        WindowStartupLocation=WindowStartupLocation.CenterOwner; WindowStyle=WindowStyle.None;
        ResizeMode=ResizeMode.NoResize; AllowsTransparency=true; Background=Brushes.Transparent;
        Foreground=new SolidColorBrush(Color.FromRgb(232,229,240)); ShowInTaskbar=false;
        var border=new Border { Background=Brush("#25252D"),BorderBrush=Brush("#786096"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(18) };
        Content=border;
        var layout=new Grid(); border.Child=layout;
        layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        var header=new DockPanel { Margin=new Thickness(0,0,0,10),Background=Brushes.Transparent };
        layout.Children.Add(header);
        var close=new Button { Content="×",IsCancel=true,Padding=new Thickness(8,3,8,3) };
        close.Click+=(_,_)=>Close(); DockPanel.SetDock(close,Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text=Title,FontSize=20,VerticalAlignment=VerticalAlignment.Center });
        header.MouseLeftButtonDown+=(_,e)=> { if(e.OriginalSource is TextBlock || e.OriginalSource==header) DragMove(); };
        var body=new StackPanel();
        var scroll=new ScrollViewer { Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Padding=new Thickness(0,0,4,0) };
        Grid.SetRow(scroll,1); layout.Children.Add(scroll);
        body.Children.Add(new TextBlock { Text="Насыщенность и яркость",Foreground=Brush("#B9B0C7"),Margin=new Thickness(0,0,0,7) });
        AutomationProperties.SetName(square,"Насыщенность и яркость цвета");
        square.ToolTip="Выберите цвет мышью. Стрелки меняют насыщенность и яркость, Shift — крупнее.";
        square.Children.Add(new Rectangle { Fill=new LinearGradientBrush(Colors.White,Colors.Transparent,0),IsHitTestVisible=false });
        square.Children.Add(new Rectangle { Fill=new LinearGradientBrush(Colors.Transparent,Colors.Black,90),IsHitTestVisible=false });
        markerLayer.Children.Add(marker); square.Children.Add(markerLayer); body.Children.Add(square);
        square.SizeChanged+=(_,_)=>PlaceMarker();
        void Choose(MouseEventArgs e)
        {
            var p=e.GetPosition(square);
            s=Math.Clamp(p.X/Math.Max(1,square.ActualWidth),0,1);
            v=1-Math.Clamp(p.Y/Math.Max(1,square.ActualHeight),0,1);
            SetColor(FromHsv(h,s,v),false);
        }
        square.MouseLeftButtonDown+=(_,e)=> { square.Focus(); square.CaptureMouse(); Choose(e); e.Handled=true; };
        square.MouseMove+=(_,e)=> { if(square.IsMouseCaptured && e.LeftButton==MouseButtonState.Pressed) Choose(e); };
        square.MouseLeftButtonUp+=(_,e)=> { if(square.IsMouseCaptured) { Choose(e); square.ReleaseMouseCapture(); e.Handled=true; } };
        square.KeyDown+=(_,e)=>
        {
            double step=Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .05 : .01;
            if(e.Key==Key.Left) s-=step; else if(e.Key==Key.Right) s+=step;
            else if(e.Key==Key.Up) v+=step; else if(e.Key==Key.Down) v-=step; else return;
            s=Math.Clamp(s,0,1); v=Math.Clamp(v,0,1); SetColor(FromHsv(h,s,v),false); e.Handled=true;
        };
        var spectrum=new LinearGradientBrush { StartPoint=new Point(0,.5),EndPoint=new Point(1,.5) };
        for(int i=0;i<=6;i++) spectrum.GradientStops.Add(new GradientStop(FromHsv(i*60,1,1),i/6d));
        hue.Background=spectrum; hue.Margin=new Thickness(0,10,0,12);
        hue.Template=(ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
             <Grid><Border Height="12" CornerRadius="6" Background="{TemplateBinding Background}" IsHitTestVisible="False"/>
              <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{Binding Value,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}">
               <Track.DecreaseRepeatButton><RepeatButton Command="{x:Static Slider.DecreaseLarge}"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
               <Track.Thumb><Thumb Width="12" Height="24"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="#38313F" BorderBrush="White" BorderThickness="2" CornerRadius="4"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
               <Track.IncreaseRepeatButton><RepeatButton Command="{x:Static Slider.IncreaseLarge}"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
              </Track>
             </Grid>
            </ControlTemplate>
            """);
        AutomationProperties.SetName(hue,"Цветовой тон"); body.Children.Add(hue);
        hue.ValueChanged+=(_,_)=> { if(!updating) { h=hue.Value; SetColor(FromHsv(h,s,v),false); } };
        var fields=new WrapPanel { Margin=new Thickness(0,0,0,10) }; body.Children.Add(fields);
        fields.Children.Add(colorPreview);
        void Field(string label,TextBox box)
        {
            var group=new StackPanel { Margin=new Thickness(9,0,0,0) };
            group.Children.Add(new TextBlock { Text=label,FontSize=11,Foreground=Brush("#B9B0C7") });
            box.Padding=new Thickness(6); box.Height=31; AutomationProperties.SetName(box,label); group.Children.Add(box); fields.Children.Add(group);
        }
        Field("HEX",hex); Field("R",rgb[0]); Field("G",rgb[1]); Field("B",rgb[2]);
        hex.TextChanged+=(_,_)=>
        {
            if(updating) return;
            if(TryHex(hex.Text,out var parsed)) SetColor(parsed,true,hex);
            else Invalid("HEX: введите #RGB или #RRGGBB.");
        };
        foreach(var box in rgb) box.TextChanged+=(_,_)=>
        {
            if(updating) return;
            var values=new byte[3];
            for(int i=0;i<3;i++) if(!byte.TryParse(rgb[i].Text,NumberStyles.None,CultureInfo.InvariantCulture,out values[i])) { Invalid("R, G и B: целые числа от 0 до 255."); return; }
            SetColor(Color.FromRgb(values[0],values[1],values[2]),true,box);
        };
        var eye=new Button { Content="⌖ Пипетка · взять цвет с экрана",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,2,0,10) };
        eye.Click+=async (_,_)=>await PickFromScreenAsync(); body.Children.Add(eye);
        body.Children.Add(new TextBlock { Text="Основные цвета",Foreground=Brush("#B9B0C7"),Margin=new Thickness(0,2,0,5) });
        var basics=new WrapPanel(); body.Children.Add(basics);
        foreach(var value in new[] { "#000000","#FFFFFF","#808080","#C0C0C0","#FF0000","#FF8000","#FFFF00","#80FF00","#00FF00","#00FF80","#00FFFF","#0080FF","#0000FF","#8000FF","#FF00FF","#FF0080" })
            AddSwatch(basics,value);
        var recent=ReadRecent();
        if(recent.Length>0)
        {
            body.Children.Add(new TextBlock { Text="Последние цвета",Foreground=Brush("#B9B0C7"),Margin=new Thickness(0,10,0,5) });
            var colors=new WrapPanel(); body.Children.Add(colors); foreach(var value in recent) AddSwatch(colors,value);
        }
        var footer=new StackPanel(); Grid.SetRow(footer,2); layout.Children.Add(footer); footer.Children.Add(message);
        var buttons=new WrapPanel { HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0) }; footer.Children.Add(buttons);
        var clear=new Button { Content="Без цвета" }; clear.Click+=(_,_)=> { SelectedColor=null; DialogResult=true; }; buttons.Children.Add(clear);
        var cancel=new Button { Content="Отмена",IsCancel=true }; cancel.Click+=(_,_)=>Close(); buttons.Children.Add(cancel);
        apply.Click+=(_,_)=> { SelectedColor=color; SaveRecent(Hex(color)); DialogResult=true; }; buttons.Children.Add(apply);
        SetColor(initial);
        if(startWithEyedropper)
        {
            EventHandler? start=null;
            start=async (_,_)=> { ContentRendered-=start; await PickFromScreenAsync(); };
            ContentRendered+=start;
        }
    }

    void AddSwatch(Panel panel,string value)
    {
        if(!TryHex(value,out var c)) return;
        var swatch=new Button { Width=24,Height=24,Margin=new Thickness(1),Padding=new Thickness(0),ToolTip=value,Content=new Border { Width=22,Height=22,Background=new SolidColorBrush(c),BorderBrush=Brush("#91869E"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4) } };
        AutomationProperties.SetName(swatch,"Цвет "+value); swatch.Click+=(_,_)=>SetColor(c); panel.Children.Add(swatch);
    }
    void Invalid(string text) { message.Text=text; apply.IsEnabled=false; }
    void SetColor(Color next,bool updateHsv=true,TextBox? editing=null)
    {
        color=next;
        if(updateHsv) (h,s,v)=ToHsv(next,h);
        updating=true;
        try
        {
            square.Background=new SolidColorBrush(FromHsv(h,1,1)); hue.Value=h;
            colorPreview.Background=new SolidColorBrush(next);
            if(editing!=hex) hex.Text=Hex(next);
            byte[] values=[next.R,next.G,next.B];
            for(int i=0;i<3;i++) if(editing!=rgb[i]) rgb[i].Text=values[i].ToString(CultureInfo.InvariantCulture);
            apply.IsEnabled=true; message.Text=""; PlaceMarker();
        }
        finally { updating=false; }
    }
    void PlaceMarker() { Canvas.SetLeft(marker,s*square.ActualWidth-6); Canvas.SetTop(marker,(1-v)*square.ActualHeight-6); }
    async Task PickFromScreenAsync()
    {
        if(picking) return;
        picking=true; Opacity=0;
        try
        {
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);
            await Task.Delay(80);
            if(!IsLoaded || !IsActive) return;
            var picker=new ScreenColorPicker(this);
            if(picker.ShowDialog()==true && picker.SelectedColor is { } sampled) SetColor(sampled);
            else if(picker.ErrorMessage is { } error) message.Text="Не удалось взять цвет: "+error;
        }
        catch(Exception error) { if(IsLoaded) message.Text="Не удалось взять цвет: "+error.Message; }
        finally { picking=false; if(IsLoaded) Opacity=1; }
    }
    internal static string Hex(Color c)=>$"#{c.R:X2}{c.G:X2}{c.B:X2}";
    internal static bool TryHex(string value,out Color color)
    {
        value=value.Trim(); if(value.StartsWith('#')) value=value[1..]; color=Colors.Black;
        if(value.Length==3) value=string.Concat(value.Select(c=>new string(c,2)));
        if(value.Length!=6 || !uint.TryParse(value,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint n)) return false;
        color=Color.FromRgb((byte)(n>>16),(byte)(n>>8),(byte)n); return true;
    }
    static SolidColorBrush Brush(string value)=>(SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    static Color FromHsv(double hue,double saturation,double value)
    {
        double c=value*saturation,x=c*(1-Math.Abs((hue/60)%2-1)),m=value-c;
        (double r,double g,double b)=(hue%360) switch
        { <60=>(c,x,0d),<120=>(x,c,0d),<180=>(0d,c,x),<240=>(0d,x,c),<300=>(x,0d,c),_=>(c,0d,x) };
        return Color.FromRgb((byte)Math.Round((r+m)*255),(byte)Math.Round((g+m)*255),(byte)Math.Round((b+m)*255));
    }
    static (double H,double S,double V) ToHsv(Color c,double previousHue)
    {
        double r=c.R/255d,g=c.G/255d,b=c.B/255d,max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),delta=max-min;
        double hue=delta==0 ? previousHue : max==r ? 60*((g-b)/delta%6) : max==g ? 60*((b-r)/delta+2) : 60*((r-g)/delta+4);
        return ((hue+360)%360,max==0 ? 0 : delta/max,max);
    }
    static readonly string recentFile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ObsidianDocs","color-palette.json");
    static string[] ReadRecent()
    {
        try { return (JsonSerializer.Deserialize<string[]>(File.ReadAllText(recentFile)) ?? []).Where(value=>value is not null && TryHex(value,out _)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray(); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    static void SaveRecent(string value)
    {
        string temporary=recentFile+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            var values=new[] { value }.Concat(ReadRecent()).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(recentFile)!);
            File.WriteAllText(temporary,JsonSerializer.Serialize(values)); File.Move(temporary,recentFile,true);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) { }
        finally { try { if(File.Exists(temporary)) File.Delete(temporary); } catch(IOException) { } catch(UnauthorizedAccessException) { } }
    }
}
