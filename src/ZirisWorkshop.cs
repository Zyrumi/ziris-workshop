// Ziri's Workshop
//
// Keeps Ziri's WoW Forever addons installed and up to date on a friend's PC.
// The friend runs this once. It copies itself to %LOCALAPPDATA%\ZirisWorkshop,
// starts with Windows, sits in the tray and:
//   - shows every addon listed in manifest.json on GitHub, with a checkbox
//   - ticked addons are installed and kept on the newest version
//   - unticking an addon moves its folder to the Recycle Bin
//   - checks when it starts, every 3 hours, and shortly after WoW saves an
//     addon's settings (that happens when you log out or /reload)
//   - updates itself from the same manifest
//
// Everything is downloaded from one hard-coded GitHub repo, checked against
// the SHA-256 in the manifest, and unpacked only into that addon's folder.
//
// Written for C# 5 so it builds with the csc.exe that ships with Windows
// (.NET Framework 4.8). See build.cmd.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyTitle("Ziri's Workshop")]
[assembly: System.Reflection.AssemblyProduct("Ziri's Workshop")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace ZirisWorkshop
{
    // ------------------------------------------------------------------
    // Data shapes (manifest.json and settings.json)
    // ------------------------------------------------------------------

    public class AddonInfo
    {
        public string id { get; set; }
        public string name { get; set; }
        public string description { get; set; }
        public string version { get; set; }
        public string url { get; set; }
        public string sha256 { get; set; }
    }

    public class AppInfo
    {
        public string version { get; set; }
        public string url { get; set; }
        public string sha256 { get; set; }
    }

    public class Manifest
    {
        public AppInfo app { get; set; }
        public List<AddonInfo> addons { get; set; }
    }

    public class Settings
    {
        public string ForeverPath { get; set; }
        public Dictionary<string, bool> Wanted { get; set; }
        public bool StartWithWindows { get; set; }
        public bool FirstCheckDone { get; set; }
        public bool TrayHintShown { get; set; }

        public Settings()
        {
            Wanted = new Dictionary<string, bool>();
            StartWithWindows = true;
        }
    }

    // ------------------------------------------------------------------
    // Command line
    // ------------------------------------------------------------------

    static class Opts
    {
        public static string Data;        // --data DIR   (test: other settings folder, no install/registry)
        public static string Source;      // --source DIR (test: read manifest + files from a folder)
        public static string Wow;         // --wow DIR    (test: use this _forever_ folder)
        public static bool Tray;          // --tray       start hidden in the tray
        public static bool CheckOnce;     // --check-once run one check without a window, then exit
        public static string Snapshot;    // --snapshot FILE.png  (test: picture of the window, then exit)
        public static int AfterUpdate;    // --after-update PID   (wait for the old copy to quit)

        public static bool Dev { get { return Data != null; } }

        public static void Parse(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "--data": Data = next; i++; break;
                    case "--source": Source = next; i++; break;
                    case "--wow": Wow = next; i++; break;
                    case "--tray": Tray = true; break;
                    case "--check-once": CheckOnce = true; break;
                    case "--snapshot": Snapshot = next; i++; break;
                    case "--after-update":
                        int pid;
                        if (int.TryParse(next, out pid)) AfterUpdate = pid;
                        i++;
                        break;
                }
            }
        }

        // Arguments a restarted copy should get again.
        public static string Passthrough()
        {
            var sb = new StringBuilder();
            if (Data != null) sb.Append(" --data " + Quote(Data));
            if (Source != null) sb.Append(" --source " + Quote(Source));
            if (Wow != null) sb.Append(" --wow " + Quote(Wow));
            if (CheckOnce) sb.Append(" --check-once");
            return sb.ToString();
        }

        public static string Quote(string s)
        {
            return "\"" + s.TrimEnd('\\') + "\"";
        }
    }

    // ------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------

    static class Util
    {
        public const string AppName = "Ziri's Workshop";
        public const string AppVersion = "1.0.0";
        public const string RawBase = "https://raw.githubusercontent.com/Zyrumi/ziris-workshop/main/";

        static readonly object logLock = new object();

        public static string DataDir
        {
            get
            {
                if (Opts.Data != null) return Path.GetFullPath(Opts.Data);
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZirisWorkshop");
            }
        }

        public static string ExePath
        {
            get { return Path.GetFullPath(System.Reflection.Assembly.GetEntryAssembly().Location); }
        }

        public static string InstalledExe
        {
            get { return Path.Combine(DataDir, "ZirisWorkshop.exe"); }
        }

        public static bool RunningInstalled
        {
            get { return string.Equals(ExePath, Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase); }
        }

        public static void Log(string msg)
        {
            try
            {
                lock (logLock)
                {
                    Directory.CreateDirectory(DataDir);
                    string path = Path.Combine(DataDir, "log.txt");
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 200000)
                    {
                        string keep = File.ReadAllText(path);
                        File.WriteAllText(path, keep.Substring(keep.Length - 100000));
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (byte b in sha.ComputeHash(data)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static List<int> Parts(string v)
        {
            var list = new List<int>();
            foreach (Match m in Regex.Matches(v ?? "", @"\d+"))
            {
                int n;
                list.Add(int.TryParse(m.Value, out n) ? n : 0);
            }
            return list;
        }

        // Is version a newer than version b?
        public static bool Newer(string a, string b)
        {
            var pa = Parts(a);
            var pb = Parts(b);
            for (int i = 0; i < Math.Max(pa.Count, pb.Count); i++)
            {
                int x = i < pa.Count ? pa[i] : 0;
                int y = i < pb.Count ? pb[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        public static bool SafeId(string id)
        {
            return id != null && Regex.IsMatch(id, @"^[A-Za-z0-9_\-]{1,64}$");
        }

        // Only paths inside the repo, like "addons/LootHighlight-2.9.2.zip".
        public static bool SafeRelative(string rel)
        {
            return rel != null && rel.Length > 0 && rel.Length < 200
                && !rel.Contains("..") && !rel.Contains(":") && !rel.Contains("\\")
                && !rel.StartsWith("/") && Regex.IsMatch(rel, @"^[A-Za-z0-9_\-./]+$");
        }

        public static byte[] Download(string rel, bool fresh)
        {
            if (!SafeRelative(rel)) throw new Exception("refused a download path: " + rel);
            if (Opts.Source != null)
                return File.ReadAllBytes(Path.Combine(Path.GetFullPath(Opts.Source), rel.Replace('/', '\\')));
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "ZirisWorkshop/" + AppVersion;
                string url = RawBase + rel;
                if (fresh) url += "?t=" + DateTime.UtcNow.Ticks;
                return wc.DownloadData(url);
            }
        }

        public static string TocValue(string tocPath, string key)
        {
            try
            {
                if (!File.Exists(tocPath)) return null;
                foreach (string line in File.ReadLines(tocPath))
                {
                    string t = line.Trim().TrimStart((char)0xFEFF);
                    if (t.StartsWith("## " + key + ":", StringComparison.OrdinalIgnoreCase))
                        return t.Substring(key.Length + 4).Trim();
                }
            }
            catch { }
            return null;
        }
    }

    // ------------------------------------------------------------------
    // The work: finding WoW, checking, installing, removing, self-update
    // ------------------------------------------------------------------

    class Workshop
    {
        public Settings Settings;
        public Manifest Manifest;
        public string Status = "";
        public readonly ConcurrentDictionary<string, string> Notes = new ConcurrentDictionary<string, string>();

        // Raised on a worker thread.
        public event Action Changed;
        public event Action<List<string>, List<string>> Finished; // updated, new addons
        public event Action ExitForUpdate;

        readonly object runLock = new object();
        FileSystemWatcher watcher;
        System.Threading.Timer saveTimer;
        string settingsPath;

        public Workshop()
        {
            settingsPath = Path.Combine(Util.DataDir, "settings.json");
            Settings = LoadSettings();
            if (Opts.Wow != null) Settings.ForeverPath = Path.GetFullPath(Opts.Wow);
        }

        Settings LoadSettings()
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    var s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(settingsPath));
                    if (s != null)
                    {
                        if (s.Wanted == null) s.Wanted = new Dictionary<string, bool>();
                        return s;
                    }
                }
            }
            catch (Exception e) { Util.Log("settings unreadable, starting fresh: " + e.Message); }
            return new Settings();
        }

        public void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Util.DataDir);
                string tmp = settingsPath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(Settings));
                if (File.Exists(settingsPath)) File.Delete(settingsPath);
                File.Move(tmp, settingsPath);
            }
            catch (Exception e) { Util.Log("could not save settings: " + e.Message); }
        }

        void Raise()
        {
            var h = Changed;
            if (h != null) h();
        }

        void SetStatus(string s)
        {
            Status = s;
            Raise();
        }

        // ---------------- WoW folder ----------------

        public string AddOnsDir
        {
            get { return Settings.ForeverPath == null ? null : Path.Combine(Settings.ForeverPath, "Interface", "AddOns"); }
        }

        // Accepts the World of Warcraft folder, _forever_, or its AddOns folder.
        public static string NormalizeForever(string picked)
        {
            if (string.IsNullOrEmpty(picked)) return null;
            try
            {
                var dir = new DirectoryInfo(picked);
                for (int up = 0; up < 3 && dir != null; up++)
                {
                    if (dir.Name.Equals("_forever_", StringComparison.OrdinalIgnoreCase) && dir.Exists)
                        return dir.FullName;
                    string inside = Path.Combine(dir.FullName, "_forever_");
                    if (Directory.Exists(inside)) return inside;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        public static string FindForever()
        {
            var roots = new List<string>();
            foreach (string key in new[] {
                @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft",
                @"SOFTWARE\Blizzard Entertainment\World of Warcraft" })
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(key))
                    {
                        string p = k == null ? null : k.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(p)) roots.Add(p);
                    }
                }
                catch { }
            }
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    string d = drive.RootDirectory.FullName;
                    foreach (string sub in new[] { "World of Warcraft", @"Blizzard\World of Warcraft", @"Games\World of Warcraft",
                        @"Program Files (x86)\World of Warcraft", @"Program Files\World of Warcraft", @"Battle.net\World of Warcraft" })
                        roots.Add(Path.Combine(d, sub));
                }
                catch { }
            }
            foreach (string r in roots)
            {
                string f = NormalizeForever(r);
                if (f != null) return f;
            }
            return null;
        }

        public string InstalledVersion(string id)
        {
            if (AddOnsDir == null || !Util.SafeId(id)) return null;
            string toc = Path.Combine(AddOnsDir, id, id + ".toc");
            if (!File.Exists(toc)) return null;
            return Util.TocValue(toc, "Version") ?? "?";
        }

        public bool IsWanted(string id)
        {
            bool w;
            return Settings.Wanted.TryGetValue(id, out w) && w;
        }

        public AddonInfo Find(string id)
        {
            var m = Manifest;
            if (m == null || m.addons == null) return null;
            return m.addons.FirstOrDefault(a => a.id == id);
        }

        // ---------------- Checking ----------------

        public void Check(string why)
        {
            lock (runLock)
            {
                try { CheckLocked(why); }
                catch (Exception e)
                {
                    Util.Log("check failed: " + e);
                    SetStatus("Something went wrong. Will try again later.");
                }
            }
        }

        void CheckLocked(string why)
        {
            Util.Log("check (" + why + ")");
            SetStatus("Checking for updates...");

            if (Settings.ForeverPath == null || !Directory.Exists(Settings.ForeverPath))
            {
                Settings.ForeverPath = FindForever();
                SaveSettings();
                WatchSaves();
            }
            if (Settings.ForeverPath == null)
            {
                SetStatus("Can't find WoW Forever. Click Change and pick your World of Warcraft folder.");
                return;
            }

            Manifest m;
            try
            {
                byte[] raw = Util.Download("manifest.json", true);
                m = new JavaScriptSerializer().Deserialize<Manifest>(Encoding.UTF8.GetString(raw).TrimStart((char)0xFEFF));
                if (m == null || m.addons == null) throw new Exception("manifest has no addons");
            }
            catch (Exception e)
            {
                Util.Log("manifest: " + e.Message);
                SetStatus("Can't reach GitHub right now. Will try again later.");
                return;
            }
            m.addons = m.addons.Where(a => a != null && Util.SafeId(a.id)).ToList();
            Manifest = m;
            Raise();

            var updated = new List<string>();
            var fresh = new List<string>();
            foreach (var a in m.addons)
            {
                string have = InstalledVersion(a.id);
                if (!Settings.Wanted.ContainsKey(a.id))
                {
                    // first time we see this addon: keep it if it is already installed
                    Settings.Wanted[a.id] = have != null;
                    if (have == null && Settings.FirstCheckDone) fresh.Add(a.name ?? a.id);
                }
                if (IsWanted(a.id) && (have == null || Util.Newer(a.version, have)))
                {
                    if (InstallLocked(a)) updated.Add((a.name ?? a.id) + " " + a.version);
                }
            }
            Settings.FirstCheckDone = true;
            SaveSettings();

            SetStatus("Last checked " + DateTime.Now.ToString("HH:mm") + ".");
            var f = Finished;
            if (f != null && (updated.Count > 0 || fresh.Count > 0)) f(updated, fresh);

            SelfUpdateLocked(m);
        }

        // ---------------- Installing ----------------

        public void InstallOne(string id)
        {
            lock (runLock)
            {
                var a = Find(id);
                if (a == null) { CheckLocked("ticked " + id); return; }
                if (InstallLocked(a))
                {
                    var f = Finished;
                    if (f != null) f(new List<string> { (a.name ?? a.id) + " " + a.version }, new List<string>());
                }
            }
        }

        bool InstallLocked(AddonInfo a)
        {
            string name = a.name ?? a.id;
            Notes[a.id] = "Downloading " + a.version + "...";
            Raise();
            try
            {
                if (AddOnsDir == null) throw new Exception("no WoW folder");
                byte[] data = Util.Download(a.url, false);
                string sum = Util.Sha256Hex(data);
                if (!string.Equals(sum, a.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("the download was damaged (checksum did not match)");

                Directory.CreateDirectory(AddOnsDir);
                string stageRoot = Path.Combine(Settings.ForeverPath, "ZirisWorkshop-staging");
                string stage = Path.Combine(stageRoot, a.id + "-" + DateTime.Now.Ticks);
                Directory.CreateDirectory(stage);
                try
                {
                    string stageFull = Path.GetFullPath(stage).TrimEnd('\\') + "\\";
                    using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
                    {
                        foreach (var e in zip.Entries)
                        {
                            string n = e.FullName.Replace('\\', '/');
                            if (!n.StartsWith(a.id + "/", StringComparison.Ordinal))
                                throw new Exception("unexpected file in the download: " + n);
                            if (n.EndsWith("/")) continue;
                            if (n.Contains("..") || n.Contains(":"))
                                throw new Exception("unsafe file name in the download: " + n);
                            string dest = Path.GetFullPath(Path.Combine(stage, n.Replace('/', '\\')));
                            if (!dest.StartsWith(stageFull, StringComparison.OrdinalIgnoreCase))
                                throw new Exception("unsafe file name in the download: " + n);
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            e.ExtractToFile(dest, true);
                        }
                    }
                    string fresh = Path.Combine(stage, a.id);
                    if (!File.Exists(Path.Combine(fresh, a.id + ".toc")))
                        throw new Exception("the download has no " + a.id + ".toc");

                    string target = Path.Combine(AddOnsDir, a.id);
                    string old = Path.Combine(stage, "previous");
                    if (Directory.Exists(target)) Directory.Move(target, old);
                    try { Directory.Move(fresh, target); }
                    catch
                    {
                        if (Directory.Exists(old) && !Directory.Exists(target)) Directory.Move(old, target);
                        throw;
                    }
                }
                finally
                {
                    try { Directory.Delete(stage, true); } catch { }
                    try { if (!Directory.EnumerateFileSystemEntries(stageRoot).Any()) Directory.Delete(stageRoot); } catch { }
                }
                string dummy;
                Notes.TryRemove(a.id, out dummy);
                Util.Log("installed " + a.id + " " + a.version);
                Raise();
                return true;
            }
            catch (Exception e)
            {
                Util.Log("install " + a.id + " failed: " + e);
                string why = e is IOException || e is UnauthorizedAccessException
                    ? "a file was in use or locked. Will try again later."
                    : e is WebException ? "can't reach GitHub right now." : e.Message;
                Notes[a.id] = "Couldn't install " + a.version + ": " + why;
                Raise();
                return false;
            }
        }

        // ---------------- Removing ----------------

        public void RemoveOne(string id)
        {
            lock (runLock)
            {
                string dummy;
                Notes.TryRemove(id, out dummy);
                if (AddOnsDir == null || !Util.SafeId(id)) return;
                string target = Path.Combine(AddOnsDir, id);
                try
                {
                    if (Directory.Exists(target))
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(target,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        Util.Log("removed " + id + " (Recycle Bin)");
                    }
                }
                catch (Exception e)
                {
                    Util.Log("remove " + id + " failed: " + e);
                    Notes[id] = "Couldn't remove it: " + e.Message;
                }
                Raise();
            }
        }

        // ---------------- Updating this app ----------------

        void SelfUpdateLocked(Manifest m)
        {
            if (m.app == null || !Util.Newer(m.app.version, Util.AppVersion)) return;
            // Only the installed copy replaces itself (or a test copy reading a test folder).
            if (!(Opts.Source != null || (!Opts.Dev && Util.RunningInstalled))) return;
            try
            {
                SetStatus("Updating Ziri's Workshop to " + m.app.version + "...");
                byte[] data = Util.Download(m.app.url, false);
                if (!string.Equals(Util.Sha256Hex(data), m.app.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("the download was damaged (checksum did not match)");
                string exe = Util.ExePath;
                string old = exe + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);           // Windows lets a running program be renamed
                try { File.WriteAllBytes(exe, data); }
                catch { File.Move(old, exe); throw; }
                string args = Opts.Passthrough() + (Opts.CheckOnce ? "" : " --tray")
                    + " --after-update " + Process.GetCurrentProcess().Id;
                Process.Start(exe, args.Trim());
                Util.Log("self-update " + Util.AppVersion + " -> " + m.app.version);
                var h = ExitForUpdate;
                if (h != null) h();
            }
            catch (Exception e)
            {
                Util.Log("self-update failed: " + e);
                SetStatus("Last checked " + DateTime.Now.ToString("HH:mm") + ".");
            }
        }

        public static void CleanupOld()
        {
            string old = Util.ExePath + ".old";
            for (int i = 0; i < 10 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch { Thread.Sleep(300); }
            }
        }

        // ---------------- Watching WoW's saved settings ----------------

        // WoW writes each addon's saved file when you log out or /reload.
        // When one of our addons' files changes, check a little later.
        public void WatchSaves()
        {
            try
            {
                if (watcher != null) { watcher.Dispose(); watcher = null; }
                if (Settings.ForeverPath == null) return;
                string dir = Path.Combine(Settings.ForeverPath, "WTF", "Account");
                if (!Directory.Exists(dir)) return;
                watcher = new FileSystemWatcher(dir, "*.lua");
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
                FileSystemEventHandler h = (s, e) => OnSaved(e.FullPath);
                watcher.Changed += h;
                watcher.Created += h;
                watcher.Renamed += (s, e) => OnSaved(e.FullPath);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) { Util.Log("watch failed: " + e.Message); }
        }

        void OnSaved(string path)
        {
            if (path.IndexOf("\\SavedVariables\\", StringComparison.OrdinalIgnoreCase) < 0) return;
            string id = Path.GetFileNameWithoutExtension(path);
            if (Find(id) == null) return;
            if (saveTimer != null) saveTimer.Dispose();
            saveTimer = new System.Threading.Timer(_ => Check("WoW saved " + id), null, 30000, Timeout.Infinite);
        }

        // ---------------- Start with Windows ----------------

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public void ApplyStartWithWindows()
        {
            if (Opts.Dev) return;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (Settings.StartWithWindows)
                        k.SetValue(Util.AppName, "\"" + Util.InstalledExe + "\" --tray");
                    else if (k.GetValue(Util.AppName) != null)
                        k.DeleteValue(Util.AppName);
                }
            }
            catch (Exception e) { Util.Log("start-with-windows: " + e.Message); }
        }
    }

    // ------------------------------------------------------------------
    // The window
    // ------------------------------------------------------------------

    class Row
    {
        public string Id;
        public TableLayoutPanel Panel;
        public CheckBox Box;
        public Label Desc;
        public Label State;
        public bool Silent;
    }

    class MainForm : Form
    {
        readonly Workshop ws;
        readonly NotifyIcon tray;
        readonly FlowLayoutPanel list;
        readonly Label pathLabel;
        readonly Label statusLabel;
        readonly CheckBox startBox;
        readonly Dictionary<string, Row> rows = new Dictionary<string, Row>();
        readonly System.Windows.Forms.Timer every3h;
        readonly System.Windows.Forms.Timer firstCheck;
        bool allowClose;
        bool busy;
        string pendingCheck;
        bool snapshotTaken;

        static readonly Color Ink = Color.FromArgb(30, 28, 44);
        static readonly Color Dim = Color.FromArgb(90, 88, 104);
        static readonly Color Card = Color.White;
        static readonly Color Back = Color.FromArgb(242, 240, 248);

        public MainForm(Workshop workshop)
        {
            ws = workshop;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = Util.AppName;
            Font = new Font("Segoe UI", 9.75f);
            BackColor = Back;
            ForeColor = Ink;
            ClientSize = new Size(560, 520);
            MinimumSize = new Size(460, 420);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = LoadIcon(32);

            // header
            var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(16, 14, 16, 8) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var pic = new PictureBox { Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 12, 0) };
            var big = LoadIcon(64);
            if (big != null) pic.Image = big.ToBitmap();
            header.Controls.Add(pic, 0, 0);
            header.SetRowSpan(pic, 2);
            var title = new Label { Text = Util.AppName, AutoSize = true, Font = new Font("Segoe UI Semibold", 16f), Margin = new Padding(0, 6, 0, 0) };
            header.Controls.Add(title, 1, 0);
            var sub = new Label { Text = "Tick the addons you want. They stay up to date by themselves.", AutoSize = true, ForeColor = Dim, Margin = new Padding(2, 2, 0, 0) };
            header.Controls.Add(sub, 1, 1);

            // addon list
            list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(12, 4, 12, 4)
            };
            list.Resize += (s, e) => FitRows();

            // bottom bar
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(16, 8, 16, 12), BackColor = Color.FromArgb(230, 227, 240) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            bottom.Controls.Add(new Label { Text = "WoW folder:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 6) }, 0, 0);
            pathLabel = new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Dim };
            bottom.Controls.Add(pathLabel, 1, 0);
            var change = new Button { Text = "Change...", AutoSize = true, Anchor = AnchorStyles.Right };
            change.Click += (s, e) => PickFolder();
            bottom.Controls.Add(change, 2, 0);

            startBox = new CheckBox { Text = "Start with Windows (recommended, so updates arrive by themselves)", AutoSize = true, Checked = ws.Settings.StartWithWindows, Margin = new Padding(0, 6, 0, 6) };
            startBox.CheckedChanged += (s, e) =>
            {
                ws.Settings.StartWithWindows = startBox.Checked;
                ws.SaveSettings();
                ws.ApplyStartWithWindows();
            };
            bottom.Controls.Add(startBox, 0, 1);
            bottom.SetColumnSpan(startBox, 3);

            statusLabel = new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            bottom.Controls.Add(statusLabel, 0, 2);
            bottom.SetColumnSpan(statusLabel, 2);
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            var checkBtn = new Button { Text = "Check now", AutoSize = true };
            checkBtn.Click += (s, e) => RunCheck("button");
            var hideBtn = new Button { Text = "Hide", AutoSize = true };
            hideBtn.Click += (s, e) => HideToTray();
            buttons.Controls.Add(checkBtn);
            buttons.Controls.Add(hideBtn);
            bottom.Controls.Add(buttons, 2, 2);

            var version = new Label { Text = "Version " + Util.AppVersion, AutoSize = true, ForeColor = Dim, Font = new Font("Segoe UI", 8f), Margin = new Padding(0, 2, 0, 0) };
            bottom.Controls.Add(version, 0, 3);
            bottom.SetColumnSpan(version, 3);

            Controls.Add(list);
            Controls.Add(bottom);
            Controls.Add(header);

            // tray
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Ziri's Workshop", null, (s, e) => ShowWindow());
            menu.Items.Add("Check for updates", null, (s, e) => RunCheck("tray"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Quit());
            tray = new NotifyIcon { Icon = LoadIcon(SystemInformation.SmallIconSize.Width) ?? SystemIcons.Application, Text = Util.AppName, ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            tray.BalloonTipClicked += (s, e) => ShowWindow();

            ws.Changed += () => Ui(RefreshAll);
            ws.Finished += (updated, fresh) => Ui(() => Announce(updated, fresh));
            ws.ExitForUpdate += () => Ui(Quit);

            every3h = new System.Windows.Forms.Timer { Interval = 3 * 60 * 60 * 1000 };
            every3h.Tick += (s, e) => RunCheck("every 3 hours");
            every3h.Start();
            firstCheck = new System.Windows.Forms.Timer { Interval = 1500 };
            firstCheck.Tick += (s, e) => { firstCheck.Stop(); RunCheck("start"); };
            firstCheck.Start();

            FormClosing += (s, e) =>
            {
                if (!allowClose && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideToTray();
                }
            };

            ws.WatchSaves();
            RefreshAll();
            ResumeLayout(true);
        }

        static Icon LoadIcon(int size)
        {
            try
            {
                using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("icon.ico"))
                    return s == null ? null : new Icon(s, new Size(size, size));
            }
            catch { return null; }
        }

        void Ui(Action a)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch (InvalidOperationException) { }
        }

        // ---------------- background work ----------------

        void RunWorker(Action work)
        {
            var t = new Thread(() =>
            {
                try { work(); }
                catch (Exception e) { Util.Log("worker: " + e); }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void RunCheck(string why)
        {
            if (busy) { pendingCheck = why; return; }
            busy = true;
            RunWorker(() =>
            {
                ws.Check(why);
                Ui(() =>
                {
                    busy = false;
                    TakeSnapshotIfAsked();
                    if (pendingCheck != null)
                    {
                        string again = pendingCheck;
                        pendingCheck = null;
                        RunCheck(again);
                    }
                });
            });
        }

        // ---------------- list ----------------

        void RefreshAll()
        {
            pathLabel.Text = ws.Settings.ForeverPath ?? "not found yet";
            statusLabel.Text = ws.Status;
            var m = ws.Manifest;
            if (m == null || m.addons == null)
            {
                if (rows.Count == 0 && list.Controls.Count == 0)
                    list.Controls.Add(new Label { Text = "Getting the list of addons...", AutoSize = true, ForeColor = Dim, Margin = new Padding(6, 10, 0, 0) });
                return;
            }
            var ids = m.addons.Select(a => a.id).ToList();
            if (!ids.SequenceEqual(rows.Keys))
            {
                list.SuspendLayout();
                list.Controls.Clear();
                rows.Clear();
                foreach (var a in m.addons) rows[a.id] = MakeRow(a);
                foreach (var r in rows.Values) list.Controls.Add(r.Panel);
                list.ResumeLayout();
                FitRows();
            }
            foreach (var a in m.addons) UpdateRow(rows[a.id], a);
        }

        Row MakeRow(AddonInfo a)
        {
            var r = new Row { Id = a.id };
            r.Panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, BackColor = Card, Padding = new Padding(10, 8, 10, 8), Margin = new Padding(4, 4, 4, 4) };
            r.Box = new CheckBox { Text = a.name ?? a.id, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f) };
            r.Box.CheckedChanged += (s, e) => OnToggle(r);
            r.Desc = new Label { Text = a.description ?? "", AutoSize = true, ForeColor = Dim, Margin = new Padding(20, 0, 0, 2) };
            r.State = new Label { AutoSize = true, Margin = new Padding(20, 2, 0, 0) };
            r.Panel.Controls.Add(r.Box);
            r.Panel.Controls.Add(r.Desc);
            r.Panel.Controls.Add(r.State);
            return r;
        }

        void FitRows()
        {
            int w = list.ClientSize.Width - list.Padding.Horizontal - 10;
            if (w < 200) return;
            foreach (var r in rows.Values)
            {
                r.Panel.MinimumSize = new Size(w, 0);
                r.Panel.MaximumSize = new Size(w, 0);
                r.Desc.MaximumSize = new Size(w - 50, 0);
                r.State.MaximumSize = new Size(w - 50, 0);
            }
        }

        void UpdateRow(Row r, AddonInfo a)
        {
            bool wanted = ws.IsWanted(a.id);
            if (r.Box.Checked != wanted)
            {
                r.Silent = true;
                r.Box.Checked = wanted;
                r.Silent = false;
            }
            string have = ws.InstalledVersion(a.id);
            string note;
            string text;
            if (ws.Notes.TryGetValue(a.id, out note) && note != null) text = note;
            else if (wanted)
            {
                if (have == null) text = "Not installed yet.";
                else if (Util.Newer(a.version, have)) text = "Version " + have + ". Update to " + a.version + " waiting.";
                else text = "Version " + have + ", up to date.";
            }
            else
            {
                text = have == null
                    ? "Not installed. Tick the box to add it."
                    : "Version " + have + " is installed but not ticked, so it isn't kept up to date.";
            }
            r.State.Text = text;
        }

        void OnToggle(Row r)
        {
            if (r.Silent) return;
            var a = ws.Find(r.Id);
            string name = a != null && a.name != null ? a.name : r.Id;
            if (r.Box.Checked)
            {
                ws.Settings.Wanted[r.Id] = true;
                ws.SaveSettings();
                RunWorker(() => ws.InstallOne(r.Id));
            }
            else
            {
                if (ws.InstalledVersion(r.Id) != null)
                {
                    var answer = MessageBox.Show(this,
                        "Remove " + name + " from WoW?\n\nIts folder goes to the Recycle Bin. Tick it again any time to get it back.",
                        Util.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes)
                    {
                        r.Silent = true;
                        r.Box.Checked = true;
                        r.Silent = false;
                        return;
                    }
                }
                ws.Settings.Wanted[r.Id] = false;
                ws.SaveSettings();
                RunWorker(() => ws.RemoveOne(r.Id));
            }
            RefreshAll();
        }

        // ---------------- other actions ----------------

        void PickFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Pick your World of Warcraft folder (the one with a _forever_ folder inside).";
                dlg.ShowNewFolderButton = false;
                if (ws.Settings.ForeverPath != null) dlg.SelectedPath = ws.Settings.ForeverPath;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string f = Workshop.NormalizeForever(dlg.SelectedPath);
                if (f == null)
                {
                    MessageBox.Show(this, "Couldn't find WoW Forever there.\n\nPick the World of Warcraft folder that has a _forever_ folder inside.",
                        Util.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                ws.Settings.ForeverPath = f;
                ws.SaveSettings();
                ws.WatchSaves();
                RefreshAll();
                RunCheck("folder changed");
            }
        }

        void Announce(List<string> updated, List<string> fresh)
        {
            RefreshAll();
            string msg = "";
            if (updated.Count > 0)
                msg = "Updated " + string.Join(", ", updated) + ". Restart WoW to use " + (updated.Count == 1 ? "it." : "them.");
            if (fresh.Count > 0)
                msg += (msg.Length > 0 ? "\n" : "") + "New from Ziri: " + string.Join(", ", fresh) + ". Open Ziri's Workshop to add it.";
            if (msg.Length > 0) tray.ShowBalloonTip(8000, Util.AppName, msg, ToolTipIcon.Info);
        }

        public void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        void HideToTray()
        {
            Hide();
            if (!ws.Settings.TrayHintShown)
            {
                ws.Settings.TrayHintShown = true;
                ws.SaveSettings();
                tray.ShowBalloonTip(6000, Util.AppName, "Still running down here and keeping your addons up to date. Click the icon to open it again.", ToolTipIcon.Info);
            }
        }

        void Quit()
        {
            allowClose = true;
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
        }

        void TakeSnapshotIfAsked()
        {
            if (Opts.Snapshot == null || snapshotTaken) return;
            snapshotTaken = true;
            var t = new System.Windows.Forms.Timer { Interval = 600 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                try
                {
                    using (var bmp = new Bitmap(Width, Height))
                    {
                        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                        bmp.Save(Opts.Snapshot, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch (Exception ex) { Util.Log("snapshot: " + ex.Message); }
                Quit();
            };
            t.Start();
        }
    }

    // ------------------------------------------------------------------
    // Start-up
    // ------------------------------------------------------------------

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            Opts.Parse(args);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            if (Opts.AfterUpdate > 0)
            {
                try { Process.GetProcessById(Opts.AfterUpdate).WaitForExit(15000); } catch { }
            }
            Workshop.CleanupOld();
            Util.Log("start " + Util.AppVersion + (args.Length > 0 ? " [" + string.Join(" ", args) + "]" : ""));

            if (Opts.CheckOnce)
            {
                var ws = new Workshop();
                bool restarting = false;
                ws.ExitForUpdate += () => restarting = true;
                ws.Check("check-once");
                Util.Log("check-once done: " + ws.Status + (restarting ? " (restarting for update)" : ""));
                return 0;
            }

            string key = Util.Sha256Hex(Encoding.UTF8.GetBytes(Util.DataDir.ToLowerInvariant())).Substring(0, 12);
            bool created;
            using (var mutex = new Mutex(true, "Local\\ZirisWorkshop-" + key, out created))
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ZirisWorkshop-show-" + key))
            {
                if (!created)
                {
                    showEvent.Set(); // already running: bring that window up
                    return 0;
                }

                if (!Opts.Dev && !Util.RunningInstalled)
                {
                    if (InstallSelf()) return 0;
                }

                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var workshop = new Workshop();
                workshop.ApplyStartWithWindows();
                var form = new MainForm(workshop);
                var handle = form.Handle; // so background work can reach the window while hidden

                var listener = new Thread(() =>
                {
                    while (true)
                    {
                        showEvent.WaitOne();
                        try { form.BeginInvoke(new Action(form.ShowWindow)); } catch { return; }
                    }
                });
                listener.IsBackground = true;
                listener.Start();

                if (!Opts.Tray || Opts.Snapshot != null) form.Show();
                Application.Run();
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        // First run from Downloads (or anywhere): copy into %LOCALAPPDATA%,
        // add a Start menu shortcut, and start the installed copy.
        static bool InstallSelf()
        {
            try
            {
                Directory.CreateDirectory(Util.DataDir);
                File.Copy(Util.ExePath, Util.InstalledExe, true);
                try
                {
                    string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                    dynamic shell = Activator.CreateInstance(shellType);
                    dynamic lnk = shell.CreateShortcut(Path.Combine(programs, "Ziri's Workshop.lnk"));
                    lnk.TargetPath = Util.InstalledExe;
                    lnk.WorkingDirectory = Util.DataDir;
                    lnk.Description = "Keeps Ziri's WoW addons up to date";
                    lnk.Save();
                }
                catch (Exception e) { Util.Log("shortcut: " + e.Message); }
                // the new copy waits for this one to quit, so it can take over the single-instance lock
                Process.Start(Util.InstalledExe, "--after-update " + Process.GetCurrentProcess().Id);
                Util.Log("installed to " + Util.InstalledExe);
                return true;
            }
            catch (Exception e)
            {
                Util.Log("install failed, running from here: " + e);
                return false;
            }
        }
    }
}
