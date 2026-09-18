using System;
using System.Drawing;
using System.IO;
using System.Text;
using Microsoft.Win32;

class SavedSettings {
    public FrameStyle Style=FrameStyle.Rainbow;
    public Color Color=Color.Empty;
    public int Duration=0,Fade=500,Width=5,Glow=100,Speed=9,Dim=46;
    public bool Enabled=true,Focus=false,AutoStart=false,CurrentScreen=false;
    public TriggerMode Trigger=TriggerMode.Right;
    public string ExcludedApps="";
    public SavedSettings Copy() { return (SavedSettings)MemberwiseClone(); }
    public static SavedSettings Load(string path) {
        var result=new SavedSettings();
        if(!File.Exists(path)) return result;
        foreach(string line in File.ReadAllLines(path)) {
            int index=line.IndexOf('='); if(index<0) continue;
            string key=line.Substring(0,index).Trim(),value=line.Substring(index+1).Trim();
            int number; bool flag; FrameStyle style; TriggerMode trigger;
            if(key=="Style" && Enum.TryParse<FrameStyle>(value,out style) && Enum.IsDefined(typeof(FrameStyle),style)) result.Style=style;
            if(key=="Color" && int.TryParse(value,out number)) foreach(Color color in Presets.Colors) if(number==color.ToArgb()) result.Color=color;
            if(int.TryParse(value,out number)) {
                if(key=="Duration" && number>=0 && number<=3000) result.Duration=number;
                if(key=="Fade" && number>=150 && number<=1500) result.Fade=number;
                if(key=="Width" && number>=3 && number<=9) result.Width=number;
                if(key=="Glow" && number>=0 && number<=150) result.Glow=number;
                if(key=="Speed" && number>=3 && number<=20) result.Speed=number;
                if(key=="Dim" && number>=10 && number<=80) result.Dim=number;
            }
            if(bool.TryParse(value,out flag)) {
                if(key=="Enabled") result.Enabled=flag;
                if(key=="Focus") result.Focus=flag;
                if(key=="AutoStart") result.AutoStart=flag;
                if(key=="CurrentScreen") result.CurrentScreen=flag;
            }
            if(key=="Trigger" && Enum.TryParse<TriggerMode>(value,out trigger) && Enum.IsDefined(typeof(TriggerMode),trigger)) result.Trigger=trigger;
            if(key=="ExcludedApps") result.ExcludedApps=value;
        }
        if(!result.Color.IsEmpty) result.Style=FrameStyle.Rainbow;
        return result;
    }
    public void Save(string path) {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            File.WriteAllLines(temp,new string[]{"Version=2","Style="+Style,"Color="+(Color.IsEmpty?0:Color.ToArgb()),"Duration="+Duration,
                "Enabled="+Enabled,"Focus="+Focus,"Trigger="+Trigger,"AutoStart="+AutoStart,"CurrentScreen="+CurrentScreen,
                "Fade="+Fade,"Width="+Width,"Glow="+Glow,"Speed="+Speed,"Dim="+Dim,"ExcludedApps="+ExcludedApps.Replace("\r","").Replace("\n",";")},Encoding.UTF8);
            if(File.Exists(path)) File.Replace(temp,path,path+".bak"); else File.Move(temp,path);
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }
}

static class Presets {
    public static readonly Color[] Colors={Color.OrangeRed,Color.DeepSkyBlue,Color.LimeGreen,Color.Gold,Color.MediumPurple};
    public static readonly string[] SolidNames={"纯色 · 橙红","纯色 · 蓝色","纯色 · 绿色","纯色 · 黄色","纯色 · 紫色"};
    public static readonly string[] TriggerNames={"右键拖动","Alt + 右键拖动","鼠标侧键 · 后退","鼠标侧键 · 前进"};
    public static int Index(SavedSettings s) { for(int i=0;i<Colors.Length;i++) if(!s.Color.IsEmpty && s.Color.ToArgb()==Colors[i].ToArgb()) return 7+i; return (int)s.Style; }
    public static void Select(SavedSettings s,int index) { s.Style=index<7?(FrameStyle)index:FrameStyle.Rainbow; s.Color=index<7?Color.Empty:Colors[index-7]; }
    public static string Name(int index) { return index<7?Rainbow.StyleNames[index]:SolidNames[index-7]; }
}

static class StartupManager {
    const string KeyPath=@"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName="QuickFrame";
    public static string Command(string executable) { return "\""+Path.GetFullPath(executable)+"\""; }
    public static bool IsEnabled(string executable) {
        using(var key=Registry.CurrentUser.OpenSubKey(KeyPath)) return ReadValue(key,executable);
    }
    internal static bool ReadValue(RegistryKey key,string executable) { return key!=null && string.Equals(key.GetValue(ValueName) as string,Command(executable),StringComparison.OrdinalIgnoreCase); }
    internal static void WriteValue(RegistryKey key,bool enabled,string executable) {
        if(enabled) key.SetValue(ValueName,Command(executable),RegistryValueKind.String); else key.DeleteValue(ValueName,false);
    }
    public static void SetEnabled(bool enabled,string executable) {
        using(var key=Registry.CurrentUser.CreateSubKey(KeyPath)) {
            WriteValue(key,enabled,executable);
        }
    }
}
