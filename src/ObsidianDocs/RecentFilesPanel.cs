using System.Windows;
using System.Windows.Controls;

namespace ObsidianDocs;

public partial class MainWindow
{
    readonly RecentFileHistory recentFileHistory=new();
    IReadOnlyList<RecentFileEntry> recentFiles=[];
    int recentFilesLoadGeneration;
    bool changingRecentHistory;

    void UpdateLeftPanelWidth()=>AiChatColumn.Width=new GridLength(
        AiChatPanel.Visibility==Visibility.Visible || RecentFilesPanel.Visibility==Visibility.Visible ? 330 : 0);

    void SetRecentFilesVisible(bool visible)
    {
        RecentFilesPanel.Visibility=visible ? Visibility.Visible : Visibility.Collapsed;
        if(visible) AiChatPanel.Visibility=Visibility.Collapsed;
        UpdateLeftPanelWidth();
    }

    async void Documents_Click(object sender,RoutedEventArgs e)
    {
        HideCard();
        bool visible=RecentFilesPanel.Visibility!=Visibility.Visible;
        SetRecentFilesVisible(visible);
        if(visible) { FilterRecentFiles(); await RefreshRecentFilesAsync(); }
    }
    void CloseRecentFiles_Click(object sender,RoutedEventArgs e)=>SetRecentFilesVisible(false);
    async void RefreshRecentFiles_Click(object sender,RoutedEventArgs e)=>await RefreshRecentFilesAsync();
    void RecentFilesSearch_Changed(object sender,TextChangedEventArgs e)=>FilterRecentFiles();

    void FilterRecentFiles()
    {
        if(RecentFilesList is null || RecentFilesSearch is null) return;
        string query=RecentFilesSearch.Text.Trim();
        var filtered=recentFiles.Where(entry=>entry.Name.Contains(query,StringComparison.OrdinalIgnoreCase)
            || entry.FilePath.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
        RecentFilesList.ItemsSource=filtered;
        RecentFilesEmpty.Visibility=filtered.Length==0 ? Visibility.Visible : Visibility.Collapsed;
        RecentFilesEmpty.Text=recentFiles.Count==0
            ? "История пока пуста. Открытые файлы появятся здесь и сохранятся после закрытия программы."
            : "По этому запросу ничего не найдено.";
        RecentFilesCount.Text=$"Файлов в истории: {recentFiles.Count}";
    }

    async Task RefreshRecentFilesAsync()
    {
        int generation=++recentFilesLoadGeneration;
        RecentFilesStatus.Text="Загружаю историю…";
        try
        {
            var files=await recentFileHistory.ReadAsync();
            if(generation!=recentFilesLoadGeneration || !IsLoaded) return;
            recentFiles=files;
            if(RecentFilesPanel.Visibility==Visibility.Visible) { FilterRecentFiles(); RecentFilesStatus.Text=""; }
        }
        catch(Exception error)
        {
            if(generation==recentFilesLoadGeneration && IsLoaded)
            {
                RecentFilesStatus.Text="Не удалось загрузить историю: "+error.Message;
                if(recentFiles.Count==0) RecentFilesEmpty.Text="Не удалось прочитать сохранённую историю.";
            }
        }
    }

    async Task RememberRecentFileAsync(string path)
    {
        try
        {
            await recentFileHistory.RememberAsync(path);
            if(RecentFilesPanel.Visibility==Visibility.Visible && IsLoaded) await RefreshRecentFilesAsync();
        }
        catch(Exception error)
        {
            // A history write must never turn a successfully opened/saved
            // document into an "opening failed" error.
            RecentFilesStatus.Text="История не сохранена: "+error.Message;
        }
    }

    async void OpenRecentFile_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not FrameworkElement { Tag:string path }) return;
        if(busy || openingCancellation is not null) { RecentFilesStatus.Text="Дождитесь завершения текущего действия."; return; }
        await OpenPathsAsync([path]);
    }

    async void RemoveRecentFile_Click(object sender,RoutedEventArgs e)
    {
        if(changingRecentHistory || sender is not FrameworkElement { Tag:string path }) return;
        await ChangeRecentHistoryAsync(()=>recentFileHistory.RemoveAsync(path));
    }
    async void ClearRecentFiles_Click(object sender,RoutedEventArgs e)
    {
        if(!changingRecentHistory) await ChangeRecentHistoryAsync(recentFileHistory.ClearAsync);
    }
    async Task ChangeRecentHistoryAsync(Func<Task> change)
    {
        changingRecentHistory=true; ClearRecentFilesButton.IsEnabled=false;
        try { await change(); await RefreshRecentFilesAsync(); }
        catch(Exception error) { RecentFilesStatus.Text="Не удалось изменить историю: "+error.Message; }
        finally { changingRecentHistory=false; ClearRecentFilesButton.IsEnabled=true; }
    }
}
