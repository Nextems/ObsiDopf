using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace ObsidianDocs;

internal sealed class DarkDialog : Window
{
    MessageBoxResult result = MessageBoxResult.Cancel;
    internal DarkDialog(Window owner, string text, string title, MessageBoxButton buttons)
    {
        Owner = owner; Title = title; Width = 500; SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(300,SystemParameters.WorkArea.Height-80); ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = Color("#25252D"); Foreground = Color("#E8E5F0");
        SourceInitialized += (_,_) => WindowTheme.Apply(this);
        var panel = new DockPanel { Margin = new Thickness(22) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,20,0,0) };
        DockPanel.SetDock(actions,Dock.Bottom); panel.Children.Add(actions);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21 }});
        void Add(string label, MessageBoxResult value, bool primary, bool cancel)
        {
            var button = new Button { Content = label, MinWidth = 85, Margin = new Thickness(6,0,0,0), IsDefault = primary, IsCancel = cancel };
            if (owner.TryFindResource(typeof(Button)) is Style style) button.Style = style;
            button.Background = Color(primary ? "#69518E" : "#36333E"); button.Foreground = Foreground;
            button.Click += (_,_) => { result = value; DialogResult = true; };
            actions.Children.Add(button);
        }
        if (buttons == MessageBoxButton.YesNo) { Add("Да",MessageBoxResult.Yes,false,false); Add("Нет",MessageBoxResult.No,true,true); result=MessageBoxResult.No; }
        else { Add("OK",MessageBoxResult.OK,true,false); if (buttons == MessageBoxButton.OKCancel) Add("Отмена",MessageBoxResult.Cancel,false,true); }
        Content = panel;
    }
    static SolidColorBrush Color(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    public static MessageBoxResult Show(Window owner,string text,string title,MessageBoxButton buttons = MessageBoxButton.OK,MessageBoxImage icon = MessageBoxImage.None)
    {
        var dialog = new DarkDialog(owner,text,title,buttons); dialog.ShowDialog(); return dialog.result;
    }
}
