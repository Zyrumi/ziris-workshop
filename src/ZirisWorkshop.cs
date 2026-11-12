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
using System.Drawing.Drawing2D;
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
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]

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
        public const string AppVersion = "1.1.1";
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
    // The window: a little tinker's workshop
    // ------------------------------------------------------------------

    // Colors, fonts and drawing helpers shared by the custom controls.
    static class Theme
    {
        public static float K = 1f; // screen scale (1 = 96 dpi)

        public static int S(float v) { return (int)Math.Round(v * K); }

        public static readonly Color Paper = Color.FromArgb(247, 240, 225);
        public static readonly Color Grid = Color.FromArgb(236, 226, 204);
        public static readonly Color GridMajor = Color.FromArgb(226, 212, 184);
        public static readonly Color Ink = Color.FromArgb(59, 47, 47);
        public static readonly Color InkMid = Color.FromArgb(104, 88, 82);
        public static readonly Color Brass = Color.FromArgb(215, 162, 62);
        public static readonly Color BrassDark = Color.FromArgb(132, 92, 30);
        public static readonly Color BrassLight = Color.FromArgb(246, 220, 146);
        public static readonly Color Steel1 = Color.FromArgb(255, 253, 248);
        public static readonly Color Steel2 = Color.FromArgb(239, 231, 215);
        public static readonly Color Pink = Color.FromArgb(224, 108, 181);
        public static readonly Color PinkDark = Color.FromArgb(150, 52, 118);
        public static readonly Color PinkLight = Color.FromArgb(251, 220, 239);
        public static readonly Color Teal = Color.FromArgb(40, 128, 117);
        public static readonly Color TealLight = Color.FromArgb(214, 240, 233);
        public static readonly Color Rust = Color.FromArgb(170, 64, 50);
        public static readonly Color RustLight = Color.FromArgb(249, 219, 212);
        public static readonly Color Waiting = Color.FromArgb(251, 237, 196);
        public static readonly Color OffTrack = Color.FromArgb(214, 205, 192);
        public static readonly Color OffLight = Color.FromArgb(234, 228, 218);
        public static readonly Color Bench1 = Color.FromArgb(88, 72, 80);
        public static readonly Color Bench2 = Color.FromArgb(56, 46, 52);
        public static readonly Color Cream = Color.FromArgb(255, 246, 230);
        public static readonly Color CreamDim = Color.FromArgb(214, 198, 176);

        public static Font Title, CardTitle, Button, Label, Tiny, Body, Symbol;

        public static void Init(float dpi)
        {
            K = dpi / 96f;
            bool bahn = HasFont("Bahnschrift SemiBold") && HasFont("Bahnschrift");
            string bold = bahn ? "Bahnschrift SemiBold" : "Segoe UI Semibold";
            string plain = bahn ? "Bahnschrift" : "Segoe UI";
            Title = new Font(bold, 22f);
            CardTitle = new Font(bold, 13f);
            Button = new Font(bold, 10f);
            Label = new Font(plain, 9.5f);
            Tiny = new Font(bold, 8f);
            Body = new Font("Segoe UI", 9.75f);
            Symbol = new Font("Segoe UI Symbol", 9f);
        }

        static bool HasFont(string name)
        {
            try
            {
                using (var fc = new System.Drawing.Text.InstalledFontCollection())
                    return fc.Families.Any(f => f.Name == name);
            }
            catch { return false; }
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // A brass rivet with a little shine.
        public static void Rivet(Graphics g, float cx, float cy, float r)
        {
            using (var b = new SolidBrush(BrassDark)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            using (var b = new SolidBrush(Brass)) g.FillEllipse(b, cx - r * 0.78f, cy - r * 0.78f, r * 1.56f, r * 1.56f);
            using (var b = new SolidBrush(BrassLight)) g.FillEllipse(b, cx - r * 0.5f, cy - r * 0.55f, r * 0.62f, r * 0.62f);
        }

        // A cog wheel with a hole in the middle.
        public static void Gear(Graphics g, float cx, float cy, float r, int teeth, float angle, Color fill, Color edge, float edgeWidth)
        {
            var pts = new List<PointF>();
            int steps = teeth * 4;
            for (int i = 0; i < steps; i++)
            {
                float a = angle + (float)(Math.PI * 2 * i / steps);
                float rr = (i % 4 == 0 || i % 4 == 1) ? r : r * 0.78f;
                pts.Add(new PointF(cx + rr * (float)Math.Cos(a), cy + rr * (float)Math.Sin(a)));
            }
            using (var path = new GraphicsPath(FillMode.Alternate))
            {
                path.AddPolygon(pts.ToArray());
                float h = r * 0.32f;
                path.AddEllipse(cx - h, cy - h, h * 2, h * 2);
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (edgeWidth > 0)
                    using (var p = new Pen(edge, edgeWidth) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
            }
        }

        // Faint blueprint grid on cream paper. offsetY keeps the grid lined up across controls.
        public static void PaperGrid(Graphics g, Rectangle area, int offsetY)
        {
            using (var b = new SolidBrush(Paper)) g.FillRectangle(b, area);
            int step = Math.Max(8, S(16));
            using (var minor = new Pen(Grid))
            using (var major = new Pen(GridMajor))
            {
                for (int x = 0, i = 0; x < area.Right; x += step, i++)
                    g.DrawLine(i % 4 == 0 ? major : minor, x, area.Top, x, area.Bottom);
                int first = -((offsetY % step + step) % step);
                int idx = (offsetY + first) / step;
                for (int y = first; y < area.Bottom; y += step, idx++)
                    g.DrawLine(idx % 4 == 0 ? major : minor, area.Left, y, area.Right, y);
            }
        }

        // The on/off lever: pink track when on, grey when off, always with the word ON or OFF.
        public static void Toggle(Graphics g, RectangleF r, bool on, bool hover)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(r, r.Height / 2))
            {
                using (var b = new SolidBrush(on ? (hover ? Color.FromArgb(234, 126, 194) : Pink) : (hover ? Color.FromArgb(224, 216, 204) : OffTrack)))
                    g.FillPath(b, path);
                using (var p = new Pen(on ? PinkDark : InkMid, Math.Max(1f, 1.5f * K))) g.DrawPath(p, path);
            }
            float pad = 3 * K;
            float d = r.Height - pad * 2;
            var text = on ? "ON" : "OFF";
            var textArea = on
                ? new Rectangle((int)(r.X + pad * 2), (int)r.Y, (int)(r.Width - d - pad * 3), (int)r.Height)
                : new Rectangle((int)(r.X + d + pad * 2), (int)r.Y, (int)(r.Width - d - pad * 3), (int)r.Height);
            TextRenderer.DrawText(g, text, Tiny, textArea, on ? Color.White : Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            float kx = on ? r.Right - d - pad : r.X + pad;
            float ky = r.Y + pad;
            using (var b = new SolidBrush(Color.FromArgb(50, 0, 0, 0))) g.FillEllipse(b, kx, ky + K, d, d);
            using (var b = new SolidBrush(Steel1)) g.FillEllipse(b, kx, ky, d, d);
            using (var p = new Pen(Brass, 2 * K)) g.DrawEllipse(p, kx + K, ky + K, d - 2 * K, d - 2 * K);
            // a little rivet in the knob
            Rivet(g, kx + d / 2, ky + d / 2, d * 0.2f);
        }

        public static void FocusRing(Graphics g, RectangleF r, float rad, Color color)
        {
            using (var path = Round(r, rad))
            using (var p = new Pen(color, Math.Max(1f, 1.5f * K)) { DashStyle = DashStyle.Dash })
                g.DrawPath(p, path);
        }
    }

    enum StatusKind { Ok, Busy, Waiting, Off, Problem }

    // Top of the window: Ziri, the title, and two gears that turn while work is going on.
    class HeaderPlate : Control
    {
        readonly Image icon;
        readonly System.Windows.Forms.Timer spin;
        float angle;
        int jobs;

        public HeaderPlate(Image icon)
        {
            this.icon = icon;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(134);
            spin = new System.Windows.Forms.Timer { Interval = 33 };
            spin.Tick += (s, e) => { angle += 0.06f; Invalidate(GearArea()); };
        }

        public void Busy(bool on)
        {
            jobs = Math.Max(0, jobs + (on ? 1 : -1));
            if (jobs > 0) spin.Start();
            else spin.Stop();
        }

        Rectangle GearArea()
        {
            return new Rectangle(Width - Theme.S(132), 0, Theme.S(132), Theme.S(110));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < Theme.S(60) || Height < Theme.S(30)) return; // not laid out yet
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.PaperGrid(g, ClientRectangle, 0);
            float k = Theme.K;

            // gears (big one turns one way, the small one the other)
            float bx = Width - 60 * k, by = 56 * k;
            Theme.Gear(g, bx - 44 * k, by - 22 * k, 17 * k, 8, -angle * 1.7f + 0.2f, Theme.PinkLight, Theme.PinkDark, 1.6f * k);
            Theme.Gear(g, bx, by, 30 * k, 11, angle, Theme.Brass, Theme.BrassDark, 2f * k);
            Theme.Rivet(g, bx, by, 4 * k);

            if (icon != null) g.DrawImage(icon, 16 * k, 12 * k, 100 * k, 100 * k);
            int tx = Theme.S(128);
            var titleSize = TextRenderer.MeasureText("Ziri's Workshop", Theme.Title);
            TextRenderer.DrawText(g, "Ziri's Workshop", Theme.Title, new Point(tx, Theme.S(22)), Theme.Ink, TextFormatFlags.NoPadding);
            var sub = new Rectangle(tx + Theme.S(2), Theme.S(26) + titleSize.Height, Math.Max(80, Width - tx - Theme.S(140)), Theme.S(44));
            TextRenderer.DrawText(g, "Switch on the addons you want. They stay up to date by themselves.", Theme.Body, sub, Theme.InkMid,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            // brass pipe along the bottom, bolted down
            float py = Height - 14 * k;
            var pipe = new RectangleF(14 * k, py - 4 * k, Width - 28 * k, 8 * k);
            using (var path = Theme.Round(pipe, 4 * k))
            using (var b = new LinearGradientBrush(new RectangleF(pipe.X, pipe.Y - 1, pipe.Width, pipe.Height + 2), Theme.BrassLight, Theme.BrassDark, 90f))
            {
                g.FillPath(b, path);
                using (var p = new Pen(Theme.BrassDark, k)) g.DrawPath(p, path);
            }
            foreach (float x in new[] { pipe.X + 8 * k, pipe.X + pipe.Width / 3, pipe.X + pipe.Width * 2 / 3, pipe.Right - 8 * k })
                Theme.Rivet(g, x, py, 6 * k);
        }
    }

    // The list area: blueprint paper with a big faint cog in the corner.
    class PaperFlow : FlowLayoutPanel
    {
        public PaperFlow()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 1 || Height < 1) return;
            var g = e.Graphics;
            Theme.PaperGrid(g, ClientRectangle, Top);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = Theme.K;
            Theme.Gear(g, Width - 40 * k, Height - 30 * k, 90 * k, 14, 0.1f, Color.FromArgb(28, Theme.Brass), Color.Empty, 0);
            Theme.Gear(g, Width - 150 * k, Height - 6 * k, 44 * k, 9, 0.3f, Color.FromArgb(22, Theme.Pink), Color.Empty, 0);
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            Invalidate();
        }
    }

    // One addon on a riveted metal plate, with its on/off lever.
    class AddonCard : Control
    {
        public string Id;
        string title = "", desc = "", status = "";
        StatusKind kind;
        bool on, hover, hoverToggle;
        public event Action<AddonCard> ToggleClicked;

        public AddonCard(string id)
        {
            Id = id;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        public bool On { get { return on; } }

        public void Set(string t, string d, string s, StatusKind k, bool isOn)
        {
            title = t ?? ""; desc = d ?? ""; status = s ?? ""; kind = k; on = isOn;
            AccessibleName = title;
            AccessibleDescription = (on ? "On. " : "Off. ") + status + " " + desc;
            int h = Measure();
            if (Height != h) Height = h;
            Invalidate();
        }

        int Pad { get { return Theme.S(16); } }
        int ToggleW { get { return Theme.S(66); } }
        int TextX { get { return Pad + Theme.S(28); } }
        int TextW { get { return Math.Max(60, Width - TextX - ToggleW - Pad - Theme.S(14)); } }
        int TitleH { get { return TextRenderer.MeasureText("Ag", Theme.CardTitle).Height; } }

        // Measure and draw text the same way, so wrapped lines and heights always agree.
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

        static Size MeasureWrapped(string text, Font font, int width)
        {
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
                return TextRenderer.MeasureText(g, text, font, new Size(width, int.MaxValue), Wrap);
        }

        int DescH()
        {
            if (desc.Length == 0) return 0;
            return MeasureWrapped(desc, Theme.Body, TextW).Height;
        }

        int PillTextH()
        {
            return MeasureWrapped(status, Theme.Label, TextW - Theme.S(34)).Height;
        }

        int Measure()
        {
            int pill = Math.Max(Theme.S(26), PillTextH() + Theme.S(10));
            return Pad + TitleH + Theme.S(3) + DescH() + Theme.S(10) + pill + Pad + Theme.S(4);
        }

        Rectangle ToggleRect
        {
            get
            {
                int h = Theme.S(30);
                return new Rectangle(Width - Pad - ToggleW - Theme.S(4), Pad + (TitleH - h) / 2 + Theme.S(2), ToggleW, h);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int h = Measure();
            if (Height != h) Height = h;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = Theme.K;
            var r = new RectangleF(2 * k, 2 * k, Width - 6 * k, Height - 8 * k);
            if (r.Width < 30 * k || r.Height < 20 * k) return; // not laid out yet

            using (var shadow = Theme.Round(new RectangleF(r.X + k, r.Y + 4 * k, r.Width, r.Height), 12 * k))
            using (var b = new SolidBrush(Color.FromArgb(46, 110, 80, 40))) g.FillPath(b, shadow);
            using (var path = Theme.Round(r, 12 * k))
            {
                using (var b = new LinearGradientBrush(r, Theme.Steel1, Theme.Steel2, 90f)) g.FillPath(b, path);
                using (var p = new Pen(hover ? Theme.Brass : Theme.BrassDark, 2f * k)) g.DrawPath(p, path);
            }
            var inner = RectangleF.Inflate(r, -5 * k, -5 * k);
            using (var path = Theme.Round(inner, 8 * k))
            using (var p = new Pen(Color.FromArgb(70, Theme.Brass), k)) g.DrawPath(p, path);
            float ri = 11 * k;
            foreach (var pt in new[] { new PointF(r.X + ri, r.Y + ri), new PointF(r.Right - ri, r.Y + ri), new PointF(r.X + ri, r.Bottom - ri), new PointF(r.Right - ri, r.Bottom - ri) })
                Theme.Rivet(g, pt.X, pt.Y, 3.6f * k);

            int y = Pad;
            int th = TitleH;
            Theme.Gear(g, Pad + 12 * k, y + th / 2f + k, 10 * k, 8, 0.2f, on ? Theme.Pink : Theme.OffTrack, on ? Theme.PinkDark : Theme.InkMid, 1.4f * k);
            TextRenderer.DrawText(g, title, Theme.CardTitle, new Rectangle(TextX, y, TextW, th), Theme.Ink, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            y += th + Theme.S(3);
            int dh = DescH();
            if (dh > 0)
                TextRenderer.DrawText(g, desc, Theme.Body, new Rectangle(TextX, y, TextW, dh), Theme.InkMid, Wrap);
            y += dh + Theme.S(10);
            DrawPill(g, new Rectangle(TextX - Theme.S(2), y, TextW + Theme.S(2), Math.Max(Theme.S(26), PillTextH() + Theme.S(10))));

            Theme.Toggle(g, ToggleRect, on, hoverToggle);
            if (Focused && ShowFocusCues)
                Theme.FocusRing(g, RectangleF.Inflate(r, -3 * k, -3 * k), 10 * k, Theme.PinkDark);
        }

        void DrawPill(Graphics g, Rectangle area)
        {
            Color bg, edge;
            string glyph;
            switch (kind)
            {
                case StatusKind.Ok: bg = Theme.TealLight; edge = Theme.Teal; glyph = "✓"; break;
                case StatusKind.Busy: bg = Theme.PinkLight; edge = Theme.PinkDark; glyph = "⚙"; break;
                case StatusKind.Waiting: bg = Theme.Waiting; edge = Theme.BrassDark; glyph = "↻"; break;
                case StatusKind.Problem: bg = Theme.RustLight; edge = Theme.Rust; glyph = "!"; break;
                default: bg = Theme.OffLight; edge = Theme.InkMid; glyph = "○"; break;
            }
            int textH = PillTextH();
            int textW = MeasureWrapped(status, Theme.Label, TextW - Theme.S(34)).Width;
            var pill = new RectangleF(area.X, area.Y, Math.Min(area.Width, textW + Theme.S(36)), area.Height);
            using (var path = Theme.Round(pill, Math.Min(pill.Height / 2, 13 * Theme.K)))
            {
                using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                using (var p = new Pen(edge, 1.2f * Theme.K)) g.DrawPath(p, path);
            }
            var gl = new Rectangle((int)pill.X + Theme.S(6), (int)pill.Y, Theme.S(20), (int)pill.Height);
            TextRenderer.DrawText(g, glyph, kind == StatusKind.Problem ? Theme.Tiny : Theme.Symbol, gl, edge,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var tr = new Rectangle((int)pill.X + Theme.S(28), (int)pill.Y + ((int)pill.Height - textH) / 2, (int)pill.Width - Theme.S(34), textH);
            TextRenderer.DrawText(g, status, Theme.Label, tr, Theme.Ink, Wrap);
        }

        void Raise()
        {
            var h = ToggleClicked;
            if (h != null) h(this);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = ToggleRect.Contains(e.Location);
            if (over != hoverToggle)
            {
                hoverToggle = over;
                Cursor = over ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false; hoverToggle = false; Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Focus();
            if (e.Button == MouseButtons.Left && ToggleRect.Contains(e.Location)) Raise();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.Handled = true; Raise(); }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override AccessibleObject CreateAccessibilityInstance() { return new CardAccessible(this); }

        class CardAccessible : ControlAccessibleObject
        {
            readonly AddonCard card;
            public CardAccessible(AddonCard c) : base(c) { card = c; }
            public override AccessibleStates State { get { return base.State | (card.on ? AccessibleStates.Checked : AccessibleStates.None); } }
            public override string DefaultAction { get { return card.on ? "Switch off" : "Switch on"; } }
            public override void DoDefaultAction() { card.Raise(); }
        }
    }

    // A small lever with a label, for the workbench ("Start with Windows").
    class LeverSwitch : Control
    {
        bool on, hover;
        public event EventHandler CheckedChanged;

        public LeverSwitch(string text)
        {
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            ForeColor = Theme.Cream;
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
            AccessibleName = text;
            Cursor = Cursors.Hand;
            var sz = TextRenderer.MeasureText(text, Theme.Body);
            Size = new Size(Theme.S(58) + sz.Width + Theme.S(10), Math.Max(Theme.S(30), sz.Height + Theme.S(6)));
        }

        public bool Checked
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                Invalidate();
                var h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var tr = new RectangleF(Theme.S(2), (Height - Theme.S(24)) / 2f, Theme.S(50), Theme.S(24));
            Theme.Toggle(g, tr, on, hover);
            TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(Theme.S(60), 0, Width - Theme.S(60), Height), ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) Theme.FocusRing(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 6 * Theme.K, Theme.BrassLight);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); Focus(); if (e.Button == MouseButtons.Left) Checked = !Checked; }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space) { e.Handled = true; Checked = !Checked; }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    // A rounded brass button with dark ink text.
    class BrassButton : Button
    {
        bool hover, down;

        public BrassButton(string text)
        {
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = Theme.Button;
            Cursor = Cursors.Hand;
            var sz = TextRenderer.MeasureText(text, Theme.Button);
            Size = new Size(sz.Width + Theme.S(38), Math.Max(Theme.S(34), sz.Height + Theme.S(14)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (Parent != null) InvokePaintBackground(this, e);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = Theme.K;
            float off = down ? k : 0;
            var r = new RectangleF(k, k + off, Width - 3 * k, Height - 4 * k);
            if (r.Width < 4 * k || r.Height < 4 * k) return;
            using (var shadow = Theme.Round(new RectangleF(r.X, r.Y + 2 * k - off, r.Width, r.Height), 9 * k))
            using (var b = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillPath(b, shadow);
            using (var path = Theme.Round(r, 9 * k))
            {
                Color top = hover ? Color.FromArgb(252, 232, 170) : Theme.BrassLight;
                Color bottom = down ? Theme.BrassDark : Theme.Brass;
                using (var b = new LinearGradientBrush(r, top, bottom, 90f)) g.FillPath(b, path);
                using (var p = new Pen(Theme.BrassDark, 1.5f * k)) g.DrawPath(p, path);
            }
            Theme.Rivet(g, r.X + 8 * k, r.Y + r.Height / 2, 2.6f * k);
            Theme.Rivet(g, r.Right - 8 * k, r.Y + r.Height / 2, 2.6f * k);
            TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), Theme.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues) Theme.FocusRing(g, RectangleF.Inflate(r, -3 * k, -3 * k), 7 * k, Theme.Ink);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
    }

    // The workbench along the bottom: dark wood-and-iron strip with a brass edge.
    class Bench : TableLayoutPanel
    {
        public Bench()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            float k = Theme.K;
            // Windows can ask for a paint before the strip has a size; a gradient over nothing throws.
            if (Width < Theme.S(30) || Height < Theme.S(8))
            {
                using (var b = new SolidBrush(Theme.Bench2)) g.FillRectangle(b, ClientRectangle);
                return;
            }
            using (var b = new LinearGradientBrush(ClientRectangle, Theme.Bench1, Theme.Bench2, 90f)) g.FillRectangle(b, ClientRectangle);
            // planks
            using (var p = new Pen(Color.FromArgb(40, 0, 0, 0), k))
                for (float y = 34 * k; y < Height; y += 34 * k) g.DrawLine(p, 0, y, Width, y);
            using (var b = new LinearGradientBrush(new Rectangle(0, 0, Width, Theme.S(6)), Theme.BrassLight, Theme.BrassDark, 90f))
                g.FillRectangle(b, 0, 0, Width, Theme.S(6));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Rivet(g, 12 * k, 3 * k, 4 * k);
            Theme.Rivet(g, Width - 12 * k, 3 * k, 4 * k);
        }
    }

    class MainForm : Form
    {
        readonly Workshop ws;
        readonly NotifyIcon tray;
        readonly HeaderPlate header;
        readonly PaperFlow list;
        readonly Label pathLabel;
        readonly Label statusLabel;
        readonly LeverSwitch startSwitch;
        readonly Dictionary<string, AddonCard> cards = new Dictionary<string, AddonCard>();
        readonly System.Windows.Forms.Timer every3h;
        readonly System.Windows.Forms.Timer firstCheck;
        bool allowClose;
        bool busy;
        string pendingCheck;
        bool snapshotTaken;

        public MainForm(Workshop workshop)
        {
            ws = workshop;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Text = Util.AppName;
            Font = Theme.Body;
            BackColor = Theme.Paper;
            ForeColor = Theme.Ink;
            // tall enough for three plates, but never taller than the screen
            var work = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(Theme.S(600), work.Width - 40), Math.Min(Theme.S(760), work.Height - 60));
            MinimumSize = new Size(Theme.S(500), Theme.S(500));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = LoadIcon(32);
            DoubleBuffered = true;

            var big = LoadIcon(256);
            header = new HeaderPlate(big == null ? null : big.ToBitmap()) { Dock = DockStyle.Top };

            list = new PaperFlow
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(Theme.S(14), Theme.S(6), Theme.S(14), Theme.S(10))
            };
            list.Resize += (s, e) => FitCards();

            var bench = new Bench { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(Theme.S(16), Theme.S(16), Theme.S(16), Theme.S(10)) };
            bench.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bench.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bench.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            bench.Controls.Add(new Label { Text = "WoW folder", AutoSize = true, Font = Theme.Tiny, ForeColor = Theme.CreamDim, BackColor = Color.Transparent, Anchor = AnchorStyles.Left, Margin = new Padding(0, Theme.S(10), Theme.S(8), 0) }, 0, 0);
            pathLabel = new Label { AutoSize = false, AutoEllipsis = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = TextRenderer.MeasureText("Ag", Theme.Body).Height + Theme.S(2), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Cream, BackColor = Color.Transparent };
            bench.Controls.Add(pathLabel, 1, 0);
            var change = new BrassButton("Change...") { Anchor = AnchorStyles.Right, Margin = new Padding(Theme.S(6), 0, 0, Theme.S(4)) };
            change.Click += (s, e) => PickFolder();
            bench.Controls.Add(change, 2, 0);

            startSwitch = new LeverSwitch("Start with Windows (recommended, so updates arrive by themselves)") { Margin = new Padding(0, Theme.S(4), 0, Theme.S(4)) };
            startSwitch.Checked = ws.Settings.StartWithWindows;
            startSwitch.CheckedChanged += (s, e) =>
            {
                ws.Settings.StartWithWindows = startSwitch.Checked;
                ws.SaveSettings();
                ws.ApplyStartWithWindows();
            };
            bench.Controls.Add(startSwitch, 0, 1);
            bench.SetColumnSpan(startSwitch, 3);

            statusLabel = new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Cream, BackColor = Color.Transparent };
            bench.Controls.Add(statusLabel, 0, 2);
            bench.SetColumnSpan(statusLabel, 2);
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, Theme.S(4), 0, 0), BackColor = Color.Transparent };
            var checkBtn = new BrassButton("Check now") { Margin = new Padding(Theme.S(6), 0, 0, 0) };
            checkBtn.Click += (s, e) => RunCheck("button");
            var hideBtn = new BrassButton("Hide") { Margin = new Padding(Theme.S(8), 0, 0, 0) };
            hideBtn.Click += (s, e) => HideToTray();
            buttons.Controls.Add(checkBtn);
            buttons.Controls.Add(hideBtn);
            bench.Controls.Add(buttons, 2, 2);

            var version = new Label { Text = "Version " + Util.AppVersion, AutoSize = true, Font = Theme.Tiny, ForeColor = Theme.CreamDim, BackColor = Color.Transparent, Margin = new Padding(0, Theme.S(6), 0, 0) };
            bench.Controls.Add(version, 0, 3);
            bench.SetColumnSpan(version, 3);

            Controls.Add(list);
            Controls.Add(bench);
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
            header.Busy(true);
            var t = new Thread(() =>
            {
                try { work(); }
                catch (Exception e) { Util.Log("worker: " + e); }
                finally { Ui(() => header.Busy(false)); }
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
                if (cards.Count == 0 && list.Controls.Count == 0)
                    list.Controls.Add(new Label { Text = "Getting the list of addons...", AutoSize = true, ForeColor = Theme.InkMid, BackColor = Color.Transparent, Margin = new Padding(Theme.S(6), Theme.S(12), 0, 0) });
                return;
            }
            var ids = m.addons.Select(a => a.id).ToList();
            if (!ids.SequenceEqual(cards.Keys))
            {
                list.SuspendLayout();
                list.Controls.Clear();
                cards.Clear();
                foreach (var a in m.addons)
                {
                    var c = new AddonCard(a.id) { Margin = new Padding(0, Theme.S(6), 0, Theme.S(4)) };
                    c.ToggleClicked += OnToggle;
                    cards[a.id] = c;
                    list.Controls.Add(c);
                }
                list.ResumeLayout();
                FitCards();
            }
            foreach (var a in m.addons) UpdateCard(cards[a.id], a);
        }

        void FitCards()
        {
            int w = list.ClientSize.Width - list.Padding.Horizontal - Theme.S(4);
            if (w < 200) return;
            foreach (var c in cards.Values)
                if (c.Width != w) c.Width = w;
        }

        void UpdateCard(AddonCard c, AddonInfo a)
        {
            bool wanted = ws.IsWanted(a.id);
            string have = ws.InstalledVersion(a.id);
            string note;
            string text;
            StatusKind kind;
            if (ws.Notes.TryGetValue(a.id, out note) && note != null)
            {
                text = note;
                kind = note.StartsWith("Couldn't") ? StatusKind.Problem : StatusKind.Busy;
            }
            else if (wanted)
            {
                if (have == null) { text = "Not installed yet."; kind = StatusKind.Waiting; }
                else if (Util.Newer(a.version, have)) { text = "Version " + have + ". Update to " + a.version + " waiting."; kind = StatusKind.Waiting; }
                else { text = "Version " + have + ", up to date."; kind = StatusKind.Ok; }
            }
            else
            {
                kind = StatusKind.Off;
                text = have == null
                    ? "Not installed. Switch it on to add it."
                    : "Version " + have + " is installed but switched off, so it isn't kept up to date.";
            }
            c.Set(a.name ?? a.id, a.description, text, kind, wanted);
        }

        void OnToggle(AddonCard c)
        {
            var a = ws.Find(c.Id);
            string name = a != null && a.name != null ? a.name : c.Id;
            if (!c.On)
            {
                ws.Settings.Wanted[c.Id] = true;
                ws.SaveSettings();
                RunWorker(() => ws.InstallOne(c.Id));
            }
            else
            {
                if (ws.InstalledVersion(c.Id) != null)
                {
                    var answer = MessageBox.Show(this,
                        "Switch off " + name + "?\n\nIts folder goes to the Recycle Bin. Switch it on again any time to get it back.",
                        Util.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes) return;
                }
                ws.Settings.Wanted[c.Id] = false;
                ws.SaveSettings();
                RunWorker(() => ws.RemoveOne(c.Id));
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

        // Test helper: a screen picture of the window, then quit.
        void TakeSnapshotIfAsked()
        {
            if (Opts.Snapshot == null || snapshotTaken) return;
            snapshotTaken = true;
            TopMost = true;
            Activate();
            var t = new System.Windows.Forms.Timer { Interval = 900 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                try
                {
                    var b = Bounds;
                    using (var bmp = new Bitmap(b.Width, b.Height))
                    {
                        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
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

                // A drawing hiccup should never put a scary dialog in front of a friend: log it and carry on.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Util.Log("ui error: " + e.Exception);
                try { SetProcessDPIAware(); } catch { }
                using (var screen = Graphics.FromHwnd(IntPtr.Zero)) Theme.Init(screen.DpiX);
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
