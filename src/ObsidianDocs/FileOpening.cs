using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace ObsidianDocs;
public partial class MainWindow
{
    CancellationTokenSource? openingCancellation;
    readonly Stopwatch openingWatch=new();
    DispatcherTimer? openingTimer;
    void ShowOpenProgress(string path)
    {
        LoadingTitle.Text=Status.Text;
        LoadingDetail.Text=System.IO.Path.GetExtension(path).Equals(".doc",StringComparison.OrdinalIgnoreCase)
            ? "При первом открытии DOC преобразуется в DOCX для редактирования. Повторное открытие использует кэш."
            : "Подготавливаю документ для отображения…";
        CancelOpeningButton.IsEnabled=true;
        LoadingElapsed.Text=""; LoadingOverlay.Visibility=Visibility.Visible;
        openingWatch.Restart();
        openingTimer ??=new DispatcherTimer(TimeSpan.FromSeconds(1),DispatcherPriority.Background,(_,_)=>LoadingElapsed.Text=$"Прошло: {openingWatch.Elapsed:m\\:ss}",Dispatcher);
        openingTimer.Start();
    }
    void HideOpenProgress()
    {
        openingTimer?.Stop(); openingWatch.Stop(); LoadingOverlay.Visibility=Visibility.Collapsed;
    }
    void CancelOpening_Click(object sender,RoutedEventArgs e)
    {
        if(openingCancellation is null) return;
        openingCancellation.Cancel(); CancelOpeningButton.IsEnabled=false;
        LoadingTitle.Text=Status.Text="Отменяю открытие…";
    }
}
