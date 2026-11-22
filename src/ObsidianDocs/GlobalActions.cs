using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace ObsidianDocs;

public partial class MainWindow
{
    void GlobalActions_Click(object sender,RoutedEventArgs e)
    {
        if(busy) return;
        var menu=new ContextMenu { Background=Brush("#2C2935"),Foreground=Brush("#E8E5F0"),PlacementTarget=GlobalActionsButton,Placement=System.Windows.Controls.Primitives.PlacementMode.Right };
        var rename=new MenuItem { Header="Изменить авторов комментариев в PDF…",Background=menu.Background,Foreground=menu.Foreground };
        // A flat action needs no reserved checkbox/icon column.
        menu.Template=(ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ContextMenu">
              <Border Background="{TemplateBinding Background}" BorderBrush="#51475F" BorderThickness="1" CornerRadius="5" Padding="3"><ItemsPresenter/></Border>
            </ControlTemplate>
            """);
        rename.Template=(ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="MenuItem">
              <Border x:Name="Item" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="{TemplateBinding Background}" CornerRadius="3" Padding="12,8">
                <ContentPresenter ContentSource="Header" RecognizesAccessKey="True"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Item" Property="Background" Value="#514369"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Item" Property="Opacity" Value=".4"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        rename.Click+=async (_,_)=> { try { await BatchAuthorsAsync(); } catch(Exception error) { Error(error); } }; menu.Items.Add(rename);
        var convert=new MenuItem { Header="Конвертировать файлы PDF ⇄ DOCX…",Background=menu.Background,Foreground=menu.Foreground,Template=rename.Template };
        convert.Click+=(_,_)=> { try { BatchConvert(); } catch(Exception error) { Error(error); } }; menu.Items.Add(convert); menu.IsOpen=true;
    }
    void BatchConvert()
    {
        if(busy) return;
        var dialog=new BatchConversionWindow(this,
            ()=>sessions.Where(s=>!s.IsUntitled && (s.CanEditPdf || s.IsWordDocument) && File.Exists(s.Path)).Select(s=>s.Path),
            ProtectBatchConversion,((ComboBoxItem)OcrLanguage.SelectedItem).Tag.ToString()!);
        dialog.ShowDialog();
        if(dialog.SavedCount>0) Status.Text=$"Глобальная конвертация · сохранено файлов: {dialog.SavedCount}";
    }
    string? ProtectBatchConversion(string path)
    {
        var opened=sessions.Where(s=>SamePdfPath(s.Path,path)).ToArray();
        if(opened.Any(s=>s.Dirty) || (opened.Length>0 && !saveTask.IsCompleted)) return "в открытом документе есть несохранённые правки";
        return ProtectBatchOriginal(path);
    }
    static bool SamePdfPath(string left,string right)=>Path.GetFullPath(left).Equals(Path.GetFullPath(right),StringComparison.OrdinalIgnoreCase);
    string? ProtectBatchOriginal(string path)
    {
        var opened=sessions.Where(s=>s.CanEditPdf && SamePdfPath(s.Path,path)).ToArray();
        if(opened.Any(s=>s.Dirty) || (opened.Length>0 && !saveTask.IsCompleted)) return "в открытом PDF есть несохранённые правки";
        if(current is not null && opened.Contains(current) && OcrCard.Visibility==Visibility.Visible && cardIsNote
            && (editingNote is not null || !string.IsNullOrWhiteSpace(OcrText.Text))) return "в открытом PDF редактируется комментарий";
        return null;
    }
    async Task BatchAuthorsAsync()
    {
        if(busy) return;
        var dialog=new PdfAuthorBatchWindow(this,authors,()=>sessions.Where(s=>s.CanEditPdf && File.Exists(s.Path)).Select(s=>s.Path),ProtectBatchOriginal);
        dialog.ShowDialog();
        if(dialog.UpdatedOriginals.Count==0) return;
        SetBusy(true,"Обновляю открытые PDF…");
        var warnings=new List<string>();
        try
        {
            foreach(var session in sessions.Where(s=>s.CanEditPdf && dialog.UpdatedOriginals.Any(p=>SamePdfPath(p,s.Path))).ToArray())
            {
                if(session.Dirty) { warnings.Add(session.Name+": в документе появились несохранённые правки"); continue; }
                try
                {
                    using var reloaded=await Task.Run(()=>DocumentSession.Open(session.Path));
                    if(!session.Dirty && sessions.Contains(session)) session.ReloadPdfAfterBatch(reloaded);
                    else warnings.Add(session.Name+": файл изменён на диске, в открытой вкладке есть правки");
                }
                catch(Exception error) { warnings.Add(session.Name+": "+error.Message); }
            }
            if(current?.CanEditPdf==true && dialog.UpdatedOriginals.Any(p=>SamePdfPath(p,current.Path)) && !current.Dirty) { HideCard(); await RenderAsync(); }
            Status.Text=warnings.Count==0 ? "Авторы комментариев обновлены · открытые PDF перечитаны" : string.Join("; ",warnings);
        }
        finally { SetBusy(false,Status.Text); }
    }
}
