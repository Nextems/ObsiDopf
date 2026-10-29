using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
namespace ObsidianDocs;
internal static class CommentIcon
{
    internal static Button CreateButton(string tooltip)
    {
        const string shape="M 10,3 C 5.7,3 3,6.2 3,10.3 C 3,14.5 5.7,17 9.9,17 L 17.7,17 L 15.8,14.1 C 16.7,13 17,11.6 17,10.2 C 17,6.2 14.2,3 10,3 Z";
        var icon=new Grid { Width=22,Height=22,IsHitTestVisible=false };
        icon.Children.Add(new Path { Data=Geometry.Parse(shape),Fill=new SolidColorBrush(Color.FromRgb(226,203,255)),Stroke=Brushes.White,StrokeThickness=3.5,StrokeLineJoin=PenLineJoin.Round,Effect=new DropShadowEffect { Color=Colors.Black,BlurRadius=2,ShadowDepth=1,Opacity=.28 } });
        icon.Children.Add(new Path { Data=Geometry.Parse(shape),Fill=Brushes.Transparent,Stroke=new SolidColorBrush(Color.FromRgb(163,78,255)),StrokeThickness=1.25,StrokeLineJoin=PenLineJoin.Round });
        var button=new Button { Width=24,Height=24,Padding=new Thickness(0),Margin=new Thickness(0),Background=Brushes.Transparent,Content=icon,ToolTip=tooltip,Cursor=System.Windows.Input.Cursors.Hand,Focusable=true };
        // Use only the bubble as the visible hit target, without a square
        // background supplied by the application's standard button template.
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
        var border=new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty,Brushes.Transparent); border.AppendChild(presenter);
        button.Template=new ControlTemplate(typeof(Button)) { VisualTree=border };
        System.Windows.Automation.AutomationProperties.SetName(button,tooltip);
        return button;
    }
}
