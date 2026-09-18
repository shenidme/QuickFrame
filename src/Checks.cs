using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Microsoft.Win32;
using System.Windows.Forms;

static class Checks {
    static void Require(bool ok,string text) { if(!ok) throw new Exception(text); }
    public static void UserInterface() {
        SavedSettings applied=null;
        using(var window=new SettingsWindow(new SavedSettings(),delegate(SavedSettings s) { applied=s; return false; })) {
            window.Show();
            Require(((ComboBox)window.Controls["preset"]).SelectedIndex==1 && ((NumericUpDown)window.Controls["hold"]).Value==0 && !((CheckBox)window.Controls["focus"]).Checked && !((CheckBox)window.Controls["startup"]).Checked,"UI defaults differ from settings defaults");
            using(var image=new Bitmap(window.Width,window.Height)) { window.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings-preview.png")); }
            ((ComboBox)window.Controls["preset"]).SelectedIndex=6;
            ((ComboBox)window.Controls["trigger"]).SelectedIndex=1;
            ((CheckBox)window.Controls["focus"]).Checked=true;
            ((CheckBox)window.Controls["startup"]).Checked=true;
            ((NumericUpDown)window.Controls["fade"]).Value=1.25m;
            Require(applied==null,"Preview applied settings before saving");
            ((Button)window.Controls["save"]).PerformClick();
            Require(applied!=null && applied.Style==FrameStyle.NightCity && applied.Trigger==TriggerMode.AltRight && applied.Focus && applied.AutoStart && applied.Fade==1250,"Save did not apply the displayed draft");
            ((Button)window.Controls["reset"]).PerformClick(); ((Button)window.Controls["save"]).PerformClick();
            Require(applied.Style==FrameStyle.Rainbow && applied.Duration==0 && !applied.Focus && !applied.AutoStart,"Reset did not restore requested defaults");
            applied=null; ((ComboBox)window.Controls["preset"]).SelectedIndex=3; ((Button)window.Controls["cancel"]).PerformClick();
            Require(applied==null && !window.Visible,"Cancel applied draft or left window open");
        }
    }
    public static void Settings(string path) {
        var defaults=new SavedSettings();
        Require(defaults.Style==FrameStyle.Rainbow && defaults.Color.IsEmpty && defaults.Duration==0 && !defaults.Focus && !defaults.AutoStart && defaults.Fade==500,"New defaults incorrect");
        foreach(TriggerMode trigger in Enum.GetValues(typeof(TriggerMode))) {
            new SavedSettings { Trigger=trigger,AutoStart=true,CurrentScreen=true,Width=9,Glow=0,Speed=20,Dim=80,Fade=150,Duration=1250,ExcludedApps="chrome.exe; GAME.exe" }.Save(path);
            var s=SavedSettings.Load(path);
            Require(s.Trigger==trigger && s.AutoStart && s.CurrentScreen && s.Width==9 && s.Glow==0 && s.Speed==20 && s.Dim==80 && s.Fade==150 && s.Duration==1250 && s.ExcludedApps=="chrome.exe; GAME.exe","Advanced settings roundtrip failed");
        }
        File.WriteAllText(path,"Version=1\nStyle=NightCity\nDuration=1500\nFocus=True\nEnabled=False");
        var legacy=SavedSettings.Load(path);
        Require(legacy.Style==FrameStyle.NightCity && legacy.Duration==1500 && legacy.Focus && !legacy.Enabled && !legacy.AutoStart && legacy.Fade==500,"Legacy settings migration lost preferences");
        File.WriteAllText(path,"Trigger=999\nWidth=-1\nGlow=900\nSpeed=0\nDim=900\nFade=0\nAutoStart=invalid");
        var invalid=SavedSettings.Load(path);
        Require(invalid.Trigger==TriggerMode.Right && invalid.Width==5 && invalid.Glow==100 && invalid.Speed==9 && invalid.Dim==46 && invalid.Fade==500 && !invalid.AutoStart,"Invalid advanced settings not rejected");
        string testKey=@"Software\QuickFrame-Tests-"+Guid.NewGuid().ToString("N");
        try {
            using(var key=Registry.CurrentUser.CreateSubKey(testKey)) {
                string exe=@"C:\Folder With Spaces\中文目录\QuickFrame.exe";
                key.SetValue("Unrelated","keep"); StartupManager.WriteValue(key,true,exe);
                Require((string)key.GetValue("QuickFrame")=="\""+exe+"\"" && StartupManager.ReadValue(key,exe),"Startup path not correctly quoted");
                Require(!StartupManager.ReadValue(key,@"C:\Other\QuickFrame.exe"),"Moved executable incorrectly reported enabled");
                StartupManager.WriteValue(key,false,exe); StartupManager.WriteValue(key,false,exe);
                Require(key.GetValue("QuickFrame")==null && (string)key.GetValue("Unrelated")=="keep","Startup disable affected other entries");
            }
        } finally { Registry.CurrentUser.DeleteSubKeyTree(testKey,false); }
    }
    public static void Rendering() {
        PreviewPixels();
        using(var surface=new LayerSurface()) {
            surface.Ensure(120,120); surface.Graphics.Clear(Color.Transparent);
            for(int i=0;i<80;i++) surface.Ensure(100+i%20,100);
            Require(surface.Allocations==1,"Surface allocated while dimensions fit");
            surface.Ensure(350,200); Require(surface.Allocations==2,"Surface failed to grow");
            surface.Dispose(); surface.Ensure(50,50); Require(surface.Allocations==3,"Surface failed after idle disposal");
        }
        var frame=new Frame { Until=1200,FadeDuration=1000 };
        Require(frame.Opacity(200)==1 && frame.Opacity(700)==0.5f && frame.Opacity(1200)==0,"Custom fade timing failed");
        Require(FocusGroup.HoleOpacity(.46,.46)==0 && Math.Abs(FocusGroup.HoleOpacity(.46,.23)-.23)<.001 && FocusGroup.HoleOpacity(.46,0)==.46,"Independent mask hole fade failed");
        foreach(TriggerMode mode in Enum.GetValues(typeof(TriggerMode))) for(int button=2;button<=5;button++) {
            bool expected=mode==TriggerMode.Right?button==2:mode==TriggerMode.AltRight?false:mode==TriggerMode.SideBack?button==4:button==5;
            Require(MouseInput.Matches(mode,button,false)==expected,"Trigger mapping failed");
        }
        Require(MouseInput.Matches(TriggerMode.AltRight,2,true),"Alt trigger failed");
        try {
            foreach(FrameStyle style in Enum.GetValues(typeof(FrameStyle))) using(var bitmap=new Bitmap(220,220,PixelFormat.Format32bppArgb)) using(var g=Graphics.FromImage(bitmap)) {
                Rainbow.WidthScale=1.8f; Rainbow.GlowScale=1.5f; Rainbow.Glow(g,new Rectangle(60,60,100,100),0.2,1,Color.Empty,style);
                Require(bitmap.GetPixel(110,110).A==0,"Large style filled interior");
                g.Clear(Color.Transparent); Rainbow.GlowScale=0; Rainbow.Glow(g,new Rectangle(60,60,100,100),0.2,1,Color.Empty,style);
            }
        } finally { Rainbow.WidthScale=Rainbow.GlowScale=1; }
    }
    static void PreviewPixels() {
        try {
            foreach(int dpi in new int[]{96,144,192,240}) foreach(int thickness in new int[]{3,9}) foreach(FrameStyle style in Enum.GetValues(typeof(FrameStyle))) {
                var size=new Size(608*dpi/96,195*dpi/96);
                var rect=new Rectangle(65*dpi/96,45*dpi/96,478*dpi/96,105*dpi/96);
                // Integer rounding at fractional DPI follows the two panel margins.
                rect.Width=size.Width-rect.Left*2; rect.Height=size.Height-rect.Top*2;
                using(var panel=new PreviewPanel()) using(var preview=new Bitmap(size.Width,size.Height)) using(var expected=new Bitmap(size.Width,size.Height)) {
                    panel.Settings=new SavedSettings { Style=style,Width=thickness,Glow=150 };
                    preview.SetResolution(dpi,dpi); expected.SetResolution(96,96);
                    using(var g=Graphics.FromImage(preview)) panel.PaintPreview(g,size,dpi,dpi,1000);
                    using(var g=Graphics.FromImage(expected)) {
                        g.Clear(panel.BackColor);
                        using(var grid=new Pen(Color.FromArgb(35,43,59))) {
                            for(int x=0;x<size.Width;x+=28*dpi/96) g.DrawLine(grid,x,0,x,size.Height);
                            for(int y=0;y<size.Height;y+=28*dpi/96) g.DrawLine(grid,0,y,size.Width,y);
                        }
                        // Desktop drawing: physical rectangle, 96-DPI backing bitmap, no scaling.
                        Rainbow.WidthScale=thickness/5f; Rainbow.GlowScale=1.5f;
                        Rainbow.Glow(g,rect,-1.0/9,1,Color.Empty,style);
                    }
                    for(int y=Math.Max(0,rect.Top-35);y<rect.Top+35;y++) for(int x=rect.Left+5;x<rect.Right;x+=11)
                        Require(preview.GetPixel(x,y).ToArgb()==expected.GetPixel(x,y).ToArgb(),"Preview differs from desktop pixels at DPI "+dpi+", width "+thickness+", style "+style+", point "+x+","+y+": "+preview.GetPixel(x,y)+" / "+expected.GetPixel(x,y));
                }
            }
        } finally { Rainbow.WidthScale=Rainbow.GlowScale=1; }
    }
}
