"""Prepares a release of Ziri's Workshop: builds the app, zips every addon
listed in addons.json and writes manifest.json. It does NOT commit or push;
look at `git status`, then commit and push yourself when you are happy.

Run from the repo folder:
    python tools/publish.py            (normal)
    python tools/publish.py --out DIR  (write into another folder, for testing)

Name and description of each addon come from its .toc (## Title, ## Notes)
unless addons.json gives "name" / "description".
"replaces": ["OldId", ...] in addons.json switches this addon on for everyone
who had one of the old ones; the app retires the old folders (Recycle Bin)
and carries their saved settings over. Drop the old ones from addons.json.
A changed addon needs a new ## Version in its .toc; an unchanged one keeps
its old zip, so friends only download what really changed.
An addon with a .pkgmeta file (the CurseForge packager's settings) leaves out
the files and folders its "ignore:" list names, the same as on CurseForge.
"""
import argparse
import hashlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKIP_NAMES = {"CLEANROOM.md", "Thumbs.db", "desktop.ini", ".DS_Store"}
SKIP_SUFFIXES = (".bak", ".zip", ".tmp", ".orig")
STAMP = (2020, 1, 1, 0, 0, 0)  # fixed dates, so the same files give the same zip


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def toc_value(toc, key):
    with open(toc, encoding="utf-8-sig") as f:
        for line in f:
            m = re.match(r"##\s*" + key + r"\s*:\s*(.*)", line.strip())
            if m:
                return m.group(1).strip()
    return None


def pkgmeta_ignore(src):
    """The paths a .pkgmeta "ignore:" list names (relative, forward slashes)."""
    path = os.path.join(src, ".pkgmeta")
    if not os.path.exists(path):
        return set()
    ignore, in_list = set(), False
    with open(path, encoding="utf-8") as f:
        for line in f:
            text = line.split("#", 1)[0].rstrip()
            if not text:
                continue
            if not text.startswith((" ", "\t", "-")):
                in_list = text.strip() == "ignore:"
                continue
            m = re.match(r"\s*-\s*(.+)", text)
            if in_list and m:
                ignore.add(m.group(1).strip().strip("'\"").rstrip("/"))
    return ignore


def addon_files(src):
    ignore = pkgmeta_ignore(src)
    files = []
    for base, dirs, names in os.walk(src):
        rel_base = os.path.relpath(base, src).replace("\\", "/")
        rel_base = "" if rel_base == "." else rel_base + "/"
        dirs[:] = sorted(d for d in dirs if not d.startswith(".") and rel_base + d not in ignore)
        for n in sorted(names):
            if n in SKIP_NAMES or n.endswith(SKIP_SUFFIXES) or n.startswith("."):
                continue
            if rel_base + n in ignore:
                continue
            files.append(os.path.join(base, n))
    return files


def make_zip(addon_id, src):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for path in addon_files(src):
            rel = os.path.relpath(path, src).replace("\\", "/")
            info = zipfile.ZipInfo(addon_id + "/" + rel, STAMP)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            with open(path, "rb") as f:
                z.writestr(info, f.read())
    return buf.getvalue()


def version_key(name):
    return [int(n) for n in re.findall(r"\d+", name)]


def prune(out, manifest):
    """Keeps the newest two versions of the app and of each listed addon
    (the one before stays for anyone whose copy of the list is a few minutes
    old) and removes the rest. Git history still has them."""
    keep = {"ZirisWorkshop.exe"}
    groups = {"app": {"ZirisWorkshop": []}, "addons": {a["id"]: [] for a in manifest["addons"]}}
    removed = []
    for folder, ids in groups.items():
        base = os.path.join(out, folder)
        for name in sorted(os.listdir(base)):
            m = re.match(r"(.+)-(\d[\d.]*)\.(exe|zip)$", name)
            if name in keep:
                continue
            if m and m.group(1) in ids:
                ids[m.group(1)].append(name)
            else:
                os.remove(os.path.join(base, name))
                removed.append(folder + "/" + name)
        for names in ids.values():
            names.sort(key=version_key)
            for name in names[:-2]:
                os.remove(os.path.join(base, name))
                removed.append(folder + "/" + name)
    if removed:
        print("removed old files: " + ", ".join(removed))


def check_text(label, text):
    if text and "—" in text:
        sys.exit(label + " contains an em dash; please reword it")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=ROOT)
    ap.add_argument("--no-build", action="store_true")
    args = ap.parse_args()
    out = os.path.abspath(args.out)

    if not args.no_build:
        r = subprocess.run(["cmd", "/c", os.path.join(ROOT, "build.cmd")], capture_output=True, text=True)
        if r.returncode != 0:
            print(r.stdout, r.stderr)
            sys.exit("build failed")

    src_text = open(os.path.join(ROOT, "src", "ZirisWorkshop.cs"), encoding="utf-8").read()
    app_version = re.search(r'AppVersion = "([^"]+)"', src_text).group(1)
    asm_version = re.search(r'AssemblyVersion\("([^"]+)"\)', src_text).group(1)
    if asm_version != app_version + ".0":
        sys.exit("AssemblyVersion is %s but AppVersion is %s; make them match in src/ZirisWorkshop.cs"
                 % (asm_version, app_version))
    exe = open(os.path.join(ROOT, "app", "ZirisWorkshop.exe"), "rb").read()

    os.makedirs(os.path.join(out, "app"), exist_ok=True)
    os.makedirs(os.path.join(out, "addons"), exist_ok=True)
    app_rel = "app/ZirisWorkshop-" + app_version + ".exe"
    app_path = os.path.join(out, app_rel)
    if os.path.exists(app_path) and open(app_path, "rb").read() != exe:
        print("note: app " + app_version + " already published with different bytes; keeping the published one.")
        print("      Bump AppVersion in src/ZirisWorkshop.cs to ship app changes.")
        exe = open(app_path, "rb").read()
    else:
        with open(app_path, "wb") as f:
            f.write(exe)
    # The file friends download must really be that version, or their copy
    # would update again and again.
    if ("ZirisWorkshop/" + app_version).encode("utf-16-le") not in exe:
        sys.exit("app/ZirisWorkshop.exe is not version " + app_version + ". Run without --no-build.")
    if os.path.abspath(out) != ROOT:
        shutil.copyfile(os.path.join(ROOT, "app", "ZirisWorkshop.exe"), os.path.join(out, "app", "ZirisWorkshop.exe"))

    manifest = {"app": {"version": app_version, "url": app_rel, "sha256": sha256(exe)}, "addons": []}
    with open(os.path.join(ROOT, "addons.json"), encoding="utf-8") as f:
        listed = json.load(f)
    for entry in listed:
        aid, src = entry["id"], entry["source"]
        toc = os.path.join(src, aid + ".toc")
        if not os.path.exists(toc):
            sys.exit("missing " + toc)
        version = toc_value(toc, "Version")
        name = entry.get("name") or toc_value(toc, "Title") or aid
        desc = entry.get("description") or toc_value(toc, "Notes") or ""
        check_text(aid + " name", name)
        check_text(aid + " description", desc)
        data = make_zip(aid, src)
        rel = "addons/" + aid + "-" + version + ".zip"
        path = os.path.join(out, rel)
        state = "new"
        if os.path.exists(path):
            old = open(path, "rb").read()
            if old == data:
                state = "unchanged"
            else:
                sys.exit(aid + " changed but its version is still " + version + ". Bump ## Version in " + toc)
        if state == "new":
            with open(path, "wb") as f:
                f.write(data)
        item = {"id": aid, "name": name, "description": desc, "version": version,
                "url": rel, "sha256": sha256(data)}
        if entry.get("replaces"):
            item["replaces"] = list(entry["replaces"])
        manifest["addons"].append(item)
        print("  %-14s %-8s %s" % (aid, version, state))

    with open(os.path.join(out, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    prune(out, manifest)
    print("app " + app_version + ", manifest written to " + out)
    print("Nothing is pushed. Check `git status`, then commit and push when ready.")


if __name__ == "__main__":
    main()
