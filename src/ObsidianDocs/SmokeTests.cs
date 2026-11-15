using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Drawing;

namespace ObsidianDocs;
public static class SmokeTests
{
    public static async Task RunAsync()
    {
        var folder = MainWindow.FindArtifacts();
        Directory.CreateDirectory(folder); folder = Path.GetFullPath(folder);
        using var pdf = DocumentSession.TextPdf(["Привет, мир!", "OCR TEST 12345", "Second paragraph"]);
        pdf.Pages[0].Annotations.Add(new PdfTextAnnotation { Contents = "Тест комментария", Title = "Smoke", Rectangle = new PdfRectangle(new XPoint(100, 600), new XPoint(120, 620)), Icon = PdfTextAnnotationIcon.Note });
        var path = Path.Combine(folder, "sample.pdf"); DocumentSession.SaveAtomic(pdf,path);
        using var session = DocumentSession.Open(path);
        if (session.Pdf!.Pages[0].Annotations[0].Contents != "Тест комментария") throw new Exception("Annotation roundtrip failed");
        // Reproduce the real UI lifecycle: snapshot after each edit, save more
        // than once, continue editing the same session, then reopen the result.
        var repeatedPath = Path.Combine(folder, "repeated-save.pdf");
        for (int i = 0; i < 3; i++)
        {
            session.Pdf!.Pages[0].Annotations.Add(new PdfTextAnnotation { Contents = $"Заметка {i}", Rectangle = new PdfRectangle(new XPoint(80, 500-i*30), new XPoint(100,520-i*30)) });
            session.Snapshot();
            session.SaveTo(repeatedPath);
            session.SaveTo(repeatedPath);
        }
        session.Pdf!.Pages[0].Annotations[0].Contents = "Изменено после сохранения";
        session.Snapshot();
        session.Pdf!.Pages[0].Annotations.Remove(session.Pdf.Pages[0].Annotations[1]);
        session.Snapshot(); session.SaveTo(repeatedPath);
        using (var reopened = DocumentSession.Open(repeatedPath))
        {
            if (reopened.Pdf!.Pages[0].Annotations.Count != 3 || reopened.Pdf.Pages[0].Annotations[0].Contents != "Изменено после сохранения")
                throw new Exception("Repeated edit/save lifecycle failed");
        }
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
        if (!pig.GetPage(1).Text.Contains("Привет")) throw new Exception("Unicode PDF failed");
        var image = session.Render(0); if (image.PixelWidth < 500) throw new Exception("Render failed");
        DocumentSession.WriteDocx(["Привет, мир!\nSecond line"], Path.Combine(folder,"sample.docx"));
        File.WriteAllBytes(Path.Combine(folder,"docx-preview.pdf"),DocxRenderer.Render(Path.Combine(folder,"sample.docx")));
        using var word = DocumentSession.Open(Path.Combine(folder,"docx-preview.pdf"));
        word.SaveTo(Path.Combine(folder, "docx-export.pdf"));
        word.SaveTo(Path.Combine(folder, "docx-export.pdf"));
        if (word.Render(0).PixelHeight == 0) throw new Exception("DOCX preview failed");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0,0,1000,260));
            dc.DrawText(new FormattedText("HELLO WORLD\nOCR TEST 12345", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 60, Brushes.Black,1), new Point(40,35));
        }
        var scan = new RenderTargetBitmap(1000,260,96,96,PixelFormats.Pbgra32); scan.Render(visual); scan.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(scan)); using (var f = File.Create(Path.Combine(folder,"scan.png"))) encoder.Save(f);
        using var ocr = new OcrService();
        var result = await ocr.RecognizeAsync(scan,"en");
        if (!result.Text.Contains("HELLO",StringComparison.OrdinalIgnoreCase) || result.Regions.Count == 0) throw new Exception("OCR failed: " + result.Text);
        var ruVisual = new DrawingVisual();
        using (var dc = ruVisual.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0,0,1000,260)); dc.DrawText(new FormattedText("ПРИВЕТ МИР\nПроверка текста 123", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"),FlowDirection.LeftToRight,new Typeface("Arial"),60,Brushes.Black,1),new Point(40,35)); }
        var ruScan = new RenderTargetBitmap(1000,260,96,96,PixelFormats.Pbgra32); ruScan.Render(ruVisual); ruScan.Freeze();
        var ruResult = await ocr.RecognizeAsync(ruScan,"ru");
        if (!ruResult.Text.Contains("ПРИВЕТ",StringComparison.OrdinalIgnoreCase)) throw new Exception("RU OCR failed: " + ruResult.Text);
        using (var searchable = new PdfDocument())
        {
            var page = SearchablePdf.AddPage(searchable,scan); SearchablePdf.AddTextLayer(page,result);
            var searchablePath = Path.Combine(folder,"searchable.pdf"); DocumentSession.SaveAtomic(searchable,searchablePath);
            using var textCheck = UglyToad.PdfPig.PdfDocument.Open(searchablePath);
            if (!textCheck.GetPage(1).Text.Contains("HELLO")) throw new Exception("Searchable PDF text layer failed");
        }
        var window = new MainWindow(); window.Show(); window.UpdateLayout();
        await window.TestCardAsync(Path.Combine(folder,"scan.png"));
        var shot = new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); shot.Render(window);
        var output = new PngBitmapEncoder(); output.Frames.Add(BitmapFrame.Create(shot)); using (var f = File.Create(Path.Combine(folder,"interface.png"))) output.Save(f);
        window.TestSelectionReset();
        LayoutRegression.Run(folder);
        var dialog = new DarkDialog(window,"Документ готов к просмотру. Проверка тёмного оформления уведомлений.","Просмотр DOCX",MessageBoxButton.OK);
        dialog.Show(); dialog.UpdateLayout();
        var dialogShot = new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32); dialogShot.Render(dialog);
        var dialogOutput = new PngBitmapEncoder(); dialogOutput.Frames.Add(BitmapFrame.Create(dialogShot)); using (var f=File.Create(Path.Combine(folder,"dialog.png"))) dialogOutput.Save(f);
        dialog.Close();
        window.Close();
        File.WriteAllText(Path.Combine(folder,"smoke-result.txt"), $"PASS: PDF rendering, Cyrillic, repeated annotation edits/saves, DOCX layout (table/image/header/footer/page break), source integrity, EN/RU OCR with regions, WPF OCR card, clipboard, searchable PDF, OCR selection reset, dark dialogs\nEN: {result.Text}\nRU: {ruResult.Text}");
    }
}



