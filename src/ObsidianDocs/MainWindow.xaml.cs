using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Drawing;

namespace ObsidianDocs;

public partial class MainWindow : Window
{
    readonly List<DocumentSession> sessions = [];
    readonly OcrService ocr = new();
    DocumentSession? current;
    BitmapSource? bitmap;
    Point start;
    Rect selection;
    double zoom = 1;

    bool dragging, busy, refreshing, noteMode, cardIsNote;
    PdfAnnotation? editingNote;
    public MainWindow()
    {
        InitializeComponent();
        ConfigureSaveCommands();
        ConfigureDocumentCommands();
        ConfigureTextSearch();
        ConfigureAiChat();
        ConfigurePageWindow();
        ConfigureTabReordering();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        AuthorPicker.ItemsSource = authors.Names; AuthorPicker.SelectedItem = authors.Selected;
        Closing += OnClosing; OcrLanguage.SelectionChanged += (_, _) => HideCard();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SetTool(); if(openingCancellation is not null) CancelOpening_Click(this,new RoutedEventArgs()); HideCard(); } if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) Open_Click(this, new RoutedEventArgs()); };
    }
    void Error(Exception error) => DarkDialog.Show(this, error.Message, "Не удалось выполнить действие", MessageBoxButton.OK, MessageBoxImage.Warning);
    void SetBusy(bool value, string message)
    {
        busy = value; Tabs.IsEnabled = !value; OcrButton.IsEnabled = !value; NoteButton.IsEnabled = !value;
        CardAction.IsEnabled = !value; RetryButton.IsEnabled = !value; Status.Text = message;
        TextNoteButton.IsEnabled=!value; DeleteCommentButton.IsEnabled=!value;
        DocxHost.IsEnabled=DocxTools.IsEnabled=!value;
        RecentFilesList.IsEnabled=!value;
    }
    async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        HideCard();
        var dialog = new OpenFileDialog { Filter = "Документы и изображения|*.pdf;*.docx;*.doc;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        await OpenPathsAsync(dialog.FileNames);
    }
    internal async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        if(busy || openingCancellation is not null) return;
        HideCard();
        using var cancellation=new CancellationTokenSource();
        openingCancellation=cancellation;
        var progress=new Progress<string>(message=>
        {
            if(ReferenceEquals(openingCancellation,cancellation) && !cancellation.IsCancellationRequested && LoadingOverlay.Visibility==Visibility.Visible)
                LoadingTitle.Text=Status.Text=message;
        });
        try
        {
            foreach(var inputPath in paths)
            {
                DocumentSession? loaded=null;
                string result="Готово";
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var path=Path.GetFullPath(inputPath);
                    var existing=sessions.FirstOrDefault(s=>s.Path.Equals(path,StringComparison.OrdinalIgnoreCase));
                    if(existing is not null) { Tabs.SelectedIndex=sessions.IndexOf(existing); await RememberRecentFileAsync(existing.Path); continue; }
                    SetBusy(true,"Открываю документ…"); ShowOpenProgress(path);
                    loaded=await Task.Run(()=>DocumentSession.Open(path,cancellation.Token,progress),cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    HideOpenProgress();
                    AddSessionTab(loaded); loaded=null;
                    Status.Text="Отображаю документ…";
                    await RenderAsync();
                    await RememberRecentFileAsync(path);
                    result=current?.IsDoc==true ? "DOC → DOCX · редактирование · Ctrl+S — сохранить как DOCX" : current?.IsWordDocument==true ? "DOCX · Ctrl+S — сохранить · Ctrl+Z — отменить" : "Готово · OCR: выделите область мышью";
                }
                catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { result="Открытие отменено"; break; }
                catch(Exception error) { result="Не удалось открыть документ"; HideOpenProgress(); Error(error); }
                finally { loaded?.Dispose(); HideOpenProgress(); SetBusy(false,result); }
            }
        }
        finally { openingCancellation=null; HideOpenProgress(); }
    }
    void CloseSession(DocumentSession session, TabItem tab)
    {
        if (busy) return;
        if (session.Dirty && DarkDialog.Show(this, "Закрыть без сохранения изменений?", "Несохранённые изменения", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        sessions.Remove(session); Tabs.Items.Remove(tab); session.Dispose();
        if (sessions.Count == 0) { current = null; ConfigureDocumentUi(); ChangeSearchDocument(); bitmap = null; viewGeneration++; surfaces.Clear(); PageStack.Children.Clear(); PageStack.Visibility = Visibility.Collapsed; CommentList.Children.Clear(); Welcome.Visibility = Visibility.Visible; SideList.Items.Clear(); PageStatus.Text = ""; OcrCard.Visibility = Visibility.Collapsed; }
    }
    async void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if(reorderingTabs) return;
        if (Tabs.SelectedItem is not TabItem { Tag: DocumentSession session } || current == session) return;
        current = session;
        if (!busy) { try { SetBusy(true, "Загружаю страницу…"); await RenderAsync(); } catch (Exception error) { Error(error); } finally { SetBusy(false, "Готово"); } }
    }
    async void Prev_Click(object s, RoutedEventArgs e) { try { await NavigateAsync((current?.Page ?? 0)-1); } catch (Exception error) { Error(error); } }
    async void Next_Click(object s, RoutedEventArgs e) { try { await NavigateAsync((current?.Page ?? 0)+1); } catch (Exception error) { Error(error); } }
    void ZoomOut_Click(object s, RoutedEventArgs e)=>ChangeZoom(zoom-.1);
    void ZoomIn_Click(object s, RoutedEventArgs e)=>ChangeZoom(zoom+.1);
    void OcrMode_Click(object s,RoutedEventArgs e) { if(busy) return; bool enable=!ocrMode; SetTool(ocr:enable); Status.Text=enable ? "Выделите область для OCR" : "OCR отключён · выделите текст и нажмите Ctrl+C"; }
    void NoteMode_Click(object s,RoutedEventArgs e) { if(busy) return; bool enable=!noteMode; SetTool(note:enable); Status.Text=enable ? "Щёлкните по странице для комментария" : "Комментарии отключены · выделите текст и нажмите Ctrl+C"; }
    static SolidColorBrush Brush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, PageImage.Width), Math.Clamp(p.Y, 0, PageImage.Height));
    void Select_Start(object s, MouseButtonEventArgs e)
    {
        if (busy || bitmap is null || current?.IsWordDocument==true) return;
        if (s is not Canvas { Tag: PageSurface surface } || surface.Image.Source is not BitmapSource source) return;
        HideCard(); activeSurface=surface; bitmap=source; current!.Page=surface.Index; UpdateCurrentPageUi();
        start = Clamp(e.GetPosition(SelectionCanvas)); editingNote = null;
        if(pdfTextMode) { e.Handled=true; AddPdfTextAtAsync(new Point(start.X/surface.Width,start.Y/surface.Height)); return; }
        if (textNoteMode || (!ocrMode && !noteMode))
        {
            if (textNoteMode && current?.CanEditPdf!=true) { Status.Text="Комментарии к тексту доступны в PDF. Сначала экспортируйте документ в PDF."; return; }
            Viewer.Focus(); dragging=true; SelectionCanvas.CaptureMouse();
            wordsTask=LoadTextWordsAsync(surface);
            return;
        }
        if (noteMode)
        {
            if (current?.CanEditPdf!=true) { Status.Text = "Комментарии доступны в PDF. Сначала экспортируйте документ в PDF."; return; }
            selection = new Rect(start.X, start.Y, 1, 1); ShowCard(selection, true); OcrText.Text = ""; OcrText.Focus(); return;
        }
        dragging = true; SelectionCanvas.CaptureMouse(); SelectionBox.Visibility = Visibility.Visible; UpdateSelection(start);
    }
    void Select_Move(object s, MouseEventArgs e)
    {
        if (!dragging) return;
        var end=Clamp(e.GetPosition(SelectionCanvas));
        if (textNoteMode || !ocrMode) { if (wordsTask.IsCompletedSuccessfully) UpdateTextSelection(end); }
        else UpdateSelection(end);
    }
    void UpdateSelection(Point end)
    {
        selection = new Rect(start, end); Canvas.SetLeft(SelectionBox, selection.X); Canvas.SetTop(SelectionBox, selection.Y); SelectionBox.Width = selection.Width; SelectionBox.Height = selection.Height;
    }
    async void Select_End(object s, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false; SelectionCanvas.ReleaseMouseCapture();
        var end=Clamp(e.GetPosition(SelectionCanvas));
        if (textNoteMode || !ocrMode) { await FinishTextSelectionAsync(end,textNoteMode); return; }
        UpdateSelection(end);
        if (selection.Width < 6 || selection.Height < 6) { SelectionBox.Visibility = Visibility.Collapsed; return; }
        ShowCard(selection, false); await RunOcrAsync();
    }
    void ShowCard(Rect rect, bool note)
    {
        cardIsNote = note; CardTitle.Text = note ? "Комментарий PDF" : "Распознанный текст";
        CardAction.Content = note ? "Сохранить" : "Копировать всё"; RetryButton.Visibility = note ? Visibility.Collapsed : Visibility.Visible;
        DeleteCommentButton.Visibility=note && editingNote is not null ? Visibility.Visible : Visibility.Collapsed;
        CardStatus.Text = note ? "Комментарий сохранится вместе с PDF." : "Результат можно исправить и скопировать.";
        OcrCard.Visibility = Visibility.Visible; PositionCard(rect);
    }
    void PositionCard(Rect rect)
    {
        if (OcrCard.Visibility != Visibility.Visible || PageHost.Visibility != Visibility.Visible) return;
        var anchor = SelectionCanvas.TranslatePoint(new Point(rect.Right, rect.Top), Workspace);
        double x = anchor.X + 14;
        if (x + 340 > Workspace.ActualWidth - 12) x = anchor.X - rect.Width - 354;
        OcrCard.Width = Math.Min(340, Math.Max(200, Workspace.ActualWidth - 24));
        Canvas.SetLeft(OcrCard, Math.Clamp(x, 12, Math.Max(12, Workspace.ActualWidth-OcrCard.Width-12)));
        Canvas.SetTop(OcrCard, Math.Clamp(anchor.Y, 12, Math.Max(12, Workspace.ActualHeight-365)));
    }
    async Task RunOcrAsync()
    {
        if (bitmap is null || busy) return;
        try
        {
            var x = Math.Clamp((int)(selection.X / PageImage.Width * bitmap.PixelWidth), 0, bitmap.PixelWidth-1);
            var y = Math.Clamp((int)(selection.Y / PageImage.Height * bitmap.PixelHeight), 0, bitmap.PixelHeight-1);
            var w = Math.Clamp((int)(selection.Width / PageImage.Width * bitmap.PixelWidth), 1, bitmap.PixelWidth-x);
            var h = Math.Clamp((int)(selection.Height / PageImage.Height * bitmap.PixelHeight), 1, bitmap.PixelHeight-y);
            var crop = new CroppedBitmap(bitmap, new Int32Rect(x,y,w,h)); crop.Freeze();
            SetBusy(true, "Распознаю текст локально…"); CardStatus.Text = "Распознавание… При первом запуске загружаются модели."; OcrText.Text = "";
            string language = ((ComboBoxItem)OcrLanguage.SelectedItem).Tag.ToString()!;
            var result = await ocr.RecognizeAsync(crop, language);
            OcrText.Text = result.Text;
            CardStatus.Text = result.Text.Length == 0 ? "Текст не найден. Попробуйте другую область или язык." : "Готово · можно исправить текст и скопировать";
        }
        catch (Exception error) { CardStatus.Text = "Ошибка: " + error.Message; }
        finally { SetBusy(false, "Готово"); }
    }
    async void Retry_Click(object s, RoutedEventArgs e) => await RunOcrAsync();
    void HideCard()
    {
        HideAiSelectionPopup();
        dragging = false;
        if (SelectionCanvas.IsMouseCaptured) SelectionCanvas.ReleaseMouseCapture();
        SelectionBox.Visibility = Visibility.Collapsed;
        OcrCard.Visibility = Visibility.Collapsed;
        selection = Rect.Empty;
        editingNote = null;
        selectionEpoch++; selectedWords.Clear(); activeSurface.TextHighlights.Children.Clear();
    }
    void CardClose_Click(object s, RoutedEventArgs e) => HideCard();
    async void CardAction_Click(object s, RoutedEventArgs e)
    {
        try { await ApplyCardAsync(); } catch (Exception error) { Error(error); }
    }
    async Task ApplyCardAsync()
    {
        if (busy) return;
            if (!cardIsNote) { Clipboard.SetText(OcrText.Text); CardStatus.Text = "Скопировано в буфер обмена"; return; }
            if (current?.Pdf is null) return;
            SetBusy(true,"Сохраняю комментарий…");
            try
            {
            var page = current.Pdf.Pages[activeSurface.Index];
            if (editingNote is not null)
            { if (string.IsNullOrWhiteSpace(OcrText.Text)) page.Annotations.Remove(editingNote); else editingNote.Contents = OcrText.Text; }
            else
            {
                if (string.IsNullOrWhiteSpace(OcrText.Text)) return;
                if (selectedWords.Count>0) PdfTextComments.Add(current.Pdf,activeSurface.Index,selectedWords,OcrText.Text,authors.Selected);
                else
                {
                var anchor=PdfTextComments.ToPdf(page,new Point(selection.X/PageImage.Width,selection.Y/PageImage.Height));
                double x=anchor.X,y=anchor.Y;
                var annotation = new PdfTextAnnotation { Title = authors.Selected, Contents = OcrText.Text, Icon = PdfTextAnnotationIcon.Comment, Color = XColor.FromArgb(162,80,255), Rectangle = new PdfRectangle(new XPoint(x,y-20), new XPoint(x+20,y)) };
                page.Annotations.Add(annotation);
                }
            }
            var editedPage=activeSurface.Index; current.Dirty = true; current.Snapshot(); HideCard(); await RefreshEditedPageAsync(editedPage); Status.Text = "Комментарий добавлен · сохраните PDF";
            }
            finally { SetBusy(false,Status.Text); }
    }
    async void Save_Click(object s, RoutedEventArgs e)=>await SaveAsCurrentAsync();
    async Task SaveAsCurrentAsync()
    {
        if (busy || current is null || (current.Pdf is null && !current.IsWordDocument)) return;
        var dialog = new SaveFileDialog { Filter = current.IsWordDocument ? "Документ Word|*.docx" : "PDF|*.pdf", FileName = current.IsUntitled ? current.Name : Path.GetFileNameWithoutExtension(current.Name) + (current.IsDoc ? ".docx" : current.IsWordDocument ? "_edited.docx" : "_edited.pdf") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var session=current;
            bool convertedDoc=session.IsDoc;
            if (session.IsWordDocument) await SaveDocxAsync(dialog.FileName); else { session.SaveTo(dialog.FileName); session.Dirty=false; }
            session.Path=dialog.FileName;
            await RememberRecentFileAsync(session.Path);
            if(convertedDoc && current==session) { ConfigureDocumentUi(); RefreshComments(); }
            foreach (TabItem tab in Tabs.Items)
                if (tab.Tag==session && tab.Header is StackPanel header && header.Children[0] is TextBlock title) title.Text=session.Name;
            Status.Text="Сохранено: "+dialog.FileName;
        }
        catch (Exception error) { Error(error); }
    }
    async void Convert_Click(object s, RoutedEventArgs e)
    {
        if (busy || current is null) return;
        HideCard();
        var session = current;
        var dialog = new SaveFileDialog { FileName = Path.GetFileNameWithoutExtension(session.Name), Filter = session.Photo is not null ? "PDF|*.pdf|PDF с текстовым слоем OCR|*.pdf|Текст OCR|*.txt|DOCX с текстом OCR|*.docx" : session.IsDoc ? "DOCX|*.docx|PDF|*.pdf" : session.IsWordDocument ? "PDF|*.pdf" : "DOCX (текст, без исходной верстки)|*.docx|PNG текущей страницы|*.png|Текст|*.txt|PDF с текстовым слоем OCR|*.pdf" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (session.IsWordDocument)
            {
                if(Path.GetExtension(dialog.FileName).Equals(".docx",StringComparison.OrdinalIgnoreCase))
                {
                    await WriteDocxCopyAsync(dialog.FileName);
                    Status.Text="Экспорт завершён: "+dialog.FileName;
                }
                else if(Path.GetExtension(dialog.FileName).Equals(".pdf",StringComparison.OrdinalIgnoreCase)) await ExportDocxAsync(dialog.FileName);
                else throw new InvalidOperationException("Выберите формат DOCX или PDF.");
                return;
            }
            var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            if (extension == ".docx" && DarkDialog.Show(this, "Экспорт переносит текст с разделением по страницам, без исходной верстки и изображений. Страницы без текстового слоя будут распознаны OCR. Продолжить?", "Текстовая конвертация", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            SetBusy(true, "Конвертация…");
            if (extension == ".png")
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(await Task.Run(() => session.Render(session.Page)))); using var output = File.Create(dialog.FileName); encoder.Save(output);
            }
            else if (extension == ".pdf")
            {
                if (!session.IsWordDocument && (session.Photo is null || dialog.FilterIndex == 2) && DarkDialog.Show(this,"Будет создана копия из изображений страниц с распознанным текстовым слоем. Интерактивные комментарии, ссылки и формы в эту копию не переносятся.","PDF с OCR",MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                if ((!session.IsWordDocument && session.Photo is null) || (session.Photo is not null && dialog.FilterIndex == 2))
                {
                    using var result = new PdfDocument();
                    var language = ((ComboBoxItem)OcrLanguage.SelectedItem).Tag.ToString()!;
                    for (int p = 0; p < session.Count; p++)
                    {
                        Status.Text = $"OCR для PDF: {p+1} / {session.Count}";
                        var image = await Task.Run(() => session.Render(p));
                        var recognized = await ocr.RecognizeAsync(image,language);
                        var page = SearchablePdf.AddPage(result,image);
                        SearchablePdf.AddTextLayer(page,recognized);
                    }
                    DocumentSession.SaveAtomic(result,dialog.FileName);
                }
                else if (session.Photo is not null)
                {
                    using var result = new PdfDocument(); var page = result.AddPage();
                    using var data = new MemoryStream(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(session.Photo)); encoder.Save(data); data.Position = 0;
                    using var image = XImage.FromStream(data); page.Width = XUnit.FromPoint(image.PointWidth); page.Height = XUnit.FromPoint(image.PointHeight);
                    using (var graphics = XGraphics.FromPdfPage(page)) graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
                    DocumentSession.SaveAtomic(result, dialog.FileName);
                }
                else session.SaveTo(dialog.FileName);
            }
            else
            {
                var language = ((ComboBoxItem)OcrLanguage.SelectedItem).Tag.ToString()!;
                var texts = new List<string>();
                using var pig = session.RenderBytes is not null ? UglyToad.PdfPig.PdfDocument.Open(session.RenderBytes) : null;
                for (int p = 0; p < session.Count; p++)
                {
                    Status.Text = $"Извлечение текста: {p+1} / {session.Count}";
                    var text = pig is null ? "" : UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(pig.GetPage(p+1));
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        var image = await Task.Run(() => session.Render(p));
                        text = (await ocr.RecognizeAsync(image, language)).Text;
                    }
                    texts.Add(text);
                }
                if (extension == ".docx") DocumentSession.WriteDocx(texts, dialog.FileName); else await File.WriteAllTextAsync(dialog.FileName, string.Join("\n\n", texts));
            }
            Status.Text = "Экспорт завершён: " + dialog.FileName;
        }
        catch (Exception error) { Error(error); Status.Text = "Экспорт не выполнен"; }
        finally { SetBusy(false, Status.Text); }
    }
    internal async Task TestCardAsync(string imagePath)
    {
        var session = DocumentSession.Open(imagePath); sessions.Add(session); current = session;
        Tabs.Items.Add(new TabItem { Header = session.Name, Tag = session }); Tabs.SelectedIndex = 0;
        await RenderAsync();
        OcrLanguage.SelectedIndex = 1;
        selection = new Rect(20,20,360,160);
        Canvas.SetLeft(SelectionBox,selection.X); Canvas.SetTop(SelectionBox,selection.Y); SelectionBox.Width=selection.Width; SelectionBox.Height=selection.Height; SelectionBox.Visibility=Visibility.Visible;
        ShowCard(selection,false); await RunOcrAsync();
        if (!OcrText.Text.Contains("HELLO",StringComparison.OrdinalIgnoreCase)) throw new Exception("OCR card scenario failed: " + CardStatus.Text);
        CardAction_Click(this,new RoutedEventArgs());
        if (Clipboard.GetText() != OcrText.Text) throw new Exception("Clipboard copy failed");
        UpdateLayout(); PositionCard(selection);
        if (Canvas.GetLeft(OcrCard)<0 || Canvas.GetLeft(OcrCard)+OcrCard.ActualWidth>Workspace.ActualWidth) throw new Exception("OCR card outside workspace");
    }
    internal void TestSelectionReset()
    {
        void Prepare() { selection = new Rect(20,20,120,60); SelectionBox.Visibility = Visibility.Visible; ShowCard(selection,false); }
        void AssertReset() { if (SelectionBox.Visibility != Visibility.Collapsed || OcrCard.Visibility != Visibility.Collapsed || !selection.IsEmpty || dragging) throw new Exception("OCR selection was retained after changing tool"); }
        Prepare(); NoteMode_Click(this,new RoutedEventArgs()); AssertReset();
        Prepare(); OcrMode_Click(this,new RoutedEventArgs()); AssertReset();
        Prepare(); Documents_Click(this,new RoutedEventArgs()); AssertReset();
        Prepare(); OcrLanguage.SelectedIndex = OcrLanguage.SelectedIndex == 0 ? 1 : 0; AssertReset();
        Prepare(); CardClose_Click(this,new RoutedEventArgs()); AssertReset();
    }
    void OnClosing(object? s, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; Status.Text = "Дождитесь окончания текущей операции"; return; }
        if (sessions.Any(x => x.Dirty) && DarkDialog.Show(this, "Есть несохранённые изменения. Закрыть приложение без сохранения?", "Закрытие", MessageBoxButton.YesNo) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        searchCancellation?.Cancel(); searchTimer.Stop(); ocr.Dispose(); foreach (var session in sessions) session.Dispose();
    }
}







