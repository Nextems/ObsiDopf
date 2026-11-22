using System.IO;
using System.Windows.Media.Imaging;

namespace ObsidianDocs;

internal sealed class BatchConversion : IDisposable
{
    readonly OcrService ocr=new();
    readonly Func<string,CancellationToken,byte[]> renderWord;
    readonly Func<BitmapSource,string,CancellationToken,Task<OcrResult>> recognize;

    internal BatchConversion(Func<string,CancellationToken,byte[]>? renderWord=null,
        Func<BitmapSource,string,CancellationToken,Task<OcrResult>>? recognize=null)
    {
        this.renderWord=renderWord ?? DocxRenderer.Render;
        this.recognize=recognize ?? ocr.RecognizeAsync;
    }
    internal static bool Supports(string path)=>Path.GetExtension(path).ToLowerInvariant() is ".pdf" or ".docx" or ".doc";
    internal static string TargetExtension(string path)=>Path.GetExtension(path).Equals(".pdf",StringComparison.OrdinalIgnoreCase) ? ".docx" : ".pdf";

    internal async Task<Prepared> PrepareAsync(string source,string? outputDirectory,string language,CancellationToken cancellation,IProgress<string>? progress=null)
    {
        source=Path.GetFullPath(source);
        if(!Supports(source)) throw new ArgumentException("Выберите PDF, DOCX или DOC.");
        string destination=Path.GetFullPath(outputDirectory ?? Path.GetDirectoryName(source)!);
        if(!Directory.Exists(destination)) throw new DirectoryNotFoundException("Папка результатов недоступна: "+destination);
        cancellation.ThrowIfCancellationRequested();
        var bytes=await File.ReadAllBytesAsync(source,cancellation).ConfigureAwait(false);
        string extension=TargetExtension(source);
        string temporary=Path.Combine(destination,".obsidopf-convert-"+Guid.NewGuid().ToString("N")+".tmp");
        // Reserve our staging name; commit never overwrites an existing file.
        using(File.Open(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { }
        try
        {
            if(extension==".docx")
            {
                var texts=await ReadPdfAsync(bytes,language,cancellation,progress).ConfigureAwait(false);
                await Task.Run(()=>DocumentSession.WriteDocx(texts,temporary),cancellation).ConfigureAwait(false);
            }
            else
            {
                progress?.Report("Создаю PDF…");
                var folder=Path.Combine(Path.GetTempPath(),"ObsidianDocs","batch-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    // Convert a stable snapshot, independently of the editor and source file.
                    string snapshot=Path.Combine(folder,"document"+Path.GetExtension(source));
                    await File.WriteAllBytesAsync(snapshot,bytes,cancellation).ConfigureAwait(false);
                    var pdf=await Task.Run(()=>renderWord(snapshot,cancellation),cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    await File.WriteAllBytesAsync(temporary,pdf,cancellation).ConfigureAwait(false);
                }
                finally { try { Directory.Delete(folder,true); } catch(IOException) { } catch(UnauthorizedAccessException) { } }
            }
            cancellation.ThrowIfCancellationRequested();
            return new Prepared(temporary,destination,Path.GetFileNameWithoutExtension(source),extension);
        }
        catch { DeleteTemporary(temporary); throw; }
    }
    Task<List<string>> ReadPdfAsync(byte[] bytes,string language,CancellationToken cancellation,IProgress<string>? progress)=>Task.Run(async ()=>
    {
        using var pdf=UglyToad.PdfPig.PdfDocument.Open(bytes);
        if(pdf.NumberOfPages==0) throw new InvalidDataException("В PDF нет страниц.");
        using var images=new DocumentSession { Path="material.pdf",RenderBytes=bytes };
        var texts=new List<string>(pdf.NumberOfPages);
        for(int page=0;page<pdf.NumberOfPages;page++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report($"Текст: страница {page+1} из {pdf.NumberOfPages}");
            string text=UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(pdf.GetPage(page+1));
            if(string.IsNullOrWhiteSpace(text))
            {
                progress?.Report($"OCR: страница {page+1} из {pdf.NumberOfPages}");
                var image=images.Render(page);
                cancellation.ThrowIfCancellationRequested();
                text=(await recognize(image,language,cancellation).ConfigureAwait(false)).Text;
            }
            texts.Add(text);
        }
        return texts;
    },cancellation);

    internal sealed class Prepared(string temporary,string destination,string stem,string extension) : IDisposable
    {
        string? pending=temporary;
        internal string Commit(CancellationToken cancellation)
        {
            var staged=pending ?? throw new InvalidOperationException("Результат уже сохранён или отменён.");
            // Leave room for a numeric suffix and Windows path components.
            string name=new(stem.Take(160).ToArray());
            for(int number=1;;number++)
            {
                cancellation.ThrowIfCancellationRequested();
                string target=Path.Combine(destination,name+(number==1 ? "" : "_"+number)+extension);
                try { File.Move(staged,target,false); pending=null; return target; }
                catch(IOException) when(File.Exists(target)) { }
            }
        }
        public void Dispose() { if(pending is { } path) { pending=null; DeleteTemporary(path); } }
    }
    static void DeleteTemporary(string path) { try { File.Delete(path); } catch(IOException) { } catch(UnauthorizedAccessException) { } }
    public void Dispose()=>ocr.Dispose();
}
