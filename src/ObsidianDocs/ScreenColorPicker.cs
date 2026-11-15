using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ObsidianDocs;

internal sealed class ScreenColorPicker : Window
{
    sealed record ScreenFrame(BitmapSource Image,int Left,int Top,int Width,int Height);
    readonly ScreenFrame frame;
    readonly Canvas hud=new();
    readonly Border badge=new() { Background=new SolidColorBrush(Color.FromRgb(37,37,45)),BorderBrush=new SolidColorBrush(Color.FromRgb(179,154,245)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(10),IsHitTestVisible=false };
    readonly Border swatch=new() { Width=30,Height=30,CornerRadius=new CornerRadius(4),BorderBrush=Brushes.Gray,BorderThickness=new Thickness(1),Margin=new Thickness(0,0,9,0) };
    readonly TextBlock value=new() { Foreground=Brushes.White,FontSize=15,FontWeight=FontWeights.SemiBold };
    public Color? SelectedColor { get; private set; }
    public string? ErrorMessage { get; private set; }
    bool finishing;

    public ScreenColorPicker(Window owner)
    {
        // Capture only on the user's eyedropper action. The frame remains in
        // memory, is never saved or sent anywhere, and predates this overlay.
        frame=Capture(); Owner=owner; ShowInTaskbar=false; Topmost=true;
        WindowStyle=WindowStyle.None; ResizeMode=ResizeMode.NoResize; WindowStartupLocation=WindowStartupLocation.Manual;
        Left=SystemParameters.VirtualScreenLeft; Top=SystemParameters.VirtualScreenTop;
        Width=SystemParameters.VirtualScreenWidth; Height=SystemParameters.VirtualScreenHeight;
        Background=Brushes.Black; Cursor=Cursors.Cross;
        var root=new Grid { ClipToBounds=true,Background=Brushes.Transparent }; Content=root;
        root.Children.Add(new Image { Source=frame.Image,Stretch=Stretch.Fill,IsHitTestVisible=false });
        root.Children.Add(hud);
        var row=new StackPanel { Orientation=Orientation.Horizontal }; badge.Child=row;
        row.Children.Add(swatch);
        var labels=new StackPanel(); row.Children.Add(labels); labels.Children.Add(value);
        labels.Children.Add(new TextBlock { Text="Нажмите на цвет · Esc — отмена",Foreground=new SolidColorBrush(Color.FromRgb(194,183,211)),FontSize=11,Margin=new Thickness(0,4,0,0) });
        hud.Children.Add(badge);
        Loaded+=(_,_)=>
        {
            try { PositionOverlay(); Activate(); Focus(); UpdateSample(); }
            catch(Exception error) { ErrorMessage=error.Message; Finish(false); }
        };
        MouseMove+=(_,_)=>UpdateSample();
        MouseLeftButtonDown+=(_,e)=> { if(UpdateSample()) Finish(true); e.Handled=true; };
        MouseRightButtonDown+=(_,e)=> { Finish(false); e.Handled=true; };
        PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Escape) { Finish(false); e.Handled=true; } };
        Deactivated+=(_,_)=> { if(IsVisible) Finish(false); };
        Closing+=(_,_)=>finishing=true;
    }
    void Finish(bool accepted) { if(finishing) return; finishing=true; DialogResult=accepted; }
    void PositionOverlay()
    {
        IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            if(!SetWindowPos(new WindowInteropHelper(this).Handle,new IntPtr(-1),frame.Left,frame.Top,frame.Width,frame.Height,0x0010))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { if(previous!=IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    bool UpdateSample()
    {
        if(!GetPhysicalCursorPos(out var point)) return false;
        int x=point.X-frame.Left,y=point.Y-frame.Top;
        if(x<0 || y<0 || x>=frame.Width || y>=frame.Height) return false;
        byte[] pixel=new byte[4]; frame.Image.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);
        var color=Color.FromRgb(pixel[2],pixel[1],pixel[0]); SelectedColor=color;
        swatch.Background=new SolidColorBrush(color); value.Text=ColorPaletteWindow.Hex(color);
        Point local=PointFromScreen(new Point(point.X,point.Y));
        Canvas.SetLeft(badge,Math.Clamp(local.X+22,4,Math.Max(4,ActualWidth-280)));
        Canvas.SetTop(badge,Math.Clamp(local.Y+24,4,Math.Max(4,ActualHeight-78)));
        return true;
    }
    static ScreenFrame Capture()
    {
        IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
        IntPtr screen=IntPtr.Zero,memory=IntPtr.Zero,bitmap=IntPtr.Zero,old=IntPtr.Zero;
        try
        {
            int left=GetSystemMetrics(76),top=GetSystemMetrics(77),width=GetSystemMetrics(78),height=GetSystemMetrics(79);
            if(width<=0 || height<=0) throw new InvalidOperationException("Не удалось определить размер экрана.");
            screen=GetDC(IntPtr.Zero); if(screen==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            memory=CreateCompatibleDC(screen); if(memory==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            bitmap=CreateCompatibleBitmap(screen,width,height); if(bitmap==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            old=SelectObject(memory,bitmap);
            if(!BitBlt(memory,0,0,width,height,screen,left,top,0x40CC0020)) throw new Win32Exception(Marshal.GetLastWin32Error());
            BitmapSource source=Imaging.CreateBitmapSourceFromHBitmap(bitmap,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
            // Desktop GDI bitmaps have no meaningful alpha channel. Keep the
            // frame opaque while retaining four-byte BGR pixels for sampling.
            if(source.Format!=PixelFormats.Bgr32) source=new FormatConvertedBitmap(source,PixelFormats.Bgr32,null,0);
            source.Freeze(); return new ScreenFrame(source,left,top,width,height);
        }
        finally
        {
            if(old!=IntPtr.Zero && memory!=IntPtr.Zero) SelectObject(memory,old);
            if(bitmap!=IntPtr.Zero) DeleteObject(bitmap);
            if(memory!=IntPtr.Zero) DeleteDC(memory);
            if(screen!=IntPtr.Zero) ReleaseDC(IntPtr.Zero,screen);
            if(previous!=IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X,Y; }
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("gdi32.dll",SetLastError=true)] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll",SetLastError=true)] static extern IntPtr CreateCompatibleBitmap(IntPtr dc,int width,int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
    [DllImport("gdi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool BitBlt(IntPtr destination,int x,int y,int width,int height,IntPtr source,int sourceX,int sourceY,uint operation);
}
