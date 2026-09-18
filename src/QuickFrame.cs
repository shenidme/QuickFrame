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
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [StructLayout(LayoutKind.Sequential)] public struct BitmapHeader { public uint Size; public int Width,Height; public ushort Planes,Bits; public uint Compression,ImageSize; public int XPels,YPels; public uint Used,Important; }
    [DllImport("gdi32.dll",SetLastError=true)] public static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapHeader info,uint usage,out IntPtr bits,IntPtr section,uint offset);
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
enum TriggerMode { Right, AltRight, SideBack, SideForward }

// No painting or synchronous UI calls are allowed on the low-level hook thread.
class MouseInput : IDisposable {
    public class Stroke { public Rectangle Rect; public long Born,Released; }
    public class Snapshot { public Rectangle? Preview; public long Born; public List<Stroke> Completed; public bool Clear; }
    readonly object gate=new object();
    readonly Gesture gesture=new Gesture();
    readonly List<Stroke> completed=new List<Stroke>();
    readonly Stopwatch clock;
    readonly System.Threading.Thread thread;
    readonly System.Threading.ManualResetEvent ready=new System.Threading.ManualResetEvent(false);
    readonly Native.HookProc callback;
    readonly Native.HookProc keyboardCallback;
    IntPtr hook;
    IntPtr keyboardHook;
    TriggerMode trigger=TriggerMode.Right;
    int heldButton=2;
    string[] excludedApps=new string[0];
    bool appExcluded,clearRequested,escapeHeld;
    public volatile bool CaptureEscape;
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
        clock=time; callback=OnMouse; keyboardCallback=OnKeyboard;
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
            keyboardHook=Native.SetWindowsHookEx(13,keyboardCallback,Native.GetModuleHandle(null),0);
            if(keyboardHook==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            using(var watchdog=new Timer { Interval=250 }) {
                watchdog.Tick+=delegate {
                    System.Threading.Interlocked.Increment(ref Heartbeats);
                    if(stopping) { Application.ExitThread(); return; }
                    UpdateForeground();
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
        finally { if(hook!=IntPtr.Zero) Native.UnhookWindowsHookEx(hook); if(keyboardHook!=IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook); Installed=false; if(dispatcher!=null) dispatcher.Dispose(); }
    }
    public void SetEnabled(bool value) { lock(gate) { if(enabled==value) return; enabled=value; discarded=true; completed.Clear(); } }
    public void Cancel() { lock(gate) { discarded=true; completed.Clear(); } }
    public void Repair() { Cancel(); repair=true; }
    public void Configure(TriggerMode value,string excluded) { lock(gate) { trigger=value; excludedApps=excluded.Split(new char[]{';',',','\n'},StringSplitOptions.RemoveEmptyEntries); } }
    void UpdateForeground() {
        string[] apps; lock(gate) apps=excludedApps;
        bool blocked=false;
        if(apps.Length>0) try {
            uint pid; Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out pid);
            using(var process=Process.GetProcessById((int)pid)) foreach(string name in apps)
                if(string.Equals(Path.GetFileNameWithoutExtension(name.Trim()),process.ProcessName,StringComparison.OrdinalIgnoreCase)) blocked=true;
        } catch(ArgumentException) { } catch(System.ComponentModel.Win32Exception) { }
        lock(gate) appExcluded=blocked;
    }
    IntPtr OnKeyboard(int code,IntPtr message,IntPtr data) {
        if(code>=0 && Marshal.ReadInt32(data)==27) {
            int msg=message.ToInt32();
            lock(gate) {
                if((msg==0x100 || msg==0x104) && (CaptureEscape || (gesture.Pending&&!discarded) || escapeHeld)) { discarded=true; completed.Clear(); clearRequested=true; escapeHeld=true; return (IntPtr)1; }
                if((msg==0x101 || msg==0x105) && escapeHeld) { escapeHeld=false; return (IntPtr)1; }
            }
        }
        return Native.CallNextHookEx(keyboardHook,code,message,data);
    }
    public Snapshot Read() {
        lock(gate) {
            var result=new Snapshot { Preview=gesture.Pending&&gesture.Dragging&&!discarded?(Rectangle?)gesture.Rect:null,Born=born,Completed=new List<Stroke>(completed),Clear=clearRequested };
            completed.Clear(); clearRequested=false; return result;
        }
    }
    IntPtr OnMouse(int code,IntPtr message,IntPtr data) {
        if(code<0) return Native.CallNextHookEx(hook,code,message,data);
        System.Threading.Interlocked.Increment(ref SeenEvents);
        var m=(Native.Mouse)Marshal.PtrToStructure(data,typeof(Native.Mouse));
        if((m.Flags&1)!=0) return Native.CallNextHookEx(hook,code,message,data);
        int button=(message.ToInt32()==0x20b || message.ToInt32()==0x20c)?(int)(m.Data>>16)+3:2;
        int action=ProcessMouse(message.ToInt32(),new Point(m.Point.X,m.Point.Y),button,(Native.GetAsyncKeyState(0x12)&0x8000)!=0);
        if((action&2)!=0) dispatcher.BeginInvoke(new Action(delegate { Native.mouse_event(button==2?0x18u:0x180u,0,0,button==2?0u:(uint)(button-3),UIntPtr.Zero); }));
        return (action&1)!=0?(IntPtr)1:Native.CallNextHookEx(hook,code,message,data);
    }
    public static bool Matches(TriggerMode mode,int button,bool alt) { return mode==TriggerMode.Right?button==2:mode==TriggerMode.AltRight?button==2&&alt:mode==TriggerMode.SideBack?button==4:button==5; }
    int ProcessMouse(int msg,Point point,int button=2,bool alt=false) {
        bool suppress=false,replay=false;
        lock(gate) {
            lastInput=clock.ElapsedMilliseconds; lastPoint=point;
            if((msg==0x204 || msg==0x20b) && enabled && !appExcluded && !gesture.Pending && Matches(trigger,button,alt)) { gesture.Down(lastPoint); heldButton=button; born=lastInput; discarded=false; suppress=true; }
            else if(msg==0x200 && gesture.Pending) {
                bool wasDragging=gesture.Dragging; gesture.Move(lastPoint);
                if(!wasDragging && gesture.Dragging) born=lastInput;
            } else if((msg==0x205 || msg==0x20c) && gesture.Pending && button==heldButton) {
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
    public void TestTriggersAndEscape() {
        dispatcher.Invoke(new Action(delegate {
            Configure(TriggerMode.AltRight,"");
            if(ProcessMouse(0x204,new Point(0,0),2,false)!=0) throw new Exception("Plain right click intercepted in Alt mode");
            if(ProcessMouse(0x204,new Point(0,0),2,true)!=1 || ProcessMouse(0x205,new Point(0,0),2,false)!=3) throw new Exception("Alt click replay failed");
            Configure(TriggerMode.SideBack,"");
            if(ProcessMouse(0x20b,new Point(0,0),5)!=0 || ProcessMouse(0x20b,new Point(0,0),4)!=1) throw new Exception("Wrong side button intercepted");
            ProcessMouse(0x200,new Point(60,70));
            if(ProcessMouse(0x205,new Point(60,70),2)!=0 || !Read().Preview.HasValue) throw new Exception("Unrelated release ended drag");
            IntPtr key=Marshal.AllocHGlobal(24);
            try {
                Marshal.WriteInt32(key,27);
                if(OnKeyboard(0,(IntPtr)0x100,key)!=(IntPtr)1 || !Read().Clear || Read().Preview.HasValue) throw new Exception("Escape failed to cancel drag");
                if(OnKeyboard(0,(IntPtr)0x100,key)!=(IntPtr)1 || OnKeyboard(0,(IntPtr)0x101,key)!=(IntPtr)1) throw new Exception("Escape repeat / release leaked");
            } finally { Marshal.FreeHGlobal(key); }
            if(ProcessMouse(0x20c,new Point(60,70),4)!=1 || Read().Completed.Count!=0) throw new Exception("Cancelled drag completed or replayed");
            Configure(TriggerMode.SideForward,"");
            if(ProcessMouse(0x20b,new Point(0,0),5)!=1 || ProcessMouse(0x20c,new Point(0,0),5)!=3) throw new Exception("Side click replay failed");
            appExcluded=true;
            if(ProcessMouse(0x20b,new Point(0,0),5)!=0) throw new Exception("Excluded application intercepted");
            appExcluded=false; Configure(TriggerMode.Right,"");
        }));
    }
    public void Dispose() { stopping=true; if(thread.Join(2000)) ready.Dispose(); }
}

class Frame {
    public const int FadeMilliseconds=500;
    public Rectangle Rect; public Color Color; public long Until, Born; public FrameStyle Style;
    public int FadeDuration=FadeMilliseconds;
    public float Opacity(long now) {
        float t=Math.Max(0,Math.Min(1,(Until-now)/(float)Math.Max(1,FadeDuration)));
        return t*t*(3-2*t);
    }
}

static class Rainbow {
    public static float WidthScale=1,GlowScale=1;
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
                using(var pen=new Pen(brush,width*WidthScale)) { pen.StartCap=LineCap.Square; pen.EndCap=LineCap.Square; g.DrawLine(pen,points[edge],points[edge+1]); }
            }
            offset+=length;
        }
    }
    public static void Glow(Graphics g,Rectangle r,double phase,float opacity,Color solid,FrameStyle style=FrameStyle.Cyber) {
        if(style==FrameStyle.NightCity) { NightCity(g,r,1,0,opacity,phase); return; }
        g.SmoothingMode=SmoothingMode.AntiAlias;
        if(style==FrameStyle.Classic) { Draw(g,r,5,phase,opacity,solid,style); return; }
        Draw(g,r,23,phase,opacity*0.018f*GlowScale,solid,style);
        Draw(g,r,19,phase,opacity*0.028f*GlowScale,solid,style);
        Draw(g,r,15,phase,opacity*0.045f*GlowScale,solid,style);
        Draw(g,r,11,phase,opacity*0.075f*GlowScale,solid,style);
        Draw(g,r,8,phase,opacity*0.14f*GlowScale,solid,style);
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
        using(var glow=new Pen(Color.FromArgb((int)(22*alpha*GlowScale),yellow),19*WidthScale)) g.DrawLines(glow,shape);
        using(var glow=new Pen(Color.FromArgb((int)(42*alpha*GlowScale),yellow),12*WidthScale)) g.DrawLines(glow,shape);
        using(var dark=new Pen(Color.FromArgb((int)(210*alpha),Color.FromArgb(12,14,18)),8)) g.DrawLines(dark,shape);
        using(var line=new Pen(Color.FromArgb((int)(255*alpha),yellow),4*WidthScale)) g.DrawLines(line,shape);
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
            using(var pen=new Pen(Color.FromArgb((int)(25*opacity*GlowScale),tint),12)) g.DrawLines(pen,points);
            using(var pen=new Pen(Color.FromArgb((int)(85*opacity*GlowScale),tint),6)) g.DrawLines(pen,points);
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
            using(var halo=new SolidBrush(Color.FromArgb((int)(32*opacity*GlowScale),tint))) g.FillEllipse(halo,p.X-5,p.Y-5,10,10);
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

static class Program {
    public static bool SmokePassed;
    public static System.Threading.EventWaitHandle ShowRequest;
    public static System.Threading.EventWaitHandle SettingsRequest;
    [STAThread] static int Main(string[] args) {
        bool test=Array.IndexOf(args,"--self-test")>=0;
        bool smoke=Array.IndexOf(args,"--smoke-test")>=0;
        bool showSettings=Array.IndexOf(args,"--settings")>=0;
        if(Array.IndexOf(args,"--ui-test")>=0) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try { Checks.UserInterface(); File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-test.txt"),"PASS: defaults, preview draft, save, reset, cancel, and panel rendering."); return 0; }
            catch(Exception e) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-test.txt"),e.ToString()); return 1; }
        }
        if(Array.IndexOf(args,"--settings-demo")>=0) {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch(EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using(var window=new SettingsWindow(new SavedSettings(),delegate(SavedSettings s) { s.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-test.ini")); return true; })) Application.Run(window);
            return 0;
        }
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
                    if(restored.Color.ToArgb()!=color.ToArgb() || restored.Style!=FrameStyle.Rainbow || restored.Duration!=3000 || !restored.Enabled || restored.Focus) throw new Exception("Solid preset did not restore");
                }
                File.WriteAllText(path,"Style=999\nColor=bad\nDuration=-1\nEnabled=bad\nFocus=False\nFutureKey=hello");
                var fallback=SavedSettings.Load(path);
                if(fallback.Style!=FrameStyle.Rainbow || fallback.Duration!=0 || !fallback.Color.IsEmpty || !fallback.Enabled || fallback.Focus) throw new Exception("Invalid settings handling failed");
                Checks.Settings(path);
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
                    listener.TestTriggersAndEscape();
                }
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"worker-test.txt"),"PASS: worker survives main-thread stall; repair succeeds; stationary held drag survives 2.4 seconds; release starts lifetime; Alt and side-button triggers; click replay flags; Esc cancellation and key repeat; excluded app passthrough. No physical mouse clicks were injected."); return 0;
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
                Checks.Rendering();
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
        string instance="Local\\QuickFrame-MVP-"+Native.DesktopName()+(smoke?"-SmokeTest":"");
        using(var mutex=new System.Threading.Mutex(true,instance,out fresh))
        using(ShowRequest=new System.Threading.EventWaitHandle(false,System.Threading.EventResetMode.AutoReset,instance+"-Show"))
        using(SettingsRequest=new System.Threading.EventWaitHandle(false,System.Threading.EventResetMode.AutoReset,instance+"-Settings")) {
            if(!fresh) { if(smoke) return 2; if(showSettings) SettingsRequest.Set(); else ShowRequest.Set(); return 0; }
            try {
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch(EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using(var controller=new Controller(smoke)) { if(showSettings) SettingsRequest.Set(); Application.Run(); }
                if(smoke) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"smoke-test.txt"),SmokePassed?"PASS: mouse hook installed; overlay displayed; frame expired and overlay hidden; clean exit.":"FAIL"); return SmokePassed?0:1; }
                return 0;
            } catch(Exception e) { MessageBox.Show(e.Message,"QuickFrame 启动失败",MessageBoxButtons.OK,MessageBoxIcon.Error); return 1; }
        }
    }
}
