using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("shell32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint ExtractIconEx(string file,int index,out nint large,out nint small,uint count);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(nint icon);
    [STAThread]
    static void Main(string[] args)
    {
        if (args[0]=="--extract")
        {
            if (ExtractIconEx(Path.GetFullPath(args[1]),0,out var large,out var small,1)==0 || large==0) throw new Exception("No executable icon found");
            try
            {
                var image=System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(large,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
                var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file=File.Create(args[2]); png.Save(file);
                Console.WriteLine("Extracted executable icon");
            }
            finally { if (large!=0) DestroyIcon(large); if (small!=0) DestroyIcon(small); }
            return;
        }
        var folder=Path.GetFullPath(args[0]);
        var svg=XDocument.Load(Path.Combine(folder,"ObsidianDocs.svg"));
        BitmapSource Render(int size)
        {
            var visual=new DrawingVisual();
            using(var drawing=visual.RenderOpen())
            {
                drawing.PushTransform(new ScaleTransform(size/256.0,size/256.0));
                foreach(var path in svg.Root!.Elements())
                {
                    var fill=(string?)path.Attribute("fill"); var stroke=(string?)path.Attribute("stroke");
                    double width=double.Parse((string?)path.Attribute("stroke-width")??"0",CultureInfo.InvariantCulture);
                    double opacity=double.Parse((string?)path.Attribute("opacity")??"1",CultureInfo.InvariantCulture);
                    drawing.PushOpacity(opacity);
                    var brush=fill is null or "none"?null:(Brush)new BrushConverter().ConvertFromString(fill)!;
                    Pen? pen=stroke is null?null:new Pen((Brush)new BrushConverter().ConvertFromString(stroke)!,width) { StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round };
                    drawing.DrawGeometry(brush,pen,Geometry.Parse((string)path.Attribute("d")!)); drawing.Pop();
                }
                drawing.Pop();
            }
            var bitmap=new RenderTargetBitmap(size,size,96,96,PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
        }
        var sizes=new[]{16,24,32,48,64,128,256}; var frames=new List<byte[]>();
        foreach(int size in sizes)
        {
            var bitmap=Render(size);
            if(size==256)
            {
                var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var data=new MemoryStream(); png.Save(data); frames.Add(data.ToArray());
                File.WriteAllBytes(Path.Combine(folder,"ObsidianDocs.png"),data.ToArray());
            }
            else
            {
                var rgba=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0); var pixels=new byte[size*size*4]; rgba.CopyPixels(pixels,size*4,0);
                int maskStride=((size+31)/32)*4; var mask=new byte[maskStride*size];
                using var data=new MemoryStream(); using var writer=new BinaryWriter(data);
                writer.Write(40); writer.Write(size); writer.Write(size*2); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0); writer.Write(pixels.Length+mask.Length); writer.Write(0);writer.Write(0);writer.Write(0);writer.Write(0);
                for(int row=size-1;row>=0;row--)
                {
                    writer.Write(pixels,row*size*4,size*4);
                    for(int x=0;x<size;x++) if(pixels[(row*size+x)*4+3]==0) mask[(size-1-row)*maskStride+x/8]|=(byte)(0x80>>(x%8));
                }
                writer.Write(mask); frames.Add(data.ToArray());
            }
        }
        using var output=File.Create(Path.Combine(folder,"ObsidianDocs.ico")); using var ico=new BinaryWriter(output);
        ico.Write((ushort)0); ico.Write((ushort)1); ico.Write((ushort)sizes.Length); int offset=6+16*sizes.Length;
        for(int i=0;i<sizes.Length;i++) { ico.Write((byte)(sizes[i]==256?0:sizes[i]));ico.Write((byte)(sizes[i]==256?0:sizes[i]));ico.Write((byte)0);ico.Write((byte)0);ico.Write((ushort)1);ico.Write((ushort)32);ico.Write(frames[i].Length);ico.Write(offset);offset+=frames[i].Length; }
        foreach(var frame in frames) ico.Write(frame);
        Console.WriteLine("Created SVG-derived PNG and ICO with sizes: "+string.Join(", ",sizes));
    }
}
