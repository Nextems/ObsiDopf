using System.Windows;
using System.Windows.Input;
namespace ObsidianDocs;
public partial class MainWindow
{
    Task saveTask=Task.CompletedTask;
    void ConfigureSaveCommands()
    {
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save,async (_,e)=>
        {
            e.Handled=true;
            try { saveTask=SaveCurrentAsync(); await saveTask; } catch (Exception error) { Error(error); }
        },(_,e)=> { e.CanExecute=!busy && (current?.Pdf is not null || current?.IsWordDocument==true); e.Handled=true; }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save,new KeyGesture(Key.S,ModifierKeys.Control)));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.SaveAs,(_,e)=> { e.Handled=true; Save_Click(this,new RoutedEventArgs()); },(_,e)=> { e.CanExecute=!busy && (current?.Pdf is not null || current?.IsWordDocument==true); e.Handled=true; }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.SaveAs,new KeyGesture(Key.S,ModifierKeys.Control|ModifierKeys.Shift)));
    }
    async Task SaveCurrentAsync()
    {
        if (busy || current is null) return;
        if (current.IsWordDocument) { if(current.IsUntitled || current.IsDoc) await SaveAsCurrentAsync(); else await SaveDocxAsync(current.Path); return; }
        if (current.Pdf is null) return;
        // Commit the currently edited comment before writing the document.
        if (OcrCard.Visibility==Visibility.Visible && cardIsNote && (editingNote is not null || !string.IsNullOrWhiteSpace(OcrText.Text))) await ApplyCardAsync();
        if (current is null) return;
        if (current.IsWordDocument) { Save_Click(this,new RoutedEventArgs()); return; }
        current.SaveChanges();
        Status.Text="Сохранено: "+current.Path;
    }
}
