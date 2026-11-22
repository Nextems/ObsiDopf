using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml.Packaging;
using ObsidianDocs;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using W=DocumentFormat.OpenXml.Wordprocessing;

internal static class Program
{
    [STAThread]
    static void Main()=>RunAsync().GetAwaiter().GetResult();

    static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"obsidopf-batch-conversion-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source=Directory.CreateDirectory(Path.Combine(root,"source")).FullName;
            string output=Directory.CreateDirectory(Path.Combine(root,"output")).FullName;
            string pdf=Path.Combine(source,"sample.pdf"); CreatePdf(pdf);
            string docx=Path.Combine(source,"sample.docx"); DocumentSession.WriteDocx(["Saved Word text"],docx);
            byte[] pdfBefore=File.ReadAllBytes(pdf),docxBefore=File.ReadAllBytes(docx);
            int ocrCalls=0;
            using var converter=new BatchConversion(renderWord:(snapshot,token)=>
            {
                Check(File.ReadAllBytes(snapshot).SequenceEqual(docxBefore),"Word converter receives a stable snapshot");
                token.ThrowIfCancellationRequested(); return pdfBefore;
            },recognize:(_,language,token)=>
            {
                token.ThrowIfCancellationRequested(); ocrCalls++; Check(language=="en","OCR language forwarded");
                return Task.FromResult(new OcrResult("Recognized second page",[]));
            });

            string convertedDocx;
            using(var pending=await converter.PrepareAsync(pdf,output,"en",default))
            {
                Check(!File.Exists(Path.Combine(output,"sample.docx")),"Result not visible before commit");
                convertedDocx=pending.Commit(default);
            }
            Check(ocrCalls==1,"OCR only used on page without text");
            using(var word=WordprocessingDocument.Open(convertedDocx,false))
            {
                var document=word.MainDocumentPart!.Document!;
                Check(document.InnerText.Contains("First page text"),"PDF text extracted");
                Check(document.InnerText.Contains("Recognized second page"),"OCR text included");
                Check(document.Descendants<W.Break>().Count(b=>b.Type?.Value==W.BreakValues.Page)==1,"Page boundary preserved");
            }
            using(var pending=await converter.PrepareAsync(docx,output,"en",default))
                Check(Path.GetExtension(pending.Commit(default))==".pdf","Mixed list chooses Word to PDF");
            using(var pending=await converter.PrepareAsync(pdf,output,"en",default))
                Check(Path.GetFileName(pending.Commit(default))=="sample_2.docx","Name collision receives numeric suffix");
            Check(File.ReadAllBytes(pdf).SequenceEqual(pdfBefore) && File.ReadAllBytes(docx).SequenceEqual(docxBefore),"Originals unchanged");
            // Saving beside an opposite-format input must not overwrite it.
            using(var pending=await converter.PrepareAsync(pdf,null,"en",default))
                Check(Path.GetFileName(pending.Commit(default))=="sample_2.docx","Opposite-format source protected");
            Check(File.ReadAllBytes(docx).SequenceEqual(docxBefore),"Existing Word input preserved");
            Console.WriteLine("PASS mixed formats, text/OCR, page breaks, destinations, duplicate names, originals");

            int count=Directory.GetFiles(output).Length;
            using(var pending=await converter.PrepareAsync(docx,output,"en",default)) { }
            Check(Directory.GetFiles(output).Length==count,"Disposing uncommitted output removes temporary file");
            using(var cancel=new CancellationTokenSource())
            {
                using var pending=await converter.PrepareAsync(docx,output,"en",default);
                cancel.Cancel(); await ThrowsAsync<OperationCanceledException>(()=>Task.FromResult(pending.Commit(cancel.Token)));
            }
            Check(Directory.GetFiles(output).Length==count,"Cancel before commit creates no final output");
            using(var cancel=new CancellationTokenSource())
            {
                cancel.Cancel(); await ThrowsAsync<OperationCanceledException>(()=>converter.PrepareAsync(pdf,output,"en",cancel.Token));
            }
            using(var cancel=new CancellationTokenSource())
            using(var slow=new BatchConversion(recognize:async (_,_,token)=> { cancel.Cancel(); await Task.Delay(Timeout.Infinite,token); return new OcrResult("",[]); }))
                await ThrowsAsync<OperationCanceledException>(()=>slow.PrepareAsync(pdf,output,"en",cancel.Token));
            Check(!Directory.GetFiles(output,".obsidopf-*").Any(),"Cancelling OCR cleans staged output");

            using(var broken=new BatchConversion(renderWord:(_,_)=>throw new InvalidDataException("Bad Word")))
                await ThrowsAsync<InvalidDataException>(()=>broken.PrepareAsync(docx,output,"en",default));
            await ThrowsAsync<ArgumentException>(()=>converter.PrepareAsync(Path.Combine(source,"file.txt"),output,"en",default));
            await ThrowsAsync<DirectoryNotFoundException>(()=>converter.PrepareAsync(pdf,Path.Combine(root,"missing"),"en",default));
            string corrupt=Path.Combine(source,"broken.pdf"); File.WriteAllText(corrupt,"not a PDF");
            await ThrowsAsync<Exception>(()=>converter.PrepareAsync(corrupt,output,"en",default));
            using(var pending=await converter.PrepareAsync(docx,output,"en",default)) pending.Commit(default);
            Check(!Directory.GetFiles(output,".obsidopf-*").Any(),"Failures leave no temporary files; next conversion succeeds");
            Console.WriteLine("PASS cancellation during OCR/before commit, errors, cleanup, continue after error");

            // Exercise the actual worker API's cancellation before it acquires work.
            using(var worker=new OcrService())
            using(var cancel=new CancellationTokenSource())
            {
                var image=BitmapSource.Create(1,1,96,96,PixelFormats.Bgr32,null,new byte[4],4); image.Freeze(); cancel.Cancel();
                await ThrowsAsync<OperationCanceledException>(()=>worker.RecognizeAsync(image,"en",cancel.Token));
            }
            Console.WriteLine("PASS OCR cancellation token");

            if(Environment.GetEnvironmentVariable("OBSIDIAN_DOCS_SOFFICE") is { Length:>0 })
            {
                using var real=new BatchConversion();
                using var pending=await real.PrepareAsync(docx,output,"en",default);
                string realPdf=pending.Commit(default);
                using var result=UglyToad.PdfPig.PdfDocument.Open(realPdf);
                Check(result.NumberOfPages==1 && result.GetPage(1).Text.Contains("Saved Word text"),"Real DOCX to PDF content");
                Console.WriteLine("PASS actual LibreOffice DOCX to PDF");
            }
        }
        finally { Directory.Delete(root,true); }
    }
    static void CreatePdf(string path)
    {
        using var pdf=new PdfDocument(); var page=pdf.AddPage();
        using(var graphics=XGraphics.FromPdfPage(page)) graphics.DrawString("First page text",new XFont("Arial",12),XBrushes.Black,new XPoint(40,60));
        pdf.AddPage(); pdf.Save(path);
    }
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    static async Task ThrowsAsync<T>(Func<Task> action) where T:Exception
    {
        try { await action(); } catch(T) { return; }
        throw new Exception("Expected "+typeof(T).Name);
    }
}
