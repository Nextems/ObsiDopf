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
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        AuthorPicker.ItemsSource = authors.Names; AuthorPicker.SelectedItem = authors.Selected;
        Closing += OnClosing; OcrLanguage.SelectionChanged += (_, _) => HideCard();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { HideCard(); } if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) Open_Click(this, new RoutedEventArgs()); };
    }
    void Error(Exception error) => DarkDialog.Show(this, error.Message, "Не удалось выполнить действие", MessageBoxButton.OK, MessageBoxImage.Warning);
    void SetBusy(bool value, string message)
    {
        busy = value; Tabs.IsEnabled = !value; OcrButton.IsEnabled = !value; NoteButton.IsEnabled = !value;
        CardAction.IsEnabled = !value; RetryButton.IsEnabled = !value; Status.Text = message;
        TextNoteButton.IsEnabled=!value; DeleteCommentButton.IsEnabled=!value;
        DocxHost.IsEnabled=DocxTools.IsEnabled=!value;
    }
    async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        HideCard();
        var dialog = new OpenFileDialog { Filter = "Документы и изображения|*.pdf;*.docx;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        await OpenPathsAsync(dialog.FileNames);
    }
    internal async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        if(busy) return;
        HideCard();
        foreach (var inputPath in paths)
        {
            try
            {
                var path=Path.GetFullPath(inputPath);
                var existing = sessions.FirstOrDefault(s => s.Path == path);
                if (existing is not null) { Tabs.SelectedIndex = sessions.IndexOf(existing); continue; }
                SetBusy(true, "Открываю документ…");
                var session = await Task.Run(() => DocumentSession.Open(path));
                sessions.Add(session);
                var header = new StackPanel { Orientation = Orientation.Horizontal };
                header.Children.Add(new TextBlock { Text = session.Name, VerticalAlignment = VerticalAlignment.Center });
                var close = new Button { Content = "×", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(12, 0, 0, 0) };
                var tab = new TabItem { Header = header, Tag = session };
                close.Click += (_, _) => CloseSession(session, tab);
                header.Children.Add(close); Tabs.Items.Add(tab); Tabs.SelectedItem = tab;
                current = session; await RenderAsync();
            }
            catch (Exception error) { Error(error); }
            finally { SetBusy(false, current?.IsDocx==true ? "DOCX · Ctrl+S — сохранить · Ctrl+Z — отменить" : "Готово · OCR: выделите область мышью"); }
        }
    }
    void CloseSession(DocumentSession session, TabItem tab)
    {
        if (busy) return;
        if (session.Dirty && DarkDialog.Show(this, "Закрыть без сохранения изменений?", "Несохранённые изменения", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        sessions.Remove(session); Tabs.Items.Remove(tab); session.Dispose();
        if (sessions.Count == 0) { current = null; ConfigureDocumentUi(); bitmap = null; viewGeneration++; surfaces.Clear(); PageStack.Children.Clear(); PageStack.Visibility = Visibility.Collapsed; CommentList.Children.Clear(); Welcome.Visibility = Visibility.Visible; SideList.Items.Clear(); PageStatus.Text = ""; OcrCard.Visibility = Visibility.Collapsed; }
    }
    async void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is not TabItem { Tag: DocumentSession session } || current == session) return;
        current = session;
        if (!busy) { try { SetBusy(true, "Загружаю страницу…"); await RenderAsync(); } catch (Exception error) { Error(error); } finally { SetBusy(false, "Готово"); } }
    }
    async void Prev_Click(object s, RoutedEventArgs e) { try { await NavigateAsync((current?.Page ?? 0)-1); } catch (Exception error) { Error(error); } }
    async void Next_Click(object s, RoutedEventArgs e) { try { await NavigateAsync((current?.Page ?? 0)+1); } catch (Exception error) { Error(error); } }
    void ZoomOut_Click(object s, RoutedEventArgs e) { if (busy) return; zoom = Math.Max(.3, zoom-.1); ResizePage(); HideCard(); }
    void ZoomIn_Click(object s, RoutedEventArgs e) { if (busy) return; zoom = Math.Min(3, zoom+.1); ResizePage(); HideCard(); }
    void OcrMode_Click(object s, RoutedEventArgs e) { HideCard(); textNoteMode=false; TextNoteButton.Background=Brush("#303039"); noteMode = false; OcrButton.Background = Brush("#69518E"); NoteButton.Background = Brush("#303039"); Status.Text = "Выделите прямоугольную область для распознавания"; }
    void NoteMode_Click(object s, RoutedEventArgs e) { HideCard(); textNoteMode=false; TextNoteButton.Background=Brush("#303039"); noteMode = true; NoteButton.Background = Brush("#69518E"); OcrButton.Background = Brush("#303039"); Status.Text = "Щёлкните по странице PDF, чтобы добавить комментарий"; }
    static SolidColorBrush Brush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, PageImage.Width), Math.Clamp(p.Y, 0, PageImage.Height));
    void Select_Start(object s, MouseButtonEventArgs e)
    {
        if (busy || bitmap is null || current?.IsDocx==true) return;
        if (s is not Canvas { Tag: PageSurface surface } || surface.Image.Source is not BitmapSource source) return;
        HideCard(); activeSurface=surface; bitmap=source; current!.Page=surface.Index; UpdateCurrentPageUi();
        start = Clamp(e.GetPosition(SelectionCanvas)); editingNote = null;
        if (textNoteMode)
        {
            if (current?.Pdf is null || current.IsDocx) { Status.Text="Комментарии к тексту доступны в PDF. Сначала экспортируйте документ в PDF."; return; }
            dragging=true; SelectionCanvas.CaptureMouse();
            wordsTask=LoadTextWordsAsync(surface);
            return;
        }
        if (noteMode)
        {
            if (current?.Pdf is null || current.IsDocx) { Status.Text = "Комментарии доступны в PDF. Сначала экспортируйте документ в PDF."; return; }
            selection = new Rect(start.X, start.Y, 1, 1); ShowCard(selection, true); OcrText.Text = ""; OcrText.Focus(); return;
        }
        dragging = true; SelectionCanvas.CaptureMouse(); SelectionBox.Visibility = Visibility.Visible; UpdateSelection(start);
    }
    void Select_Move(object s, MouseEventArgs e)
    {
        if (!dragging) return;
        var end=Clamp(e.GetPosition(SelectionCanvas));
        if (textNoteMode) { if (wordsTask.IsCompletedSuccessfully) UpdateTextSelection(end); }
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
        if (textNoteMode) { await FinishTextSelectionAsync(end); return; }
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
    void Save_Click(object s, RoutedEventArgs e)
    {
        if (busy || current is null || (current.Pdf is null && !current.IsDocx)) return;
        var dialog = new SaveFileDialog { Filter = current.IsDocx ? "Документ Word|*.docx" : "PDF|*.pdf", FileName = Path.GetFileNameWithoutExtension(current.Name) + (current.IsDocx ? "_edited.docx" : "_edited.pdf") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (current.IsDocx) SaveDocx(dialog.FileName); else current.SaveTo(dialog.FileName);
            current.Path=dialog.FileName; current.Dirty=false;
            foreach (TabItem tab in Tabs.Items)
                if (tab.Tag==current && tab.Header is StackPanel header && header.Children[0] is TextBlock title) title.Text=current.Name;
            Status.Text="Сохранено: "+dialog.FileName;
        }
        catch (Exception error) { Error(error); }
    }
    async void Convert_Click(object s, RoutedEventArgs e)
    {
        if (busy || current is null) return;
        HideCard();
        var session = current;
        var dialog = new SaveFileDialog { FileName = Path.GetFileNameWithoutExtension(session.Name), Filter = session.Photo is not null ? "PDF|*.pdf|PDF с текстовым слоем OCR|*.pdf|Текст OCR|*.txt|DOCX с текстом OCR|*.docx" : session.IsDocx ? "PDF|*.pdf" : "DOCX (текст, без исходной верстки)|*.docx|PNG текущей страницы|*.png|Текст|*.txt|PDF с текстовым слоем OCR|*.pdf" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (session.IsDocx) { await ExportDocxAsync(dialog.FileName); return; }
            var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            if (extension == ".docx" && DarkDialog.Show(this, "Экспорт переносит текст с разделением по страницам, без исходной верстки и изображений. Страницы без текстового слоя будут распознаны OCR. Продолжить?", "Текстовая конвертация", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            SetBusy(true, "Конвертация…");
            if (extension == ".png")
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(await Task.Run(() => session.Render(session.Page)))); using var output = File.Create(dialog.FileName); encoder.Save(output);
            }
            else if (extension == ".pdf")
            {
                if (!session.IsDocx && (session.Photo is null || dialog.FilterIndex == 2) && DarkDialog.Show(this,"Будет создана копия из изображений страниц с распознанным текстовым слоем. Интерактивные комментарии, ссылки и формы в эту копию не переносятся.","PDF с OCR",MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                if ((!session.IsDocx && session.Photo is null) || (session.Photo is not null && dialog.FilterIndex == 2))
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
        ocr.Dispose(); foreach (var session in sessions) session.Dispose();
    }
}







