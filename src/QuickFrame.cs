using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

static class Native {
    public delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Mouse { public Point Point; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll", CharSet=CharSet.Auto)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window,int id);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct Size { public int X,Y; }
    [StructLayout(LayoutKind.Sequential,Pack=1)] public struct Blend { public byte Op,Flags,Alpha,Format; }
    [DllImport("user32.dll",SetLastError=true)] public static extern bool UpdateLayeredWindow(IntPtr window,IntPtr dst,ref Point position,ref Size size,IntPtr source,ref Point origin,uint key,ref Blend blend,uint flags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint id);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder value,int length,out int needed);
    public static string DesktopName() {
        var name=new StringBuilder(512); int needed;
        if(!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()),2,name,1024,out needed))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }
}

// Pure gesture state: suppress the original right press; replay only a click.
class Gesture {
    public bool Pending, Dragging;
    public Point Start, Current;
    public void Down(Point p) { Pending=true; Dragging=false; Start=Current=p; }
    public void Move(Point p) {
        if(!Pending) return;
        Current=p;
        if(Math.Abs((long)p.X-Start.X)>=8 || Math.Abs((long)p.Y-Start.Y)>=8) Dragging=true;
    }
    public Rectangle Rect { get { return Rectangle.FromLTRB(Math.Min(Start.X,Current.X),Math.Min(Start.Y,Current.Y),Math.Max(Start.X,Current.X),Math.Max(Start.Y,Current.Y)); } }
    public bool Up(Point p) { Move(p); bool click=!Dragging; Pending=false; Dragging=false; return click; }
}

enum FrameStyle { Cyber, Rainbow, Classic, Aurora, Amber, Mint, NightCity }

// No painting or synchronous UI calls are allowed on the low-level hook thread.
class MouseInput : IDisposable {
    public class Stroke { public Rectangle Rect; public long Born,Released; }
    public class Snapshot { public Rectangle? Preview; public long Born; public List<Stroke> Completed; }
    readonly object gate=new object();
    readonly Gesture gesture=new Gesture();
    readonly List<Stroke> completed=new List<Stroke>();
    readonly Stopwatch clock;
    readonly System.Threading.Thread thread;
    readonly System.Threading.ManualResetEvent ready=new System.Threading.ManualResetEvent(false);
    readonly Native.HookProc callback;
    IntPtr hook;
    Exception startupError;
    long born,lastInput,lastInstall;
    Point lastPoint;
    bool enabled=true,discarded;
    volatile bool stopping,repair;
    public volatile bool Installed;
    public long SeenEvents;
    public long Heartbeats,InstallCount;
    Control dispatcher;
    public MouseInput(Stopwatch time) {
        clock=time; callback=OnMouse;
        thread=new System.Threading.Thread(Run) { IsBackground=true,Name="QuickFrame mouse input" };
        thread.SetApartmentState(System.Threading.ApartmentState.STA); thread.Start();
        if(!ready.WaitOne(5000)) throw new Exception("鼠标监听线程启动超时。");
        if(startupError!=null) throw startupError;
    }
    void Install() {
        if(hook!=IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        hook=Native.SetWindowsHookEx(14,callback,Native.GetModuleHandle(null),0);
        Installed=hook!=IntPtr.Zero; lastInstall=clock.ElapsedMilliseconds;
        if(!Installed) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        System.Threading.Interlocked.Increment(ref InstallCount);
        Native.Point point;
        lock(gate) { lastInput=lastInstall; if(Native.GetCursorPos(out point)) lastPoint=new Point(point.X,point.Y); }
    }
    void Run() {
        try {
            dispatcher=new Control(); var dispatcherHandle=dispatcher.Handle;
            Install();
            using(var watchdog=new Timer { Interval=250 }) {
                watchdog.Tick+=delegate {
                    System.Threading.Interlocked.Increment(ref Heartbeats);
                    if(stopping) { Application.ExitThread(); return; }
                    bool restart=repair; repair=false;
                    Native.Point cursor;
                    lock(gate) {
                        // The right-down event is suppressed, so polling Windows key state is not authoritative.
                        // An active hold ends only on right-up, explicit cancel, or manual repair.
                        if(!gesture.Pending && Native.GetCursorPos(out cursor) && clock.ElapsedMilliseconds-lastInput>1500 && (cursor.X!=lastPoint.X || cursor.Y!=lastPoint.Y)) restart=true;
                        if(restart) { gesture.Pending=false; gesture.Dragging=false; discarded=true; }
                    }
                    if((restart || !Installed) && clock.ElapsedMilliseconds-lastInstall>1000) {
                        try { Install(); } catch { Installed=false; }
                    } else if(restart) repair=true;
                };
                watchdog.Start(); ready.Set(); Application.Run();
            }
        } catch(Exception e) { startupError=e; ready.Set(); }
        finally { if(hook!=IntPtr.Zero) Native.UnhookWindowsHookEx(hook); Installed=false; if(dispatcher!=null) dispatcher.Dispose(); }
    }
    public void SetEnabled(bool value) { lock(gate) { enabled=value; discarded=true; completed.Clear(); } }
    public void Cancel() { lock(gate) { discarded=true; completed.Clear(); } }
    public void Repair() { Cancel(); repair=true; }
    public Snapshot Read() {
        lock(gate) {
            var result=new Snapshot { Preview=gesture.Pending&&gesture.Dragging&&!discarded?(Rectangle?)gesture.Rect:null,Born=born,Completed=new List<Stroke>(completed) };
            completed.Clear(); return result;
        }
    }
    IntPtr OnMouse(int code,IntPtr message,IntPtr data) {
        if(code<0) return Native.CallNextHookEx(hook,code,message,data);
        System.Threading.Interlocked.Increment(ref SeenEvents);
        var m=(Native.Mouse)Marshal.PtrToStructure(data,typeof(Native.Mouse));
        if((m.Flags&1)!=0) return Native.CallNextHookEx(hook,code,message,data);
        int action=ProcessMouse(message.ToInt32(),new Point(m.Point.X,m.Point.Y));
        if((action&2)!=0) dispatcher.BeginInvoke(new Action(delegate { Native.mouse_event(0x0008|0x0010,0,0,0,UIntPtr.Zero); }));
        return (action&1)!=0?(IntPtr)1:Native.CallNextHookEx(hook,code,message,data);
    }
    int ProcessMouse(int msg,Point point) {
        bool suppress=false,replay=false;
        lock(gate) {
            lastInput=clock.ElapsedMilliseconds; lastPoint=point;
            if(msg==0x204 && enabled) { gesture.Down(lastPoint); born=lastInput; discarded=false; suppress=true; }
            else if(msg==0x200 && gesture.Pending) {
                bool wasDragging=gesture.Dragging; gesture.Move(lastPoint);
                if(!wasDragging && gesture.Dragging) born=lastInput;
            } else if(msg==0x205 && gesture.Pending) {
                bool click=gesture.Up(lastPoint); suppress=true;
                if(!discarded) {
                    if(click) replay=true;
                    else completed.Add(new Stroke { Rect=gesture.Rect,Born=born,Released=lastInput });
                }
            }
        }
        return (suppress?1:0)|(replay?2:0);
    }
    public void TestHeldDrag() {
        // Exercise the real input state machine and watchdog without injecting desktop clicks.
        dispatcher.Invoke(new Action(delegate { ProcessMouse(0x204,new Point(100,100)); ProcessMouse(0x200,new Point(240,220)); }));
        System.Threading.Thread.Sleep(2400);
        Snapshot held=Read();
        if(!held.Preview.HasValue || held.Completed.Count!=0) throw new Exception("Stationary held drag was cancelled or timed out");
        long releaseTime=clock.ElapsedMilliseconds;
        dispatcher.Invoke(new Action(delegate { ProcessMouse(0x205,new Point(240,220)); }));
        Snapshot released=Read();
        if(released.Preview.HasValue || released.Completed.Count!=1 || released.Completed[0].Released<releaseTime) throw new Exception("Release must start the lifetime exactly once");
    }
    public void Dispose() { stopping=true; if(thread.Join(2000)) ready.Dispose(); }
}

class Frame {
    public const int FadeMilliseconds=500;
    public Rectangle Rect; public Color Color; public long Until, Born; public FrameStyle Style;
    public float Opacity(long now) {
        float t=Math.Max(0,Math.Min(1,(Until-now)/(float)FadeMilliseconds));
        return t*t*(3-2*t);
    }
}

static class Rainbow {
    public static readonly string[] StyleNames={"赛博霓虹","彩虹辉光","经典彩虹（无辉光）","冰蓝极光","琥珀全息","薄荷流光","夜城协议 · 2077"};
    static readonly Color[][] Palettes={
        new Color[]{Color.FromArgb(0,245,255),Color.FromArgb(38,121,255),Color.FromArgb(167,52,255),Color.FromArgb(255,24,182),Color.FromArgb(0,245,255)},
        new Color[]{Color.Red,Color.Yellow,Color.Lime,Color.Cyan,Color.Blue,Color.Magenta,Color.Red},
        new Color[]{Color.Red,Color.Yellow,Color.Lime,Color.Cyan,Color.Blue,Color.Magenta,Color.Red},
        new Color[]{Color.FromArgb(63,136,255),Color.FromArgb(121,248,255),Color.FromArgb(215,252,255),Color.FromArgb(152,143,255),Color.FromArgb(63,136,255)},
        new Color[]{Color.FromArgb(255,103,20),Color.FromArgb(255,189,43),Color.FromArgb(255,244,155),Color.FromArgb(255,150,22),Color.FromArgb(255,103,20)},
        new Color[]{Color.FromArgb(38,224,168),Color.FromArgb(145,255,218),Color.FromArgb(79,222,242),Color.FromArgb(191,252,210),Color.FromArgb(38,224,168)},
        new Color[]{Color.FromArgb(252,238,10),Color.FromArgb(255,187,0),Color.FromArgb(25,245,255),Color.FromArgb(255,45,80),Color.FromArgb(252,238,10)}
    };
    public static Color Hue(double value,FrameStyle style=FrameStyle.Cyber) {
        Color[] palette=Palettes[(int)style];
        double cycle=value-Math.Floor(value),h=cycle*(palette.Length-1);
        int sector=(int)h; double t=h-sector; t=t*t*(3-2*t);
        Color a=palette[sector],b=palette[sector+1];
        double pulse=style==FrameStyle.Cyber?Math.Pow(Math.Max(0,Math.Cos(cycle*Math.PI*4)),48)*0.65:0;
        return Color.FromArgb((int)((a.R+(b.R-a.R)*t)*(1-pulse)+235*pulse),(int)((a.G+(b.G-a.G)*t)*(1-pulse)+255*pulse),(int)((a.B+(b.B-a.B)*t)*(1-pulse)+255*pulse));
    }
    public static void Draw(Graphics g,Rectangle r,float width,double phase,float opacity=1,Color solid=default(Color),FrameStyle style=FrameStyle.Cyber) {
        float w=Math.Max(1,r.Width),h=Math.Max(1,r.Height),perimeter=2*(w+h),offset=0;
        PointF[] points={new PointF(r.Left,r.Top),new PointF(r.Left+w,r.Top),new PointF(r.Left+w,r.Top+h),new PointF(r.Left,r.Top+h),new PointF(r.Left,r.Top)};
        for(int edge=0;edge<4;edge++) {
            float length=edge%2==0?w:h;
            using(var brush=new LinearGradientBrush(points[edge],points[edge+1],Color.Red,Color.Blue)) {
                var blend=new ColorBlend(33);
                for(int i=0;i<33;i++) { blend.Positions[i]=i/32f; blend.Colors[i]=Color.FromArgb((int)(255*opacity),solid.IsEmpty?Hue(phase+(offset+length*i/32f)/perimeter,style):solid); }
                brush.InterpolationColors=blend; brush.WrapMode=WrapMode.TileFlipXY;
                using(var pen=new Pen(brush,width)) { pen.StartCap=LineCap.Square; pen.EndCap=LineCap.Square; g.DrawLine(pen,points[edge],points[edge+1]); }
            }
            offset+=length;
        }
    }
    public static void Glow(Graphics g,Rectangle r,double phase,float opacity,Color solid,FrameStyle style=FrameStyle.Cyber) {
        if(style==FrameStyle.NightCity) { NightCity(g,r,1,0,opacity,phase); return; }
        g.SmoothingMode=SmoothingMode.AntiAlias;
        if(style==FrameStyle.Classic) { Draw(g,r,5,phase,opacity,solid,style); return; }
        Draw(g,r,23,phase,opacity*0.018f,solid,style);
        Draw(g,r,19,phase,opacity*0.028f,solid,style);
        Draw(g,r,15,phase,opacity*0.045f,solid,style);
        Draw(g,r,11,phase,opacity*0.075f,solid,style);
        Draw(g,r,8,phase,opacity*0.14f,solid,style);
        Draw(g,r,5,phase,opacity,solid,style);
        Draw(g,r,1,phase,opacity*0.30f,Color.FromArgb(214,252,255),style);
        if(style==FrameStyle.Aurora && r.Width>=32 && r.Height>=32) {
            var inset=r; inset.Inflate(-5,-5);
            Draw(g,inset,1,phase+0.2,opacity*0.32f,solid,style);
        }
        if((style==FrameStyle.Cyber || style==FrameStyle.Amber) && r.Width>=32 && r.Height>=32) {
            var inner=r; inner.Inflate(-6,-6);
            using(var detail=new Pen(Color.FromArgb((int)(95*opacity),solid.IsEmpty?Hue(0,style):solid),1)) {
                detail.DashPattern=style==FrameStyle.Amber?new float[]{2,8}:new float[]{9,5,2,5};
                detail.DashOffset=(float)(phase*20);
                g.DrawRectangle(detail,inner);
            }
            var corners=r; corners.Inflate(5,5);
            int arm=Math.Min(23,Math.Min(r.Width,r.Height)/4);
            Point[][] brackets={
                new Point[]{new Point(corners.Left,corners.Top+arm),new Point(corners.Left,corners.Top),new Point(corners.Left+arm,corners.Top)},
                new Point[]{new Point(corners.Right-arm,corners.Top),new Point(corners.Right,corners.Top),new Point(corners.Right,corners.Top+arm)},
                new Point[]{new Point(corners.Right,corners.Bottom-arm),new Point(corners.Right,corners.Bottom),new Point(corners.Right-arm,corners.Bottom)},
                new Point[]{new Point(corners.Left+arm,corners.Bottom),new Point(corners.Left,corners.Bottom),new Point(corners.Left,corners.Bottom-arm)}
            };
            for(int i=0;i<4;i++) {
                Color accent=solid.IsEmpty?(style==FrameStyle.Amber?Color.FromArgb(255,210,82):(i%2==0?Color.FromArgb(55,248,255):Color.FromArgb(255,65,198))):solid;
                using(var halo=new Pen(Color.FromArgb((int)(40*opacity),accent),6)) g.DrawLines(halo,brackets[i]);
                using(var edge=new Pen(Color.FromArgb((int)(230*opacity),accent),2)) g.DrawLines(edge,brackets[i]);
            }
        }
    }
    public static float Deploy(long age) {
        float t=Math.Max(0,Math.Min(1,age/320f)); return 1-(1-t)*(1-t)*(1-t);
    }
    public static float Glitch(float exit) {
        // Two brief dropouts during the existing half-second fade, on the border only.
        return (exit>0.16f && exit<0.28f)||(exit>0.52f && exit<0.66f)?0.20f:1;
    }
    public static void NightCity(Graphics g,Rectangle target,float deploy,float exit,float opacity,double phase) {
        if(opacity<=0) return;
        float scale=0.64f+0.36f*deploy;
        float w=Math.Max(1,target.Width*scale),h=Math.Max(1,target.Height*scale);
        var r=new RectangleF(target.Left+(target.Width-w)/2,target.Top+(target.Height-h)/2,w,h);
        float cut=Math.Min(12,Math.Min(w,h)/4);
        PointF[] shape={new PointF(r.Left+cut,r.Top),new PointF(r.Right,r.Top),new PointF(r.Right,r.Bottom-cut),new PointF(r.Right-cut,r.Bottom),new PointF(r.Left,r.Bottom),new PointF(r.Left,r.Top+cut),new PointF(r.Left+cut,r.Top)};
        float alpha=opacity*Glitch(exit);
        Color yellow=Color.FromArgb(252,238,10),cyan=Color.FromArgb(29,244,255),red=Color.FromArgb(255,47,87);
        g.SmoothingMode=SmoothingMode.AntiAlias;
        using(var glow=new Pen(Color.FromArgb((int)(22*alpha),yellow),19)) g.DrawLines(glow,shape);
        using(var glow=new Pen(Color.FromArgb((int)(42*alpha),yellow),12)) g.DrawLines(glow,shape);
        using(var dark=new Pen(Color.FromArgb((int)(210*alpha),Color.FromArgb(12,14,18)),8)) g.DrawLines(dark,shape);
        using(var line=new Pen(Color.FromArgb((int)(255*alpha),yellow),4)) g.DrawLines(line,shape);
        if(w<40 || h<30) return;
        using(var accent=new Pen(Color.FromArgb((int)(235*alpha),cyan),2)) {
            g.DrawLine(accent,r.Left+cut+8,r.Top+5,r.Right-12,r.Top+5);
            g.DrawLine(accent,r.Right-5,r.Top+12,r.Right-5,r.Bottom-cut-6);
        }
        // Asymmetric bars and small edge ticks give a HUD silhouette without filling the selected content.
        using(var bar=new Pen(Color.FromArgb((int)(255*alpha),red),5)) g.DrawLine(bar,r.Left,r.Top+cut+5,r.Left,r.Top+cut+Math.Min(27,h/3));
        using(var tick=new Pen(Color.FromArgb((int)(200*alpha),yellow),2)) {
            for(int i=0;i<3;i++) g.DrawLine(tick,r.Right-10-i*8,r.Top-4,r.Right-14-i*8,r.Top-9);
        }
        float scan=(float)((-phase*3)%1); if(scan<0) scan+=1;
        if(deploy<1) scan=deploy;
        float sx=r.Left+cut+(w-cut-12)*scan;
        using(var tracer=new Pen(Color.FromArgb((int)(255*alpha),Color.White),3)) g.DrawLine(tracer,sx,r.Top,sx+Math.Min(12,w/5),r.Top);
        NightCityArmor(g,r,deploy,exit,opacity,phase);
        if(exit>0) {
            // Color-separated horizontal fragments drift outward during shutdown.
            float drift=3+exit*8;
            var state=g.Save();
            g.SetClip(new RectangleF(r.Left-20,r.Top+h*0.25f,r.Width+40,Math.Max(3,h*0.10f)));
            g.TranslateTransform(drift,0);
            using(var glitch=new Pen(Color.FromArgb((int)(180*opacity),cyan),3)) g.DrawLines(glitch,shape);
            g.Restore(state);
            state=g.Save();
            g.SetClip(new RectangleF(r.Left-20,r.Top+h*0.68f,r.Width+40,Math.Max(3,h*0.08f)));
            g.TranslateTransform(-drift,0);
            using(var glitch=new Pen(Color.FromArgb((int)(180*opacity),red),3)) g.DrawLines(glitch,shape);
            g.Restore(state);
        }
    }
    static PointF Rim(RectangleF r,float t) {
        t=t-(float)Math.Floor(t);
        float distance=t*2*(r.Width+r.Height);
        if(distance<r.Width) return new PointF(r.Left+distance,r.Top);
        distance-=r.Width;
        if(distance<r.Height) return new PointF(r.Right,r.Top+distance);
        distance-=r.Height;
        if(distance<r.Width) return new PointF(r.Right-distance,r.Bottom);
        return new PointF(r.Left,r.Bottom-(distance-r.Width));
    }
    static void NightCityArmor(Graphics g,RectangleF r,float deploy,float exit,float opacity,double phase) {
        Color yellow=Color.FromArgb(252,238,10),cyan=Color.FromArgb(29,244,255),red=Color.FromArgb(255,47,87);
        float alpha=opacity*Glitch(exit),gap=10+(1-deploy)*30+exit*exit*24;
        float arm=Math.Min(44,Math.Min(r.Width,r.Height)*0.28f);
        for(int corner=0;corner<4;corner++) {
            int dx=corner==0||corner==3?-1:1,dy=corner<2?-1:1;
            float x=(dx<0?r.Left:r.Right)+dx*gap,y=(dy<0?r.Top:r.Bottom)+dy*gap;
            var points=new PointF[]{new PointF(x,y-dy*arm),new PointF(x,y-dy*8),new PointF(x-dx*8,y),new PointF(x-dx*arm,y)};
            Color tint=corner%2==0?cyan:yellow;
            using(var pen=new Pen(Color.FromArgb((int)(25*opacity),tint),12)) g.DrawLines(pen,points);
            using(var pen=new Pen(Color.FromArgb((int)(85*opacity),tint),6)) g.DrawLines(pen,points);
            using(var pen=new Pen(Color.FromArgb((int)(250*opacity),tint),2)) g.DrawLines(pen,points);
            using(var square=new SolidBrush(Color.FromArgb((int)(240*opacity),red))) g.FillRectangle(square,x-dx*arm-2,y-2,4,4);
        }
        // Bright packets circulate on a rail; shutdown sends each packet away from the content.
        var rail=r; rail.Inflate(5,5);
        for(int i=0;i<14;i++) {
            float t=i/14f-(float)phase*1.7f;
            var p=Rim(rail,t); var tail=Rim(rail,t-0.007f);
            float nx=(p.X-(r.Left+r.Width/2))/(r.Width/2+5),ny=(p.Y-(r.Top+r.Height/2))/(r.Height/2+5);
            float fly=exit*exit*(24+(i%4)*9);
            p.X+=nx*fly; p.Y+=ny*fly; tail.X+=nx*fly; tail.Y+=ny*fly;
            Color tint=i%3==0?red:cyan;
            using(var pen=new Pen(Color.FromArgb((int)(190*opacity),tint),2)) g.DrawLine(pen,tail,p);
            using(var halo=new SolidBrush(Color.FromArgb((int)(32*opacity),tint))) g.FillEllipse(halo,p.X-5,p.Y-5,10,10);
            using(var core=new SolidBrush(Color.FromArgb((int)(245*opacity),Color.White))) g.FillRectangle(core,p.X-1,p.Y-1,2,2);
        }
        if(r.Width<180 || r.Height<90) return;
        float drift=exit*exit*22;
        float panelWidth=Math.Min(126,r.Width*0.34f);
        var state=g.Save(); g.TranslateTransform(-drift,-drift);
        PointF[] top={new PointF(r.Left+17,r.Top-5),new PointF(r.Left+17,r.Top-24),new PointF(r.Left+panelWidth,r.Top-24),new PointF(r.Left+panelWidth+14,r.Top-10),new PointF(r.Left+panelWidth+14,r.Top-5)};
        using(var fill=new SolidBrush(Color.FromArgb((int)(225*alpha),yellow))) g.FillPolygon(fill,top);
        using(var font=new Font(FontFamily.GenericMonospace,8,FontStyle.Bold)) using(var ink=new SolidBrush(Color.FromArgb((int)(255*alpha),Color.FromArgb(9,13,17))))
            g.DrawString(exit>0?"SIGNAL // LOST":deploy<0.99f?"SCAN // INIT":"NC // LOCKED",font,ink,r.Left+23,r.Top-22);
        g.Restore(state);
        state=g.Save(); g.TranslateTransform(drift,drift);
        float bx=r.Right-panelWidth-10,by=r.Bottom+6;
        using(var fill=new SolidBrush(Color.FromArgb((int)(220*alpha),Color.FromArgb(9,13,17)))) g.FillRectangle(fill,bx,by,panelWidth+10,18);
        using(var edge=new Pen(Color.FromArgb((int)(210*alpha),red),2)) g.DrawLine(edge,bx,by+18,r.Right-4,by+18);
        using(var font=new Font(FontFamily.GenericMonospace,7,FontStyle.Bold)) using(var ink=new SolidBrush(Color.FromArgb((int)(230*alpha),cyan)))
            g.DrawString("NET / "+((int)(-phase*900)%1000).ToString("D3")+" : ACTIVE",font,ink,bx+4,by+2);
        g.Restore(state);
        using(var bar=new Pen(Color.FromArgb((int)(235*alpha),yellow),3)) {
            for(int i=0;i<6;i++) {
                float x=r.Left+24+i*10;
                g.DrawLine(bar,x,r.Bottom+7,x+5,r.Bottom+14);
            }
        }
        using(var ticks=new Pen(Color.FromArgb((int)(160*alpha),cyan),1)) {
            for(int i=0;i<9;i++) {
                float y=r.Top+25+i*(r.Height-50)/8;
                g.DrawLine(ticks,r.Right+10,y,r.Right+(i%4==0?23:16),y);
            }
        }
        // A staggered off-grid shard rather than a full-screen flash.
        if(exit>0) {
            using(var shard=new SolidBrush(Color.FromArgb((int)(210*opacity),red)))
                g.FillRectangle(shard,r.Left-10-drift,r.Top+r.Height*0.58f,18,3);
            using(var shard=new SolidBrush(Color.FromArgb((int)(230*opacity),cyan)))
                g.FillRectangle(shard,r.Right-18+drift,r.Top+r.Height*0.32f,27,2);
        }
    }
    public static Icon TrayIcon() {
        using(var bitmap=new Bitmap(32,32)) {
            using(var g=Graphics.FromImage(bitmap)) Draw(g,new Rectangle(5,5,22,22),5,0);
            IntPtr handle=bitmap.GetHicon();
            try { using(var icon=Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
            finally { Native.DestroyIcon(handle); }
        }
    }
}

class FocusShade : Form {
    string geometry="";
    public FocusShade() {
        FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true;
        StartPosition=FormStartPosition.Manual; BackColor=Color.Black; Opacity=0;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var c=base.CreateParams; c.ExStyle|=0x08000000|0x00000020|0x00080000|0x00000080; return c; } }
    public static double Strength(long age,float fade) {
        double t=Math.Max(0,Math.Min(1,age/280.0));
        return 0.46*t*t*(3-2*t)*fade;
    }
    public static Region Mask(Rectangle screen,IEnumerable<Rectangle> holes) {
        var region=new Region(new Rectangle(0,0,screen.Width,screen.Height));
        foreach(var rect in holes) {
            var hole=rect; hole.Offset(-screen.Left,-screen.Top);
            if(hole.Width>0 && hole.Height>0) region.Exclude(hole);
        }
        return region;
    }
    public void UpdateShade(List<Rectangle> holes,double strength) {
        if(strength<=0.001 || holes.Count==0) { Hide(); return; }
        Rectangle screen=SystemInformation.VirtualScreen;
        var key=new StringBuilder(screen.ToString());
        foreach(var hole in holes) key.Append(hole.ToString());
        string next=key.ToString();
        if(next!=geometry) {
            Bounds=screen;
            Region old=Region;
            Region=Mask(screen,holes);
            if(old!=null) old.Dispose();
            geometry=next;
        }
        Opacity=strength;
        if(!Visible) Show();
    }
}

class Overlay : Form {
    public readonly List<Frame> Frames=new List<Frame>();
    public Rectangle? Preview { get; set; }
    public Color PreviewColor=Color.Empty;
    public FrameStyle PreviewStyle;
    public long PreviewBorn;
    readonly Stopwatch animation=Stopwatch.StartNew();
    public long Now;
    public bool FocusEnabled=true;
    readonly FocusShade shade=new FocusShade();
    public bool ShadeVisible { get { return shade.Visible; } }
    public Overlay() {
        FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true;
        StartPosition=FormStartPosition.Manual;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var c=base.CreateParams; c.ExStyle|=0x08000000|0x00000020|0x00080000|0x00000080; return c; } }
    public void RefreshFrames() {
        if(Frames.Count==0 && !Preview.HasValue) { shade.Hide(); Hide(); return; }
        var holes=new List<Rectangle>(); double shadeStrength=0;
        foreach(var frame in Frames) {
            holes.Add(frame.Rect);
            shadeStrength=Math.Max(shadeStrength,FocusShade.Strength(Now-frame.Born,frame.Opacity(Now)));
        }
        if(Preview.HasValue) {
            holes.Add(Preview.Value);
            shadeStrength=Math.Max(shadeStrength,FocusShade.Strength(Now-PreviewBorn,1));
        }
        bool wasVisible=shade.Visible;
        shade.UpdateShade(holes,FocusEnabled?shadeStrength:0);
        if(!wasVisible && shade.Visible && Visible) Native.SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x0013);
        Rectangle bounds=Preview.HasValue?Preview.Value:Frames[0].Rect;
        foreach(var frame in Frames) bounds=Rectangle.Union(bounds,frame.Rect);
        bounds.Inflate(80,80);
        bounds=Rectangle.Intersect(bounds,SystemInformation.VirtualScreen);
        if(bounds.Width<=0 || bounds.Height<=0) { Hide(); return; }
        using(var bitmap=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppArgb)) {
            using(var g=Graphics.FromImage(bitmap)) {
                g.Clear(Color.Transparent);
                foreach(var frame in Frames) Draw(g,frame.Rect,frame.Color,frame.Opacity(Now),bounds.Location,frame.Style,frame.Born,Math.Max(0,1-(frame.Until-Now)/(float)Frame.FadeMilliseconds));
                if(Preview.HasValue) Draw(g,Preview.Value,PreviewColor,1,bounds.Location,PreviewStyle,PreviewBorn,0);
            }
            if(!Visible) Show();
            IntPtr dc=Native.CreateCompatibleDC(IntPtr.Zero),image=IntPtr.Zero,old=IntPtr.Zero;
            try {
                image=bitmap.GetHbitmap(Color.FromArgb(0)); old=Native.SelectObject(dc,image);
                var position=new Native.Point { X=bounds.Left,Y=bounds.Top };
                var size=new Native.Size { X=bounds.Width,Y=bounds.Height };
                var origin=new Native.Point(); var blend=new Native.Blend { Alpha=255,Format=1 };
                if(!Native.UpdateLayeredWindow(Handle,IntPtr.Zero,ref position,ref size,dc,ref origin,0,ref blend,2))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            } finally {
                if(old!=IntPtr.Zero) Native.SelectObject(dc,old);
                if(image!=IntPtr.Zero) Native.DeleteObject(image);
                if(dc!=IntPtr.Zero) Native.DeleteDC(dc);
            }
        }
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }
    protected override void Dispose(bool disposing) { if(disposing) shade.Dispose(); base.Dispose(disposing); }
    void Draw(Graphics g,Rectangle r,Color color,float opacity,Point origin,FrameStyle style,long born,float exit) {
        r.Offset(-origin.X,-origin.Y);
        if(style==FrameStyle.NightCity) Rainbow.NightCity(g,r,Rainbow.Deploy(Now-born),exit,opacity,-animation.Elapsed.TotalSeconds/9.0);
        else Rainbow.Glow(g,r,-animation.Elapsed.TotalSeconds/9.0,opacity,color,style);
    }
}

class SavedSettings {
    public FrameStyle Style=FrameStyle.NightCity;
    public Color Color=Color.Empty;
    public int Duration=1500;
    public bool Enabled=true,Focus=true;
    public static SavedSettings Load(string path) {
        var result=new SavedSettings();
        if(!File.Exists(path)) return result;
        foreach(string line in File.ReadAllLines(path)) {
            int index=line.IndexOf('='); if(index<0) continue;
            string key=line.Substring(0,index).Trim(),value=line.Substring(index+1).Trim();
            int number; bool flag; FrameStyle style;
            if(key=="Style" && Enum.TryParse<FrameStyle>(value,out style) && Enum.IsDefined(typeof(FrameStyle),style)) result.Style=style;
            if(key=="Color" && int.TryParse(value,out number)) {
                foreach(Color color in new Color[]{Color.OrangeRed,Color.DeepSkyBlue,Color.LimeGreen,Color.Gold,Color.MediumPurple})
                    if(number==color.ToArgb()) result.Color=color;
            }
            if(key=="Duration" && int.TryParse(value,out number) && Array.IndexOf(new int[]{0,1000,1500,2000,3000},number)>=0) result.Duration=number;
            if(key=="Enabled" && bool.TryParse(value,out flag)) result.Enabled=flag;
            if(key=="Focus" && bool.TryParse(value,out flag)) result.Focus=flag;
        }
        if(!result.Color.IsEmpty) result.Style=FrameStyle.Rainbow;
        return result;
    }
    public void Save(string path) {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            File.WriteAllLines(temp,new string[]{"Version=1","Style="+Style,"Color="+(Color.IsEmpty?0:Color.ToArgb()),"Duration="+Duration,"Enabled="+Enabled,"Focus="+Focus},Encoding.UTF8);
            if(File.Exists(path)) File.Replace(temp,path,path+".bak");
            else File.Move(temp,path);
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }
}

class Controller : Form {
    readonly Overlay overlay=new Overlay();
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly Timer timer=new Timer();
    readonly NotifyIcon tray=new NotifyIcon();
    readonly MouseInput input;
    bool enabled=true;
    Color color=Color.Empty;
    FrameStyle style=FrameStyle.NightCity;
    readonly Icon trayIcon=Rainbow.TrayIcon();
    int duration=1500;
    readonly ToolStripMenuItem toggle=new ToolStripMenuItem("暂停画框");
    readonly string settingsPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings.ini");
    readonly bool persistSettings;
    bool initialized,saveErrorShown;
    public Controller(bool smoke) {
        persistSettings=!smoke;
        if(persistSettings) {
            try {
                var saved=SavedSettings.Load(settingsPath);
                style=saved.Style; color=saved.Color; duration=saved.Duration; enabled=saved.Enabled; overlay.FocusEnabled=saved.Focus;
            } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
        Text="QuickFrame · 屏幕画框";
        ShowInTaskbar=false;
        var handle=Handle;
        var menu=new ContextMenuStrip();
        menu.Items.Add("QuickFrame · 右键拖动画框").Enabled=false;
        menu.Items.Add(toggle); toggle.Click+=delegate { Toggle(); };
        var styles=new ToolStripMenuItem("外观预设");
        foreach(FrameStyle value in Enum.GetValues(typeof(FrameStyle))) {
            FrameStyle selected=value; var item=new ToolStripMenuItem(Rainbow.StyleNames[(int)value]); item.Checked=value==style && color.IsEmpty;
            item.Click+=delegate { style=selected; color=Color.Empty; foreach(ToolStripItem sibling in styles.DropDownItems) { var option=sibling as ToolStripMenuItem; if(option!=null) option.Checked=option==item; } SaveSettings(); };
            styles.DropDownItems.Add(item);
        }
        menu.Items.Add(styles);
        styles.DropDownItems.Add(new ToolStripSeparator());
        string[] names={"纯色 · 橙红","纯色 · 蓝色","纯色 · 绿色","纯色 · 黄色","纯色 · 紫色"};
        Color[] values={Color.OrangeRed,Color.DeepSkyBlue,Color.LimeGreen,Color.Gold,Color.MediumPurple};
        for(int i=0;i<names.Length;i++) {
            Color value=values[i]; var item=new ToolStripMenuItem(names[i]); item.Checked=!color.IsEmpty && color.ToArgb()==value.ToArgb();
            item.Click+=delegate { color=value; style=FrameStyle.Rainbow; foreach(ToolStripItem sibling in styles.DropDownItems) { var option=sibling as ToolStripMenuItem; if(option!=null) option.Checked=option==item; } SaveSettings(); };
            styles.DropDownItems.Add(item);
        }
        var times=new ToolStripMenuItem("渐隐前停留时间（渐隐 0.5 秒）");
        foreach(int ms in new int[]{0,1000,1500,2000,3000}) {
            int value=ms; var item=new ToolStripMenuItem(ms==0?"无停留（松开即渐隐）":(ms/1000.0).ToString("0.0")+" 秒"); item.Checked=ms==duration;
            item.Click+=delegate { duration=value; foreach(ToolStripMenuItem sibling in times.DropDownItems) sibling.Checked=sibling==item; SaveSettings(); };
            times.DropDownItems.Add(item);
        }
        menu.Items.Add(times);
        var focus=new ToolStripMenuItem("聚焦遮罩 · 框外渐暗") { Checked=overlay.FocusEnabled,CheckOnClick=true };
        focus.Click+=delegate { overlay.FocusEnabled=focus.Checked; overlay.RefreshFrames(); SaveSettings(); };
        menu.Items.Add(focus);
        menu.Items.Add("清除所有框",null,delegate { Clear(); });
        menu.Items.Add("修复鼠标监听",null,delegate { input.Repair(); Clear(); tray.ShowBalloonTip(2000,"QuickFrame","正在重新连接鼠标监听。",ToolTipIcon.Info); });
        menu.Items.Add("退出",null,delegate { Application.Exit(); });
        tray.Icon=trayIcon; tray.Text="QuickFrame · 右键菜单切换风格";
        tray.ContextMenuStrip=menu; tray.DoubleClick+=delegate { Toggle(); }; tray.Visible=true;
        input=new MouseInput(clock);
        input.SetEnabled(enabled);
        toggle.Text=enabled?"暂停画框":"恢复画框";
        tray.Text=enabled?"QuickFrame · 已开启":"QuickFrame · 已暂停";
        initialized=true;
        bool hotkey=Native.RegisterHotKey(Handle,1,0x4003,(uint)Keys.F8);
        bool shadeWasShown=false;
        timer.Interval=33;
        timer.Tick+=delegate {
            if(Program.ShowRequest!=null && Program.ShowRequest.WaitOne(0)) {
                tray.ShowBalloonTip(2000,"QuickFrame 已在运行","请右键点击霓虹方框托盘图标进行设置。",ToolTipIcon.Info);
            }
            overlay.Now=clock.ElapsedMilliseconds;
            var snapshot=input.Read();
            overlay.Preview=snapshot.Preview; overlay.PreviewBorn=snapshot.Born;
            overlay.PreviewColor=color; overlay.PreviewStyle=style;
            foreach(var stroke in snapshot.Completed) overlay.Frames.Add(new Frame { Rect=stroke.Rect,Color=color,Style=style,Born=stroke.Born,Until=stroke.Released+duration+Frame.FadeMilliseconds });
            overlay.Frames.RemoveAll(f=>f.Until<=overlay.Now);
            if(overlay.Visible || overlay.Frames.Count>0 || overlay.Preview.HasValue) overlay.RefreshFrames();
            if(overlay.ShadeVisible) shadeWasShown=true;
        };
        timer.Start();
        if(smoke) {
            foreach(FrameStyle sample in Enum.GetValues(typeof(FrameStyle)))
                overlay.Frames.Add(new Frame { Rect=new Rectangle(30+((int)sample%3)*200,30+((int)sample/3)*130,170,95),Color=color,Style=sample,Born=clock.ElapsedMilliseconds,Until=clock.ElapsedMilliseconds+200+Frame.FadeMilliseconds });
            overlay.RefreshFrames();
            var finish=new Timer { Interval=1000 };
            finish.Tick+=delegate {
                finish.Stop(); finish.Dispose();
                Program.SmokePassed=shadeWasShown && overlay.Frames.Count==0 && !overlay.Visible && !overlay.ShadeVisible && input.Installed && !Visible && !ShowInTaskbar && tray.Visible;
                Application.Exit();
            };
            finish.Start();
        } else {
            SaveSettings();
            string status=enabled?(duration==0?"松开右键立即开始 0.5 秒消散。":"松开后停留 "+(duration/1000.0).ToString("0.0")+" 秒，再消散 0.5 秒。"):"已恢复上次的暂停状态。";
            tray.ShowBalloonTip(3500,"QuickFrame 已启动",status+(hotkey?"\nCtrl+Alt+F8 暂停或恢复。":"\n快捷键已被占用，请使用托盘菜单暂停。"),ToolTipIcon.Info);
        }
    }
    void Clear() { input.Cancel(); overlay.Frames.Clear(); overlay.Preview=null; overlay.RefreshFrames(); }
    void Toggle() { enabled=!enabled; input.SetEnabled(enabled); toggle.Text=enabled?"暂停画框":"恢复画框"; tray.Text=enabled?"QuickFrame · 已开启":"QuickFrame · 已暂停"; Clear(); SaveSettings(); }
    void SaveSettings() {
        if(!persistSettings || !initialized) return;
        try {
            new SavedSettings { Style=style,Color=color,Duration=duration,Enabled=enabled,Focus=overlay.FocusEnabled }.Save(settingsPath);
            saveErrorShown=false;
        } catch(Exception e) {
            if(!(e is IOException) && !(e is UnauthorizedAccessException)) throw;
            if(!saveErrorShown) tray.ShowBalloonTip(3500,"QuickFrame 设置未能保存","请将程序放在可写目录中。"+e.Message,ToolTipIcon.Warning);
            saveErrorShown=true;
        }
    }
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if(e.CloseReason==CloseReason.UserClosing) { e.Cancel=true; Hide(); }
        base.OnFormClosing(e);
    }
    protected override void WndProc(ref Message m) { if(m.Msg==0x312 && m.WParam.ToInt32()==1) Toggle(); base.WndProc(ref m); }
    protected override void Dispose(bool disposing) {
        if(disposing) SaveSettings();
        Native.UnregisterHotKey(Handle,1);
        if(disposing) { timer.Dispose(); if(input!=null) input.Dispose(); tray.Visible=false; tray.Dispose(); trayIcon.Dispose(); overlay.Dispose(); }
        base.Dispose(disposing);
    }
}

static class Program {
    public static bool SmokePassed;
    public static System.Threading.EventWaitHandle ShowRequest;
    [STAThread] static int Main(string[] args) {
        bool test=Array.IndexOf(args,"--self-test")>=0;
        bool smoke=Array.IndexOf(args,"--smoke-test")>=0;
        if(Array.IndexOf(args,"--settings-test")>=0) {
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings-test-"+Guid.NewGuid().ToString("N")+".ini");
            try {
                foreach(FrameStyle style in Enum.GetValues(typeof(FrameStyle))) {
                    new SavedSettings { Style=style,Duration=0,Enabled=false,Focus=false }.Save(path);
                    var restored=SavedSettings.Load(path);
                    if(restored.Style!=style || !restored.Color.IsEmpty || restored.Duration!=0 || restored.Enabled || restored.Focus) throw new Exception("Preset / zero delay / toggles did not restore");
                }
                foreach(Color color in new Color[]{Color.OrangeRed,Color.DeepSkyBlue,Color.LimeGreen,Color.Gold,Color.MediumPurple}) {
                    new SavedSettings { Style=FrameStyle.Rainbow,Color=color,Duration=3000 }.Save(path);
                    var restored=SavedSettings.Load(path);
                    if(restored.Color.ToArgb()!=color.ToArgb() || restored.Style!=FrameStyle.Rainbow || restored.Duration!=3000 || !restored.Enabled || !restored.Focus) throw new Exception("Solid preset did not restore");
                }
                File.WriteAllText(path,"Style=999\nColor=bad\nDuration=-1\nEnabled=bad\nFocus=False\nFutureKey=hello");
                var fallback=SavedSettings.Load(path);
                if(fallback.Style!=FrameStyle.NightCity || fallback.Duration!=1500 || !fallback.Color.IsEmpty || !fallback.Enabled || fallback.Focus) throw new Exception("Invalid settings handling failed");
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings-test.txt"),"PASS: seven style presets, five solid colors, zero delay, pause/focus toggles, atomic overwrite, invalid-field fallback."); return 0;
            } catch(Exception e) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings-test.txt"),e.ToString()); return 1; }
            finally { if(File.Exists(path)) File.Delete(path); if(File.Exists(path+".bak")) File.Delete(path+".bak"); }
        }
        if(Array.IndexOf(args,"--worker-test")>=0) {
            try {
                using(var listener=new MouseInput(Stopwatch.StartNew())) {
                    long before=System.Threading.Interlocked.Read(ref listener.Heartbeats);
                    System.Threading.Thread.Sleep(1600);
                    if(System.Threading.Interlocked.Read(ref listener.Heartbeats)-before<3 || !listener.Installed) throw new Exception("Input worker stalled with main thread");
                    long installs=System.Threading.Interlocked.Read(ref listener.InstallCount);
                    listener.Repair(); System.Threading.Thread.Sleep(500);
                    if(System.Threading.Interlocked.Read(ref listener.InstallCount)<=installs || !listener.Installed) throw new Exception("Repair failed");
                    listener.TestHeldDrag();
                }
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"worker-test.txt"),"PASS: worker survives main-thread stall; repair succeeds; stationary held drag survives 2.4 seconds of watchdog ticks; only release completes the stroke and sets its lifetime. No physical mouse clicks were injected."); return 0;
            } catch(Exception e) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"worker-test.txt"),e.ToString()); return 1; }
        }
        if(Array.IndexOf(args,"--render-preview")>=0) {
            using(var bitmap=new Bitmap(1000,1010)) {
                using(var g=Graphics.FromImage(bitmap)) {
                    g.Clear(Color.FromArgb(18,21,29));
                    using(var font=new Font("Microsoft YaHei UI",15)) {
                        for(int i=0;i<7;i++) {
                            int x=35+(i%2)*490,y=25+(i/2)*250;
                            g.DrawString(Rainbow.StyleNames[i],font,Brushes.White,x,y);
                            Rainbow.Glow(g,new Rectangle(x+8,y+55,405,135),0,1,Color.Empty,(FrameStyle)i);
                        }
                    }
                }
                bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"styles-preview.png"));
            }
            using(var bitmap=new Bitmap(1000,560)) {
                using(var g=Graphics.FromImage(bitmap)) using(var font=new Font("Microsoft YaHei UI",14)) {
                    g.Clear(Color.FromArgb(18,21,29));
                    string[] captions={"01 / 展开","02 / 锁定","03 / 闪断","04 / 错位消散"};
                    for(int i=0;i<4;i++) {
                        int x=40+(i%2)*490,y=25+(i/2)*270;
                        g.DrawString(captions[i],font,Brushes.White,x,y);
                        Rainbow.NightCity(g,new Rectangle(x+8,y+65,400,140),i==0?0.25f:1,i==2?0.2f:(i==3?0.70f:0),i==3?0.3f:1,-0.1);
                    }
                }
                bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"night-city-preview.png"));
            }
            return 0;
        }
        if(test) {
            try {
                var g=new Gesture(); g.Down(new Point(100,100));
                if(!g.Up(new Point(103,102))) throw new Exception("Click misclassified");
                g.Down(new Point(100,100)); g.Move(new Point(92,100));
                if(!g.Dragging || g.Up(new Point(70,50))) throw new Exception("Drag misclassified");
                if(g.Rect!=new Rectangle(70,50,30,50)) throw new Exception("Reverse rectangle");
                g.Down(new Point(-200,-100)); g.Move(new Point(200,100)); g.Move(new Point(-200,-100));
                if(g.Up(new Point(-200,-100))) throw new Exception("Drag must remain a drag");
                var f=new Frame { Until=2000 };
                if(FocusShade.Strength(0,1)!=0 || Math.Abs(FocusShade.Strength(280,1)-0.46)>0.001 || Math.Abs(FocusShade.Strength(500,0.5f)-0.23)>0.001 || FocusShade.Strength(1000,0)!=0) throw new Exception("Shade transition timing");
                using(var mask=FocusShade.Mask(new Rectangle(-100,0,400,300),new Rectangle[]{new Rectangle(-50,50,100,100),new Rectangle(0,75,100,100)})) {
                    if(!mask.IsVisible(10,10) || mask.IsVisible(60,60) || mask.IsVisible(125,100) || mask.IsVisible(190,160)) throw new Exception("Focus holes / negative screen coordinates / overlapping holes");
                }
                if(Rainbow.Deploy(0)!=0 || Rainbow.Deploy(320)!=1 || Rainbow.Deploy(1000)!=1 || Rainbow.Glitch(0.2f)>=Rainbow.Glitch(0.4f)) throw new Exception("Deploy / glitch timing");
                if(f.Opacity(0)!=1 || f.Opacity(1500)!=1 || f.Opacity(1750)!=0.5f || f.Opacity(2000)!=0 || f.Opacity(2200)!=0) throw new Exception("Fade timing");
                foreach(FrameStyle style in Enum.GetValues(typeof(FrameStyle))) {
                    using(var b=new Bitmap(100,100,PixelFormat.Format32bppArgb)) {
                        using(var graphics=Graphics.FromImage(b)) Rainbow.Glow(graphics,new Rectangle(25,25,50,50),0,1,Color.Empty,style);
                        if(b.GetPixel(50,50).A!=0 || b.GetPixel(50,25).A<200 || (style!=FrameStyle.Classic && b.GetPixel(50,18).A==0)) throw new Exception("Style alpha coverage: "+style);
                        using(var graphics=Graphics.FromImage(b)) { graphics.Clear(Color.Transparent); Rainbow.Glow(graphics,new Rectangle(25,25,50,50),0.2,0,Color.Empty,style); }
                        if(b.GetPixel(50,25).A!=0) throw new Exception("Style did not fade: "+style);
                    }
                }
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test.txt"),"PASS: gestures; fade, deployment and glitch timing; all seven styles have transparent interiors and fade to zero."); return 0;
            } catch(Exception e) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test.txt"),e.ToString()); return 1; }
        }
        bool fresh;
        string instance="Local\\QuickFrame-MVP-"+Native.DesktopName();
        using(var mutex=new System.Threading.Mutex(true,instance,out fresh))
        using(ShowRequest=new System.Threading.EventWaitHandle(false,System.Threading.EventResetMode.AutoReset,instance+"-Show")) {
            if(!fresh) { ShowRequest.Set(); return 0; }
            try {
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch(EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using(var controller=new Controller(smoke)) Application.Run();
                if(smoke) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"smoke-test.txt"),SmokePassed?"PASS: mouse hook installed; overlay displayed; frame expired and overlay hidden; clean exit.":"FAIL"); return SmokePassed?0:1; }
                return 0;
            } catch(Exception e) { MessageBox.Show(e.Message,"QuickFrame 启动失败",MessageBoxButtons.OK,MessageBoxIcon.Error); return 1; }
        }
    }
}
