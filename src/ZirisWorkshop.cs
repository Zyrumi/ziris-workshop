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
//   - an addon that "replaces" older ones (Ziri's Toolkit replaces Loot
//     Highlight and Fresh Ranks) is switched on for everyone who had one of
//     them; the old folders go to the Recycle Bin and their saved settings
//     are carried over
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
        public List<string> replaces { get; set; } // older addons this one takes over from
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
        public bool SetupDone { get; set; }      // the settings box was seen once
        public Dictionary<string, string> Retire { get; set; } // old addon id -> the addon replacing it

        public Settings()
        {
            Wanted = new Dictionary<string, bool>();
            Retire = new Dictionary<string, string>();
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
        public static string SnapshotSettings; // --snapshot-settings FILE.png (test: picture of the settings box)
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
                    case "--snapshot-settings": SnapshotSettings = next; i++; break;
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
        public const string AppVersion = "1.2.1";
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
                        if (s.Retire == null) s.Retire = new Dictionary<string, string>();
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
                SetStatus("Can't find WoW Forever. Open Settings (the cog at the top) and pick your World of Warcraft folder.");
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
            foreach (var a in m.addons) TakeOverLocked(a);
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
            RetireLocked();
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

        // ---------------- Replacing older addons ----------------

        static List<string> Replaces(AddonInfo a)
        {
            if (a.replaces == null) return new List<string>();
            return a.replaces.Where(r => Util.SafeId(r) && r != a.id).Distinct().ToList();
        }

        // Someone who had one of the addons this one replaces gets this one
        // switched on, and the old ones are queued to be retired.
        void TakeOverLocked(AddonInfo a)
        {
            var had = Replaces(a).Where(r => IsWanted(r)
                || (!Settings.Wanted.ContainsKey(r) && InstalledVersion(r) != null)).ToList();
            if (had.Count == 0) return;
            Settings.Wanted[a.id] = true;
            foreach (string r in had)
            {
                Settings.Wanted[r] = false;
                Settings.Retire[r] = a.id;
            }
            Util.Log(a.id + " takes over from " + string.Join(", ", had));
        }

        // Once the replacement is installed: carry the old saved settings
        // over, then the old folder goes to the Recycle Bin.
        void RetireLocked()
        {
            if (AddOnsDir == null || Settings.Retire.Count == 0) return;
            foreach (var kv in Settings.Retire.ToList())
            {
                string old = kv.Key, by = kv.Value;
                if (!Util.SafeId(old) || !Util.SafeId(by)) { Settings.Retire.Remove(old); continue; }
                if (InstalledVersion(by) == null) continue; // wait until it is in place
                try
                {
                    CarrySaves(old, by);
                    string target = Path.Combine(AddOnsDir, old);
                    if (Directory.Exists(target))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(target,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    Settings.Retire.Remove(old);
                    Settings.Wanted.Remove(old);
                    Util.Log("retired " + old + " (replaced by " + by + ", folder to the Recycle Bin)");
                }
                catch (Exception e)
                {
                    Util.Log("retire " + old + " failed: " + e);
                }
            }
            Raise();
        }

        // WoW keeps an addon's settings in WTF\Account\...\SavedVariables\<id>.lua
        // (also per realm and character). Where the new addon declares the
        // same saved variables, the old file is appended to the new one, unless
        // the new one already has them.
        public void CarrySaves(string old, string by)
        {
            string wtf = Path.Combine(Settings.ForeverPath, "WTF", "Account");
            if (!Directory.Exists(wtf)) return;
            string toc = Path.Combine(AddOnsDir, by, by + ".toc");
            var declared = new HashSet<string>();
            foreach (string key in new[] { "SavedVariables", "SavedVariablesPerCharacter" })
                foreach (string v in (Util.TocValue(toc, key) ?? "").Split(','))
                    if (v.Trim().Length > 0) declared.Add(v.Trim());
            if (declared.Count == 0) return;

            var assigned = new Regex(@"^([A-Za-z_][A-Za-z0-9_]*)\s*=", RegexOptions.Multiline);
            foreach (string file in Directory.EnumerateFiles(wtf, old + ".lua", SearchOption.AllDirectories))
            {
                string dir = Path.GetDirectoryName(file);
                if (!string.Equals(Path.GetFileName(dir), "SavedVariables", StringComparison.OrdinalIgnoreCase)) continue;
                string text = File.ReadAllText(file);
                var carry = assigned.Matches(text).Cast<Match>().Select(x => x.Groups[1].Value)
                    .Where(v => declared.Contains(v)).ToList();
                if (carry.Count == 0) continue;
                string target = Path.Combine(dir, by + ".lua");
                string have = File.Exists(target) ? File.ReadAllText(target) : "";
                if (assigned.Matches(have).Cast<Match>().Any(x => carry.Contains(x.Groups[1].Value))) continue;
                if (have.Length > 0 && !have.EndsWith("\n")) have += "\r\n";
                File.WriteAllText(target, have + text);
                Util.Log("carried " + string.Join(", ", carry) + " from " + file + " to " + by + ".lua");
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

    // Little helpers for a window without the Windows title bar.
    static class Native
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        public const int WS_MINIMIZEBOX = 0x20000, WS_SYSMENU = 0x80000, CS_DROPSHADOW = 0x20000;

        // Drag the whole window from a plain area (header, bottom strip).
        public static void DragWindow(Control from)
        {
            var f = from.FindForm();
            if (f == null) return;
            ReleaseCapture();
            SendMessage(f.Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
        }

        // Windows 11 rounds the corners when asked; older Windows just keeps them square.
        public static void RoundCorners(IntPtr h)
        {
            try { int round = 2; DwmSetWindowAttribute(h, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4); } catch { }
        }
    }

    // Top of the window: Ziri, the title, two gears that turn while work is going on,
    // and the Settings and Hide buttons. There is no Windows title bar, so you drag the window from here.
    class HeaderPlate : Control
    {
        readonly Image icon;
        readonly System.Windows.Forms.Timer spin;
        float angle;
        int jobs;
        public readonly RoundButton SettingsButton = new RoundButton(RoundButton.Kind.Settings, "Settings");
        public readonly RoundButton HideButton = new RoundButton(RoundButton.Kind.Hide, "Hide (keeps running in the tray)");

        public HeaderPlate(Image icon)
        {
            this.icon = icon;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(118);
            Controls.Add(SettingsButton);
            Controls.Add(HideButton);
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
            return new Rectangle(Width - Theme.S(112), Theme.S(42), Theme.S(112), Theme.S(64));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int top = Theme.S(10), right = Width - Theme.S(12);
            HideButton.Location = new Point(right - HideButton.Width, top);
            SettingsButton.Location = new Point(HideButton.Left - Theme.S(6) - SettingsButton.Width, top);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Native.DragWindow(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < Theme.S(60) || Height < Theme.S(30)) return; // not laid out yet
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.PaperGrid(g, ClientRectangle, 0);
            float k = Theme.K;

            // gears (big one turns one way, the small one the other)
            float bx = Width - 50 * k, by = 74 * k;
            Theme.Gear(g, bx - 37 * k, by - 6 * k, 13 * k, 8, -angle * 1.7f + 0.2f, Theme.PinkLight, Theme.PinkDark, 1.5f * k);
            Theme.Gear(g, bx, by, 24 * k, 11, angle, Theme.Brass, Theme.BrassDark, 1.8f * k);
            Theme.Rivet(g, bx, by, 3.5f * k);

            if (icon != null) g.DrawImage(icon, 12 * k, 8 * k, 88 * k, 88 * k);
            int tx = Theme.S(110);
            var titleSize = TextRenderer.MeasureText("Ziri's Workshop", Theme.Title);
            TextRenderer.DrawText(g, "Ziri's Workshop", Theme.Title, new Point(tx, Theme.S(16)), Theme.Ink, TextFormatFlags.NoPadding);
            var sub = new Rectangle(tx + Theme.S(2), Theme.S(20) + titleSize.Height, Math.Max(80, Width - tx - Theme.S(128)), Theme.S(42));
            TextRenderer.DrawText(g, "Switch on the addons you want. They stay up to date by themselves.", Theme.Body, sub, Theme.InkMid,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            // brass pipe along the bottom, bolted down
            float py = Height - 11 * k;
            var pipe = new RectangleF(12 * k, py - 4 * k, Width - 24 * k, 8 * k);
            if (pipe.Width < 20 * k) return;
            using (var path = Theme.Round(pipe, 4 * k))
            using (var b = new LinearGradientBrush(new RectangleF(pipe.X, pipe.Y - 1, pipe.Width, pipe.Height + 2), Theme.BrassLight, Theme.BrassDark, 90f))
            {
                g.FillPath(b, path);
                using (var p = new Pen(Theme.BrassDark, k)) g.DrawPath(p, path);
            }
            foreach (float x in new[] { pipe.X + 8 * k, pipe.X + pipe.Width / 3, pipe.X + pipe.Width * 2 / 3, pipe.Right - 8 * k })
                Theme.Rivet(g, x, py, 5.5f * k);
        }
    }

    // A small round brass button with a drawn symbol: a cog for Settings, a cross for Hide.
    class RoundButton : Button
    {
        public enum Kind { Settings, Hide }
        readonly Kind kind;
        bool hover, down;
        readonly ToolTip tip = new ToolTip();

        public RoundButton(Kind kind, string name)
        {
            this.kind = kind;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(Theme.S(30), Theme.S(30));
            Cursor = Cursors.Hand;
            Text = "";
            AccessibleName = name;
            tip.SetToolTip(this, name);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (Parent != null) InvokePaintBackground(this, e);
            float k = Theme.K;
            float off = down ? k : 0;
            var r = new RectangleF(k, k + off, Width - 3 * k, Height - 3 * k);
            if (r.Width < 6 * k) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Color.FromArgb(60, 110, 80, 40))) g.FillEllipse(b, r.X, r.Y + 1.5f * k - off, r.Width, r.Height);
            using (var b = new LinearGradientBrush(r, hover ? Color.FromArgb(252, 232, 170) : Theme.BrassLight, down ? Theme.BrassDark : Theme.Brass, 90f))
                g.FillEllipse(b, r);
            using (var p = new Pen(Theme.BrassDark, 1.3f * k)) g.DrawEllipse(p, r);
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            if (kind == Kind.Settings)
                Theme.Gear(g, cx, cy, r.Width * 0.3f, 8, 0.3f, Theme.Ink, Color.Empty, 0);
            else
                using (var p = new Pen(Theme.Ink, 2.2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    float d = r.Width * 0.19f;
                    g.DrawLine(p, cx - d, cy - d, cx + d, cy + d);
                    g.DrawLine(p, cx - d, cy + d, cx + d, cy - d);
                }
            if (Focused && ShowFocusCues) Theme.FocusRing(g, RectangleF.Inflate(r, -2 * k, -2 * k), r.Width / 2, Theme.Ink);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
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

    // One addon as a compact riveted plate: name, status, description and the lever.
    class AddonCard : Control
    {
        public string Id;
        string title = "", desc = "", status = "", fullStatus = "";
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
        public string FullStatus { get { return fullStatus; } }

        // shortStatus goes in the little pill, longStatus in the tooltip and for screen readers
        public void Set(string t, string d, string shortStatus, string longStatus, StatusKind k, bool isOn)
        {
            title = t ?? ""; desc = d ?? ""; status = shortStatus ?? ""; fullStatus = longStatus ?? ""; kind = k; on = isOn;
            AccessibleName = title;
            AccessibleDescription = (on ? "On. " : "Off. ") + fullStatus + " " + desc;
            int h = Measure();
            if (Height != h) Height = h;
            Invalidate();
        }

        int Pad { get { return Theme.S(11); } }
        int Side { get { return Theme.S(14); } }
        int Shadow { get { return Theme.S(5); } }
        int ToggleW { get { return Theme.S(66); } }
        int TextX { get { return Side + Theme.S(4); } }
        int TextW { get { return Math.Max(60, Width - TextX - ToggleW - Side - Theme.S(14)); } }
        int RowH { get { return Math.Max(TextRenderer.MeasureText("Ag", Theme.CardTitle).Height, Theme.S(22)); } }

        // Measure and draw text the same way, so wrapped lines and heights always agree.
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;
        const TextFormatFlags Line = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;

        static Size MeasureWith(string text, Font font, int width, TextFormatFlags flags)
        {
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
                return TextRenderer.MeasureText(g, text, font, new Size(width, int.MaxValue), flags);
        }

        int DescH() { return desc.Length == 0 ? 0 : MeasureWith(desc, Theme.Body, TextW, Wrap).Height; }
        int ContentH() { return RowH + (desc.Length == 0 ? 0 : Theme.S(2) + DescH()); }
        int Measure() { return Math.Max(Theme.S(30), ContentH()) + 2 * Pad + Shadow; }

        Rectangle ToggleRect
        {
            get
            {
                int h = Theme.S(30);
                return new Rectangle(Width - Side - ToggleW - Theme.S(2), (Height - Shadow - h) / 2 + Theme.S(1), ToggleW, h);
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
            var r = new RectangleF(2 * k, 2 * k, Width - 6 * k, Height - Shadow - 1 * k);
            if (r.Width < 30 * k || r.Height < 20 * k) return; // not laid out yet

            using (var shadow = Theme.Round(new RectangleF(r.X + k, r.Y + 3.5f * k, r.Width, r.Height), 10 * k))
            using (var b = new SolidBrush(Color.FromArgb(46, 110, 80, 40))) g.FillPath(b, shadow);
            using (var path = Theme.Round(r, 10 * k))
            {
                using (var b = new LinearGradientBrush(r, Theme.Steel1, Theme.Steel2, 90f)) g.FillPath(b, path);
                using (var p = new Pen(hover ? Theme.Brass : Theme.BrassDark, 1.8f * k)) g.DrawPath(p, path);
            }
            float ri = 7.5f * k;
            foreach (var pt in new[] { new PointF(r.X + ri, r.Y + ri), new PointF(r.Right - ri, r.Y + ri), new PointF(r.X + ri, r.Bottom - ri), new PointF(r.Right - ri, r.Bottom - ri) })
                Theme.Rivet(g, pt.X, pt.Y, 2.6f * k);

            float mid = r.Y + r.Height / 2;

            // name, then the status pill on the same line, then the description
            int y = (int)(mid - ContentH() / 2f);
            int titleW = Math.Min(TextW, TextRenderer.MeasureText(title, Theme.CardTitle, Size.Empty, TextFormatFlags.NoPadding).Width);
            TextRenderer.DrawText(g, title, Theme.CardTitle, new Rectangle(TextX, y, titleW + Theme.S(2), RowH), Theme.Ink, Line);
            int px = TextX + titleW + Theme.S(10);
            int room = TextX + TextW - px;
            if (status.Length > 0 && room >= Theme.S(56))
                DrawPill(g, new Rectangle(px, y + (RowH - Theme.S(22)) / 2, room, Theme.S(22)));
            if (desc.Length > 0)
                TextRenderer.DrawText(g, desc, Theme.Body, new Rectangle(TextX, y + RowH + Theme.S(2), TextW, DescH()), Theme.InkMid, Wrap);

            Theme.Toggle(g, ToggleRect, on, hoverToggle);
            if (Focused && ShowFocusCues)
                Theme.FocusRing(g, RectangleF.Inflate(r, -3 * k, -3 * k), 8 * k, Theme.PinkDark);
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
            int textW = TextRenderer.MeasureText(status, Theme.Label, Size.Empty, TextFormatFlags.NoPadding).Width;
            var pill = new RectangleF(area.X, area.Y, Math.Min(area.Width, textW + Theme.S(34)), area.Height);
            using (var path = Theme.Round(pill, pill.Height / 2))
            {
                using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                using (var p = new Pen(edge, 1.1f * Theme.K)) g.DrawPath(p, path);
            }
            var gl = new Rectangle((int)pill.X + Theme.S(5), (int)pill.Y, Theme.S(18), (int)pill.Height);
            TextRenderer.DrawText(g, glyph, kind == StatusKind.Problem ? Theme.Tiny : Theme.Symbol, gl, edge,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var tr = new Rectangle((int)pill.X + Theme.S(24), (int)pill.Y, (int)pill.Width - Theme.S(31), (int)pill.Height);
            TextRenderer.DrawText(g, status, Theme.Label, tr, Theme.Ink, Line);
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
        public int TextLeft = Theme.S(60); // where the words start, so they can line up with other rows

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
            TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(TextLeft, 0, Width - TextLeft, Height), ForeColor,
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

    // The dark workbench strip at the bottom: version, last check, and Check now.
    class Bench : Control
    {
        public readonly BrassButton CheckNow = new BrassButton("Check now");
        string status = "";
        readonly string version;

        static int Pad { get { return Theme.S(18); } }
        static int Edge { get { return Theme.S(6); } }   // the brass edge on top

        public Bench(string version)
        {
            this.version = "Version " + version;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Edge + Theme.S(52);
            Controls.Add(CheckNow);
            AccessibleRole = AccessibleRole.StatusBar;
        }

        public string StatusText { set { if (value != status) { status = value ?? ""; AccessibleName = status; Invalidate(); } } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            CheckNow.Location = new Point(Width - Pad - CheckNow.Width, Edge + (Height - Edge - CheckNow.Height) / 2);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Native.DragWindow(this);
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
            using (var b = new LinearGradientBrush(new Rectangle(0, 0, Width, Edge), Theme.BrassLight, Theme.BrassDark, 90f))
                g.FillRectangle(b, 0, 0, Width, Edge);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Rivet(g, 12 * k, 3 * k, 4 * k);
            Theme.Rivet(g, Width - 12 * k, 3 * k, 4 * k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < Theme.S(100)) return;
            var g = e.Graphics;
            const TextFormatFlags Line = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            var row = new Rectangle(Pad, Edge, Width - 2 * Pad, Height - Edge);
            int vw = TextRenderer.MeasureText(version, Theme.Tiny, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, version, Theme.Tiny, new Rectangle(row.X, row.Y, vw + Theme.S(2), row.Height), Theme.CreamDim, Line);
            int sx = row.X + vw + Theme.S(10);
            using (var b = new SolidBrush(Theme.Brass)) g.FillEllipse(b, sx, row.Y + row.Height / 2f - 2 * Theme.K, 4 * Theme.K, 4 * Theme.K);
            sx += Theme.S(14);
            TextRenderer.DrawText(g, status, Theme.Body, new Rectangle(sx, row.Y, Math.Max(10, CheckNow.Left - Theme.S(12) - sx), row.Height), Theme.Cream, Line);
        }
    }

    // Settings in a small box of its own: the WoW folder and Start with Windows.
    // It also opens by itself the first time, so the folder gets confirmed before anything else.
    class SettingsForm : Form
    {
        readonly bool firstRun;
        public string ForeverPath;
        public readonly LeverSwitch StartSwitch;
        readonly BrassButton change, done;
        readonly int pad, titleY, introY, introH, captionY, noteY;
        Rectangle pathBox;

        const string Intro = "Check that this is your WoW Forever folder, then switch on the addons you want.";
        const string Note = "Recommended, so updates arrive by themselves.";

        public SettingsForm(string path, bool startWithWindows, bool firstRun)
        {
            this.firstRun = firstRun;
            ForeverPath = path;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Theme.Paper;
            Font = Theme.Body;
            Text = firstRun ? "Welcome" : "Settings";

            int w = Theme.S(470);
            pad = Theme.S(22);
            change = new BrassButton("Change...");
            done = new BrassButton(firstRun ? "Looks good" : "Done");
            StartSwitch = new LeverSwitch("Start with Windows") { ForeColor = Theme.Ink };
            StartSwitch.Checked = startWithWindows;

            int y = pad;
            titleY = y;
            y += TextRenderer.MeasureText("Ag", Theme.CardTitle).Height + Theme.S(4);
            if (firstRun)
            {
                introY = y;
                introH = TextRenderer.MeasureText(Intro, Theme.Body, new Size(w - 2 * pad, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
                y += introH;
            }
            y += Theme.S(16);
            captionY = y;
            y += TextRenderer.MeasureText("Ag", Theme.Tiny).Height + Theme.S(6);
            int boxH = Theme.S(36);
            pathBox = new Rectangle(pad, y, w - 2 * pad - change.Width - Theme.S(10), boxH);
            change.Location = new Point(w - pad - change.Width, y + (boxH - change.Height) / 2);
            y += boxH + Theme.S(18);
            StartSwitch.Location = new Point(pad - Theme.S(2), y);
            StartSwitch.Width = w - 2 * pad;
            y += StartSwitch.Height;
            noteY = y;
            y += TextRenderer.MeasureText("Ag", Theme.Body).Height + Theme.S(20);
            done.Location = new Point(w - pad - done.Width, y);
            y += done.Height + pad;
            ClientSize = new Size(w, y);

            Controls.Add(change);
            Controls.Add(StartSwitch);
            Controls.Add(done);
            change.Click += (s, e) => PickFolder();
            done.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            AcceptButton = done;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; done.PerformClick(); } };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= Native.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Native.DragWindow(this);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.PaperGrid(g, ClientRectangle, 0);
            using (var p = new Pen(Theme.BrassDark, Math.Max(1, Theme.S(2)))) g.DrawRectangle(p, Theme.S(1), Theme.S(1), Width - Theme.S(2) - 1, Height - Theme.S(2) - 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = Theme.K;
            int w = ClientSize.Width - 2 * pad;
            TextRenderer.DrawText(g, firstRun ? "Welcome to Ziri's Workshop" : "Settings", Theme.CardTitle, new Point(pad, titleY), Theme.Ink, TextFormatFlags.NoPadding);
            if (firstRun)
                TextRenderer.DrawText(g, Intro, Theme.Body, new Rectangle(pad, introY, w, introH), Theme.InkMid, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "WoW Forever folder", Theme.Tiny, new Point(pad, captionY), Theme.InkMid, TextFormatFlags.NoPadding);

            bool found = !string.IsNullOrEmpty(ForeverPath);
            using (var path = Theme.Round(pathBox, 8 * k))
            {
                using (var b = new SolidBrush(found ? Theme.Steel1 : Theme.RustLight)) g.FillPath(b, path);
                using (var p = new Pen(found ? Theme.BrassDark : Theme.Rust, 1.3f * k)) g.DrawPath(p, path);
            }
            var inner = Rectangle.Inflate(pathBox, -Theme.S(10), 0);
            TextRenderer.DrawText(g, found ? ForeverPath : "! Not found yet. Click Change and pick your World of Warcraft folder.", Theme.Body, inner, Theme.Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);

            TextRenderer.DrawText(g, Note, Theme.Body, new Point(StartSwitch.Left + StartSwitch.TextLeft, noteY), Theme.InkMid, TextFormatFlags.NoPadding);
        }

        void PickFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Pick your World of Warcraft folder (the one with a _forever_ folder inside).";
                dlg.ShowNewFolderButton = false;
                if (!string.IsNullOrEmpty(ForeverPath)) dlg.SelectedPath = ForeverPath;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string f = Workshop.NormalizeForever(dlg.SelectedPath);
                if (f == null)
                {
                    MessageBox.Show(this, "Couldn't find WoW Forever there.\n\nPick the World of Warcraft folder that has a _forever_ folder inside.",
                        Util.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                ForeverPath = f;
                Invalidate();
            }
        }
    }

    class MainForm : Form
    {
        readonly Workshop ws;
        readonly NotifyIcon tray;
        readonly HeaderPlate header;
        readonly PaperFlow list;
        readonly Bench bench;
        readonly ToolTip tips = new ToolTip();
        bool settingsOpen;
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
            // no Windows title bar: the header is the top, and the window grows with the list
            FormBorderStyle = FormBorderStyle.None;
            Padding = new Padding(Math.Max(1, Theme.S(1)));
            var work = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(Theme.S(600), work.Width - 40), Math.Min(Theme.S(460), work.Height - 60));
            // Windows sometimes squeezed the window to a sliver (seen at 2 px
            // wide, likely when screens sleep or wake); never smaller than this.
            MinimumSize = new Size(Math.Min(Theme.S(420), work.Width - 40), Math.Min(Theme.S(240), work.Height - 60));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = LoadIcon(32);
            DoubleBuffered = true;

            var big = LoadIcon(256);
            header = new HeaderPlate(big == null ? null : big.ToBitmap()) { Dock = DockStyle.Top };

            list = new PaperFlow
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(Theme.S(12), Theme.S(8), Theme.S(12), Theme.S(8))
            };
            list.Resize += (s, e) => FitCards();

            header.SettingsButton.Click += (s, e) => OpenSettings(false);
            header.HideButton.Click += (s, e) => HideToTray();
            bench = new Bench(Util.AppVersion) { Dock = DockStyle.Bottom };
            bench.CheckNow.Click += (s, e) => RunCheck("button");

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

            Shown += (s, e) =>
            {
                if (Opts.SnapshotSettings != null) SnapshotSettingsBox();
                else MaybeFirstRun();
            };

            ws.WatchSaves();
            RefreshAll();
            ResumeLayout(true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= Native.WS_MINIMIZEBOX | Native.WS_SYSMENU; // taskbar click still minimizes
                cp.ClassStyle |= Native.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            using (var p = new Pen(Theme.BrassDark)) e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        // Grow or shrink with the list: room for about six addons, then it scrolls.
        void FitWindow()
        {
            var work = Screen.FromControl(this).WorkingArea;
            int content = list.Padding.Vertical;
            foreach (Control c in list.Controls) content += c.Height + c.Margin.Vertical;
            int chrome = header.Height + bench.Height + Padding.Vertical;
            int maxList = Math.Max(Theme.S(120), Math.Min(Theme.S(560), work.Height - 40 - chrome));
            int h = chrome + Math.Max(Theme.S(90), Math.Min(content, maxList));
            if (ClientSize.Height == h) return;
            ClientSize = new Size(ClientSize.Width, h);
            if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
        }

        void MaybeFirstRun()
        {
            if (ws.Settings.SetupDone || Opts.Snapshot != null || !Visible) return;
            BeginInvoke(new Action(() => OpenSettings(true)));
        }

        void OpenSettings(bool firstRun)
        {
            if (settingsOpen) return;
            settingsOpen = true;
            try
            {
                string path = ws.Settings.ForeverPath ?? Workshop.FindForever();
                using (var f = new SettingsForm(path, ws.Settings.StartWithWindows, firstRun))
                {
                    f.ShowDialog(this);
                    bool folderChanged = f.ForeverPath != null && !string.Equals(f.ForeverPath, ws.Settings.ForeverPath, StringComparison.OrdinalIgnoreCase);
                    if (folderChanged) ws.Settings.ForeverPath = f.ForeverPath;
                    if (ws.Settings.StartWithWindows != f.StartSwitch.Checked)
                    {
                        ws.Settings.StartWithWindows = f.StartSwitch.Checked;
                        ws.ApplyStartWithWindows();
                    }
                    ws.Settings.SetupDone = true;
                    ws.SaveSettings();
                    if (folderChanged)
                    {
                        ws.WatchSaves();
                        RefreshAll();
                        RunCheck("folder changed");
                    }
                }
            }
            finally { settingsOpen = false; }
        }

        // Test helper: a picture of the settings box, then quit.
        void SnapshotSettingsBox()
        {
            var f = new SettingsForm(ws.Settings.ForeverPath ?? Workshop.FindForever(), ws.Settings.StartWithWindows, !ws.Settings.SetupDone);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(Left + (Width - f.Width) / 2, Top + Theme.S(80));
            f.TopMost = true;
            f.Show(this);
            var t = new System.Windows.Forms.Timer { Interval = 900 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                try
                {
                    var b = f.Bounds;
                    using (var bmp = new Bitmap(b.Width, b.Height))
                    {
                        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                        bmp.Save(Opts.SnapshotSettings, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch (Exception ex) { Util.Log("snapshot: " + ex.Message); }
                Quit();
            };
            t.Start();
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
            bench.StatusText = ws.Status;
            var m = ws.Manifest;
            if (m == null || m.addons == null)
            {
                if (cards.Count == 0 && list.Controls.Count == 0)
                    list.Controls.Add(new Label { Text = "Getting the list of addons...", AutoSize = true, ForeColor = Theme.InkMid, BackColor = Color.Transparent, Margin = new Padding(Theme.S(6), Theme.S(12), 0, 0) });
                FitWindow();
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
                    var c = new AddonCard(a.id) { Margin = new Padding(0, Theme.S(4), 0, Theme.S(4)) };
                    c.ToggleClicked += OnToggle;
                    cards[a.id] = c;
                    list.Controls.Add(c);
                }
                list.ResumeLayout();
                FitCards();
            }
            foreach (var a in m.addons) UpdateCard(cards[a.id], a);
            FitWindow();
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
            string note, shortText, longText;
            StatusKind kind;
            if (ws.Notes.TryGetValue(a.id, out note) && note != null)
            {
                longText = note;
                kind = note.StartsWith("Couldn't") ? StatusKind.Problem : StatusKind.Busy;
                int colon = note.IndexOf(':');
                shortText = colon > 0 ? note.Substring(0, colon) : note;
            }
            else if (wanted)
            {
                if (have == null) { shortText = "Waiting to install"; longText = "Not installed yet."; kind = StatusKind.Waiting; }
                else if (Util.Newer(a.version, have)) { shortText = "Update waiting"; longText = "Version " + have + ". Update to " + a.version + " waiting."; kind = StatusKind.Waiting; }
                else { shortText = have + ", up to date"; longText = "Version " + have + ", up to date."; kind = StatusKind.Ok; }
            }
            else
            {
                kind = StatusKind.Off;
                shortText = have == null ? "" : have + ", not updated";
                longText = have == null
                    ? "Not installed. Switch it on to add it."
                    : "Version " + have + " is installed but switched off, so it isn't kept up to date.";
            }
            c.Set(a.name ?? a.id, a.description, shortText, longText, kind, wanted);
            if (tips.GetToolTip(c) != longText) tips.SetToolTip(c, longText);
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
            RescueBounds();
            Activate();
            BringToFront();
            MaybeFirstRun();
        }

        // Back to the normal width, and onto a screen, if Windows squeezed or
        // moved the window while it was away.
        void RescueBounds()
        {
            var work = Screen.FromControl(this).WorkingArea;
            int wantW = Math.Min(Theme.S(600), work.Width - 40);
            bool squeezed = ClientSize.Width < wantW - 2 || Height < Theme.S(200);
            bool offScreen = !Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(Bounds));
            if (squeezed)
            {
                Util.Log("window was squeezed to " + Width + "x" + Height + "; put back");
                ClientSize = new Size(wantW, Math.Max(ClientSize.Height, Theme.S(300)));
                FitWindow();
            }
            if (squeezed || offScreen)
            {
                var area = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + Math.Max(0, (area.Height - Height) / 2));
            }
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
