using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
namespace ObsidianDocs;

internal sealed class PageSurface : Grid
{
    public int Index { get; }
    public double Top { get; set; }
    public double Ratio { get; }
    public Image Image { get; } = new() { Stretch=Stretch.Fill };
    public Canvas Selection { get; } = new() { Background=Brushes.Transparent };
    public Canvas Markers { get; } = new();
    public Canvas TextHighlights { get; } = new() { IsHitTestVisible=false };
    public List<TextWord>? Words { get; set; }
    public Rectangle Box { get; } = new() { Stroke=new SolidColorBrush(Color.FromRgb(182,147,245)),StrokeThickness=2,Fill=new SolidColorBrush(Color.FromArgb(64,111,76,155)),Visibility=Visibility.Collapsed };
    public PageSurface(int index,double ratio)
    {
        Index=index; Ratio=ratio;
        Margin=new Thickness(0,0,0,24); HorizontalAlignment=HorizontalAlignment.Center;
        Background=Brushes.White; ClipToBounds=true;
        Children.Add(Image); Children.Add(Selection); Children.Add(TextHighlights); Children.Add(Markers); Selection.Children.Add(Box);
        Selection.Tag=this;
    }
    public void Size(double width)
    {
        Width=Image.Width=Selection.Width=Markers.Width=TextHighlights.Width=width;
        Height=Image.Height=Selection.Height=Markers.Height=TextHighlights.Height=width*Ratio;
    }
}
