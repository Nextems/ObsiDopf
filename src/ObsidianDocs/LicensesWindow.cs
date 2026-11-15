using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ObsidianDocs;

internal sealed class LicensesWindow : Window
{
    sealed record Document(string Title,string? Path,string? Text=null);
    readonly List<Document> documents=[];
    readonly ListBox entries=new() { DisplayMemberPath=nameof(Document.Title) };
    readonly TextBox viewer=new()
    {
        IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,
        VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
        FontFamily=new FontFamily("Consolas"),FontSize=12,Padding=new Thickness(14)
    };
    readonly TextBlock status=new() { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0) };

    public LicensesWindow(Window owner)
    {
        Owner=owner; Title="ObsiDopf · Лицензии и исходники"; Width=1040; Height=780; MinWidth=800; MinHeight=560;
        Resources=owner.Resources; WindowStartupLocation=WindowStartupLocation.CenterOwner; ShowInTaskbar=false;
        Background=new SolidColorBrush(Color.FromRgb(37,37,45)); Foreground=new SolidColorBrush(Color.FromRgb(232,229,240));
        SourceInitialized+=(_,_)=>WindowTheme.Apply(this);
        var root=new Grid { Margin=new Thickness(20) }; Content=root;
        root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        var header=new StackPanel { Margin=new Thickness(0,0,0,14) }; root.Children.Add(header);
        string version=Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0";
        header.Children.Add(new TextBlock { Text=$"ObsiDopf {version}",FontSize=24,FontWeight=FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text="Copyright (c) 2026 Maxim_Dembitsky (Nextems)",Margin=new Thickness(0,5,0,8) });
        header.Children.Add(new TextBlock
        {
            Text="Собственный код — GNU AGPL, версия 3. Вы можете изменять и распространять его на условиях лицензии. " +
                "Программа предоставляется БЕЗ ГАРАНТИЙ, включая гарантии пригодности для конкретной цели. " +
                "Авторство и лицензии сторонних компонентов сохранены.",
            TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(185,176,204))
        });
        var body=new Grid(); Grid.SetRow(body,1); root.Children.Add(body);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(250) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var navigation=new DockPanel(); body.Children.Add(navigation);
        var search=new TextBox { Margin=new Thickness(0,0,0,8),ToolTip="Найти лицензию или компонент" };
        AutomationProperties.SetName(search,"Найти лицензию или компонент");
        DockPanel.SetDock(search,Dock.Top); navigation.Children.Add(search); navigation.Children.Add(entries);
        AutomationProperties.SetName(entries,"Лицензии и компоненты");
        AutomationProperties.SetName(viewer,"Текст выбранной лицензии");
        Grid.SetColumn(viewer,2); body.Children.Add(viewer);
        entries.SelectionChanged+=(_,_)=>ShowDocument();
        AddFile("ObsiDopf · AGPL-3.0",LocalFile("LICENSE"));
        AddFile("Авторство и условия",LocalFile("COPYRIGHT.md"));
        AddFile("SuperDoc 1.47.2 · AGPL",LocalFile("WordAssets/SUPERDOC-LICENSE.txt"));
        AddFile("SuperDoc · зависимости",LocalFile("WordAssets/THIRD-PARTY-NOTICES.txt"));
        AddFile("Исходники и сборка",LocalFile("Licensing/SOURCE-INFO.md"));
        AddFile("LibreOffice · условия",LocalFile("native/libreoffice/runtime/license.txt"));
        AddFile("LibreOffice · лицензии компонентов",LocalFile("native/libreoffice/runtime/LICENSE.html"));
        AddFile("LibreOffice · уведомления",LocalFile("native/libreoffice/runtime/NOTICE"));
        string notices=LocalFile("ThirdPartyNotices");
        if(Directory.Exists(notices))
        {
            try
            {
                foreach(var file in Directory.EnumerateFiles(notices,"*",SearchOption.AllDirectories).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase))
                {
                    string name=System.IO.Path.GetFileName(file);
                    if(name.Contains("LICENSE",StringComparison.OrdinalIgnoreCase) || name.Contains("LICENCE",StringComparison.OrdinalIgnoreCase)
                        || name.Contains("COPYING",StringComparison.OrdinalIgnoreCase) || name.Contains("NOTICE",StringComparison.OrdinalIgnoreCase)
                        || name.Contains("COPYRIGHT",StringComparison.OrdinalIgnoreCase))
                        AddFile(System.IO.Path.GetRelativePath(notices,file),file);
                }
            }
            catch(IOException) { documents.Add(new Document("Компоненты",null,"Не удалось прочитать каталог ThirdPartyNotices. Полные уведомления доступны в папке установленной программы.")); }
            catch(UnauthorizedAccessException) { documents.Add(new Document("Компоненты",null,"Нет доступа к каталогу ThirdPartyNotices.")); }
        }
        void Filter()
        {
            string query=search.Text.Trim();
            entries.ItemsSource=documents.Where(d=>d.Title.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
            entries.SelectedIndex=entries.Items.Count>0 ? 0 : -1;
            if(entries.SelectedIndex<0) viewer.Clear();
        }
        search.TextChanged+=(_,_)=>Filter(); Filter();
        PreviewKeyDown+=(_,e)=> { if(e.Key==Key.F && Keyboard.Modifiers==ModifierKeys.Control) { search.Focus(); search.SelectAll(); e.Handled=true; } };
        var footer=new StackPanel { Margin=new Thickness(0,12,0,0) }; Grid.SetRow(footer,2); root.Children.Add(footer);
        var buttons=new WrapPanel(); footer.Children.Add(buttons);
        Button Action(string label,Action action)
        {
            var button=new Button { Content=label };
            button.Click+=(_,_)=> { try { action(); status.Text=""; } catch(Exception error) { status.Text=error.Message; } };
            buttons.Children.Add(button); return button;
        }
        Action("Исходники ObsiDopf",()=>Open("https://github.com/Nextems/ObsiDopf"));
        Action("Исходники SuperDoc",()=>Open("https://github.com/superdoc/docx-editor/tree/66c22186c7c0735360ca4fd94f7499898415e903"));
        Action("Релизы и архивы",()=>Open("https://github.com/Nextems/ObsiDopf/releases"));
        Action("Копировать текст",()=> { if(viewer.Text.Length>0) Clipboard.SetText(viewer.Text); });
        Action("Закрыть",Close).IsCancel=true;
        footer.Children.Add(status);
    }

    void AddFile(string title,string path)=>documents.Add(new Document(title,path));
    static string LocalFile(string relative)
    {
        string path=System.IO.Path.Combine(AppContext.BaseDirectory,relative.Replace('/',System.IO.Path.DirectorySeparatorChar));
        return File.Exists(path) || Directory.Exists(path) ? path : InstalledComponents.Find(relative) ?? path;
    }
    void ShowDocument()
    {
        if(entries.SelectedItem is not Document document) return;
        try
        {
            viewer.Text=document.Text ?? (File.Exists(document.Path) ? File.ReadAllText(document.Path!)
                : "Этот текст отсутствует в данной сборке. Лицензии и сведения об исходниках доступны в репозитории проекта и полном установочном комплекте.");
            if(document.Path?.EndsWith(".html",StringComparison.OrdinalIgnoreCase)==true)
            {
                string html=viewer.Text;
                html=System.Text.RegularExpressions.Regex.Replace(html,"<(script|style)\\b[^>]*>.*?</\\1>","",System.Text.RegularExpressions.RegexOptions.Singleline|System.Text.RegularExpressions.RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
                html=System.Text.RegularExpressions.Regex.Replace(html,"</?(p|div|h[1-6]|br|li|pre|tr)\\b[^>]*>","\n",System.Text.RegularExpressions.RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
                viewer.Text=System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html,"<[^>]+>","",System.Text.RegularExpressions.RegexOptions.None,TimeSpan.FromSeconds(1)));
            }
            viewer.ScrollToHome();
        }
        catch(IOException error) { viewer.Text="Не удалось прочитать документ: "+error.Message; }
        catch(UnauthorizedAccessException error) { viewer.Text="Не удалось прочитать документ: "+error.Message; }
        catch(System.Text.RegularExpressions.RegexMatchTimeoutException) { viewer.Text="Лицензия в HTML слишком велика для отображения. Оригинальный файл доступен в каталоге native/libreoffice/runtime установленной программы."; }
    }
    static void Open(string url)=>Process.Start(new ProcessStartInfo(url) { UseShellExecute=true });
}

public partial class MainWindow
{
    void Licenses_Click(object sender,RoutedEventArgs e)=>new LicensesWindow(this).ShowDialog();
}
