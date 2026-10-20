// 配置存储：exe 同目录 duo.json（与 Electron 版 schema 一致，可直接复用旧配置）
// 便携零残留：不写 AppData，删除 exe + duo.json 即完全卸载
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

class Config {
    public class Settings {
        public int refreshMs = 30000;
        public int lowThreshold = 20;
        public int flashThreshold = 5;
        public bool autostart = false;
        public List<string> trayOrder = new List<string> { "nut75", "sc620" };
    }
    public class DeviceMeta { public string name; public string icon; }

    public Settings settings = new Settings();
    public Dictionary<string, DeviceMeta> devices = new Dictionary<string, DeviceMeta>();

    static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "duo.json");
    static readonly JavaScriptSerializer json = new JavaScriptSerializer();

    public static Config Load() {
        Config c = null;
        try { c = json.Deserialize<Config>(File.ReadAllText(FilePath)); } catch { }
        if (c == null) c = new Config();
        if (c.settings == null) c.settings = new Settings();
        if (c.devices == null) c.devices = new Dictionary<string, DeviceMeta>();
        var s = c.settings;
        s.refreshMs = new[] { 10000, 30000, 60000, 300000 }.Contains(s.refreshMs) ? s.refreshMs : 30000;
        s.lowThreshold = Math.Min(99, Math.Max(1, s.lowThreshold));
        s.flashThreshold = Math.Min(s.lowThreshold - 1, Math.Max(0, s.flashThreshold));
        if (s.trayOrder == null || Drivers.Defs.Any(d => !s.trayOrder.Contains(d.Id)))
            s.trayOrder = Drivers.Defs.Select(d => d.Id).ToList();
        return c;
    }

    public void Save() {
        try { File.WriteAllText(FilePath, json.Serialize(this)); } catch { }
    }

    public string DeviceName(Drivers.Def def) {
        DeviceMeta m;
        return devices.TryGetValue(def.Id, out m) && !string.IsNullOrEmpty(m.name) ? m.name : def.DefaultName;
    }

    public void SetDeviceName(string id, string name) {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;
        DeviceMeta m;
        if (!devices.TryGetValue(id, out m)) devices[id] = m = new DeviceMeta();
        m.name = name.Substring(0, Math.Min(24, name.Length));
        Save();
    }
}
