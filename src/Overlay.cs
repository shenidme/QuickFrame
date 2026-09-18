using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Reuse a premultiplied DIB and DC. Moving a frame does not allocate a bitmap.
sealed class LayerSurface : IDisposable {
    IntPtr dc, dib, previous;
    Bitmap bitmap;
    public Graphics Graphics;
    public int Allocations { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public void Ensure(int width,int height) {
        if(Graphics!=null && width<=Width && height<=Height) return;
        Dispose(); Width=((width+127)/128)*128; Height=((height+127)/128)*128;
        try {
            dc=Native.CreateCompatibleDC(IntPtr.Zero);
            var info=new Native.BitmapHeader { Size=40,Width=Width,Height=-Height,Planes=1,Bits=32 };
            IntPtr bits; dib=Native.CreateDIBSection(dc,ref info,0,out bits,IntPtr.Zero,0);
            if(dc==IntPtr.Zero || dib==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            previous=Native.SelectObject(dc,dib);
            bitmap=new Bitmap(Width,Height,Width*4,PixelFormat.Format32bppPArgb,bits);
            Graphics=Graphics.FromImage(bitmap); Allocations++;
        } catch { Dispose(); throw; }
    }
    public void Present(IntPtr window,Rectangle bounds) {
        Graphics.Flush();
        var position=new Native.Point { X=bounds.X,Y=bounds.Y };
        var size=new Native.Size { X=bounds.Width,Y=bounds.Height };
        var origin=new Native.Point(); var blend=new Native.Blend { Alpha=255,Format=1 };
        if(!Native.UpdateLayeredWindow(window,IntPtr.Zero,ref position,ref size,dc,ref origin,0,ref blend,2))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() {
        if(Graphics!=null) { Graphics.Dispose(); Graphics=null; }
        if(bitmap!=null) { bitmap.Dispose(); bitmap=null; }
        if(previous!=IntPtr.Zero && dc!=IntPtr.Zero) Native.SelectObject(dc,previous);
        if(dib!=IntPtr.Zero) Native.DeleteObject(dib);
        if(dc!=IntPtr.Zero) Native.DeleteDC(dc);
        dc=dib=previous=IntPtr.Zero; Width=Height=0;
    }
}

class FocusShade : Form {
    string geometry="";
    public FocusShade() {
        FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true;
        StartPosition=FormStartPosition.Manual; BackColor=Color.Black; Opacity=0;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var c=base.CreateParams; c.ExStyle|=0x08000000|0x20|0x80000|0x80; return c; } }
    public static double Strength(long age,float fade) {
        double t=Math.Max(0,Math.Min(1,age/280.0)); return 0.46*t*t*(3-2*t)*fade;
    }
    public static Region Mask(Rectangle screen,IEnumerable<Rectangle> holes) {
        var region=new Region(new Rectangle(0,0,screen.Width,screen.Height));
        foreach(var rect in holes) { var hole=rect; hole.Offset(-screen.Left,-screen.Top); if(hole.Width>0 && hole.Height>0) region.Exclude(hole); }
        return region;
    }
    public void UpdateBand(Rectangle screen,Rectangle? area,List<Rectangle> excluded,double strength) {
        if(strength<0.002) { Hide(); return; }
        var key=new StringBuilder(screen.ToString()).Append(area.ToString());
        foreach(var rect in excluded) key.Append(rect.ToString());
        string next=key.ToString();
        if(next!=geometry) {
            Bounds=screen;
            Region region=Mask(screen,excluded);
            if(area.HasValue) { var local=area.Value; local.Offset(-screen.X,-screen.Y); region.Intersect(local); }
            Region old=Region; Region=region; if(old!=null) old.Dispose(); geometry=next;
        }
        Opacity=strength; if(!Visible) Show();
    }
}

// Disjoint regions prevent overlapping masks from doubling their darkness.
sealed class FocusGroup : IDisposable {
    public class Hole { public Rectangle Rect; public double Strength; }
    readonly List<FocusShade> bands=new List<FocusShade>();
    public bool Visible { get { return bands.Exists(b=>b.Visible); } }
    public static double HoleOpacity(double background,double strength) { return Math.Max(0,background-strength); }
    public void Update(Rectangle screen,List<Hole> holes) {
        holes.Sort((a,b)=>b.Strength.CompareTo(a.Strength));
        if(holes.Count==0 || holes[0].Strength<0.002) { Hide(); return; }
        double background=holes[0].Strength; int used=0;
        var excluded=new List<Rectangle>();
        foreach(var hole in holes) {
            double opacity=HoleOpacity(background,hole.Strength);
            if(opacity>=0.002) Get(used++).UpdateBand(screen,hole.Rect,excluded,opacity);
            excluded.Add(hole.Rect);
        }
        Get(used++).UpdateBand(screen,null,excluded,background);
        for(int i=used;i<bands.Count;i++) bands[i].Hide();
    }
    FocusShade Get(int index) { while(bands.Count<=index) bands.Add(new FocusShade()); return bands[index]; }
    public void Hide() { foreach(var band in bands) band.Hide(); }
    public void Dispose() { foreach(var band in bands) band.Dispose(); bands.Clear(); }
}

class Overlay : Form {
    public readonly List<Frame> Frames=new List<Frame>();
    public Rectangle? Preview { get; set; }
    public Color PreviewColor;
    public FrameStyle PreviewStyle;
    public long PreviewBorn,Now;
    public bool FocusEnabled,CurrentScreen;
    public int Dim=46,Speed=9;
    readonly FocusGroup shade=new FocusGroup();
    readonly LayerSurface surface=new LayerSurface();
    public bool ShadeVisible { get { return shade.Visible; } }
    public int Allocations { get { return surface.Allocations; } }
    public Overlay() { FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true; StartPosition=FormStartPosition.Manual; }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var c=base.CreateParams; c.ExStyle|=0x08000000|0x20|0x80000|0x80; return c; } }
    public void RefreshFrames() {
        if(Frames.Count==0 && !Preview.HasValue) { shade.Hide(); Hide(); surface.Dispose(); return; }
        Rectangle bounds=Preview.HasValue?Preview.Value:Frames[0].Rect;
        Rectangle focusScreen=CurrentScreen?Screen.FromRectangle(Preview.HasValue?Preview.Value:Frames[Frames.Count-1].Rect).Bounds:SystemInformation.VirtualScreen;
        var holes=new List<FocusGroup.Hole>();
        foreach(var frame in Frames) {
            bounds=Rectangle.Union(bounds,frame.Rect);
            holes.Add(new FocusGroup.Hole { Rect=frame.Rect,Strength=FocusShade.Strength(Now-frame.Born,frame.Opacity(Now))*Dim/46.0 });
        }
        if(Preview.HasValue) holes.Add(new FocusGroup.Hole { Rect=Preview.Value,Strength=FocusShade.Strength(Now-PreviewBorn,1)*Dim/46.0 });
        if(FocusEnabled) shade.Update(focusScreen,holes); else shade.Hide();
        bounds.Inflate(90,90); bounds=Rectangle.Intersect(bounds,SystemInformation.VirtualScreen);
        if(bounds.Width<=0 || bounds.Height<=0) { Hide(); return; }
        surface.Ensure(bounds.Width,bounds.Height);
        Graphics g=surface.Graphics; g.Clear(Color.Transparent);
        foreach(var frame in Frames) Draw(g,frame.Rect,frame.Color,frame.Opacity(Now),bounds.Location,frame.Style,frame.Born,Math.Max(0,1-(frame.Until-Now)/(float)frame.FadeDuration));
        if(Preview.HasValue) Draw(g,Preview.Value,PreviewColor,1,bounds.Location,PreviewStyle,PreviewBorn,0);
        if(!Visible) Show();
        // Mask bands may have just been shown above the border.
        if(shade.Visible) Native.SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x13);
        surface.Present(Handle,bounds);
    }
    void Draw(Graphics g,Rectangle r,Color color,float opacity,Point origin,FrameStyle style,long born,float exit) {
        r.Offset(-origin.X,-origin.Y); double phase=-Now/1000.0/Math.Max(1,Speed);
        if(style==FrameStyle.NightCity) Rainbow.NightCity(g,r,Rainbow.Deploy(Now-born),exit,opacity,phase);
        else Rainbow.Glow(g,r,phase,opacity,color,style);
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }
    protected override void Dispose(bool disposing) { if(disposing) { shade.Dispose(); surface.Dispose(); } base.Dispose(disposing); }
}
