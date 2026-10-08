#!/usr/bin/env python3
# Teleporter server helper. Uploaded by the app to ~/.teleporter/bin/helper.py (versioned) and
# run over ssh. Standard library only, Python 3.8+. Every subcommand prints machine-readable
# output on stdout; problems go to stderr with a non-zero exit.
import hashlib
import json
import os
import re
import shutil
import stat
import sys
import time

VERSION = "4"


def walk(root, skip):
    """Yield (kind, relpath, fullpath) for regular files, empty dirs, links, unreadable entries.
    Symlinks are reported, never followed."""
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        rel_dir = os.path.relpath(dirpath, root)
        rel_dir = "" if rel_dir == "." else rel_dir.replace(os.sep, "/")
        kept = []
        for d in sorted(dirnames):
            full = os.path.join(dirpath, d)
            rel = (rel_dir + "/" + d) if rel_dir else d
            if os.path.islink(full):
                yield ("l", rel, full)
            elif d in skip:
                continue
            else:
                kept.append(d)
        dirnames[:] = kept
        for f in sorted(filenames):
            full = os.path.join(dirpath, f)
            rel = (rel_dir + "/" + f) if rel_dir else f
            try:
                st = os.lstat(full)
            except OSError:
                yield ("u", rel, full)
                continue
            if stat.S_ISLNK(st.st_mode):
                yield ("l", rel, full)
            elif stat.S_ISREG(st.st_mode):
                yield ("f", rel, full)
            else:
                yield ("u", rel, full)
        if not dirnames and not filenames and rel_dir:
            yield ("d", rel_dir, dirpath)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def cmd_manifest(args):
    root, skip = args[0], set(args[1:])
    out = sys.stdout
    for kind, rel, full in walk(root, skip):
        rec = {"k": kind, "p": rel}
        if kind == "f":
            try:
                st = os.stat(full)
                rec["s"] = st.st_size
                rec["h"] = sha256(full)
                rec["m"] = "%o" % stat.S_IMODE(st.st_mode)
            except OSError:
                rec = {"k": "u", "p": rel}
        out.write(json.dumps(rec) + "\n")


def cmd_filelist(args):
    """NUL-separated list for `tar --null -T -`: regular files and empty dirs only."""
    root, skip = args[0], set(args[1:])
    out = sys.stdout.buffer
    for kind, rel, _ in walk(root, skip):
        if kind in ("f", "d"):
            out.write(rel.encode("utf-8", "surrogateescape") + b"\0")


def cmd_chmod(args):
    """Apply {relpath: octal mode} from stdin under root."""
    root = args[0]
    modes = json.load(sys.stdin)
    n = 0
    for rel, mode in modes.items():
        p = os.path.join(root, rel)
        if os.path.isfile(p) and not os.path.islink(p):
            os.chmod(p, int(mode, 8))
            n += 1
    print(json.dumps({"applied": n}))


def canon(path):
    """Forward slashes, no trailing slash, MSYS /c/Users/... turned into c:/Users/...; case kept."""
    p = path.replace("\\", "/").rstrip("/")
    m = re.match(r"^/([a-zA-Z])/(.*)$", p)
    if m:
        p = m.group(1) + ":/" + m.group(2)
    return p


def is_windows_path(p):
    return bool(re.match(r"^[a-zA-Z]:", p))


def cmd_git_exec_bits(args):
    """chmod +x every file git records as 100755 (Windows had no exec bit to carry)."""
    import subprocess
    root = args[0]
    out = subprocess.run(["git", "-C", root, "ls-files", "-s", "-z"], capture_output=True, check=True).stdout
    n = 0
    for rec in out.split(b"\0"):
        if not rec.startswith(b"100755 "):
            continue
        path = rec.split(b"\t", 1)[1].decode("utf-8", "surrogateescape")
        full = os.path.join(root, path)
        if os.path.isfile(full) and not os.path.islink(full):
            os.chmod(full, os.stat(full).st_mode | 0o111)
            n += 1
    print(json.dumps({"exec": n}))


def norm(path):
    """Comparison key: canon(), lower-cased for Windows paths (case-insensitive there)."""
    p = canon(path)
    return p.lower() if is_windows_path(p) else p


def first_cwd(jsonl):
    try:
        with open(jsonl, "r", encoding="utf-8", errors="replace") as fh:
            for i, line in enumerate(fh):
                if i > 200:
                    break
                if '"cwd"' not in line:
                    continue
                try:
                    v = json.loads(line).get("cwd")
                except ValueError:
                    continue
                if isinstance(v, str):
                    return v
    except OSError:
        pass
    return None


def claude_dirs(project):
    """Project folders under ~/.claude/projects whose sessions ran in `project`."""
    base = os.path.expanduser("~/.claude/projects")
    target = norm(project)
    found = []
    if not os.path.isdir(base):
        return found
    for name in sorted(os.listdir(base)):
        d = os.path.join(base, name)
        if not os.path.isdir(d):
            continue
        jsonls = [f for f in os.listdir(d) if f.endswith(".jsonl")]
        for f in jsonls[:5]:
            cwd = first_cwd(os.path.join(d, f))
            if cwd is not None:
                c = norm(cwd)
                if c == target:
                    found.append(d)
                break
    return found


def map_cwd(value, old_root, new_root, new_is_windows):
    """Map a cwd inside old_root to the same place under new_root, or None if outside."""
    v, o = canon(value), canon(old_root)
    kv, ko = norm(value), norm(old_root)
    if kv != ko and not kv.startswith(ko + "/"):
        return None
    tail = v[len(o):].lstrip("/")
    if new_is_windows:
        return new_root.rstrip("\\/") + ("\\" + tail.replace("/", "\\") if tail else "")
    return new_root.rstrip("/") + ("/" + tail if tail else "")


def rewrite_jsonl(src, dst, old_root, new_root, new_is_windows):
    with open(src, "r", encoding="utf-8", errors="surrogateescape", newline="") as fi, \
         open(dst, "w", encoding="utf-8", errors="surrogateescape", newline="") as fo:
        for line in fi:
            stripped = line.rstrip("\r\n")
            ending = line[len(stripped):]
            try:
                obj = json.loads(stripped)
            except ValueError:
                fo.write(line)
                continue
            if isinstance(obj, dict) and isinstance(obj.get("cwd"), str):
                mapped = map_cwd(obj["cwd"], old_root, new_root, new_is_windows)
                if mapped is not None:
                    obj["cwd"] = mapped
                    fo.write(json.dumps(obj, ensure_ascii=False, separators=(",", ":")) + ending)
                    continue
            fo.write(line)


def cmd_sessions_list(args):
    project = args[0]
    dirs = claude_dirs(project)
    sessions = sum(len([f for f in os.listdir(d) if f.endswith(".jsonl")]) for d in dirs)
    memory = any(os.path.isdir(os.path.join(d, "memory")) for d in dirs)
    print(json.dumps({"dirs": [os.path.basename(d) for d in dirs], "sessions": sessions, "memory": memory}))


def cmd_sessions_stage(args):
    """Copy the project's Claude sessions + memory into staging/.claude/..., cwd rewritten
    to new_root, under target_dir_name (the encoded folder name on the destination)."""
    project, new_root, target_name, staging = args[0], args[1], args[2], args[3]
    new_is_windows = bool(re.match(r"^[a-zA-Z]:", new_root))
    home = os.path.expanduser("~")
    dest_proj = os.path.join(staging, ".claude", "projects", target_name)
    os.makedirs(dest_proj, exist_ok=True)
    uuids = []
    for d in claude_dirs(project):
        for name in os.listdir(d):
            src = os.path.join(d, name)
            dst = os.path.join(dest_proj, name)
            if name.endswith(".jsonl"):
                uuids.append(name[:-6])
                rewrite_jsonl(src, dst, project, new_root, new_is_windows)
            elif os.path.isdir(src) and not os.path.exists(dst):
                shutil.copytree(src, dst, symlinks=True)
            elif os.path.isfile(src) and not os.path.exists(dst):
                shutil.copy2(src, dst)
    for u in uuids:
        fh = os.path.join(home, ".claude", "file-history", u)
        if os.path.isdir(fh):
            shutil.copytree(fh, os.path.join(staging, ".claude", "file-history", u), symlinks=True)
    print(json.dumps({"sessions": len(uuids)}))


def cmd_sessions_park(args):
    """Move the project's Claude folders and their file-history into park_dir."""
    project, park = args[0], args[1]
    home = os.path.expanduser("~")
    os.makedirs(park, exist_ok=True)
    moved = 0
    for d in claude_dirs(project):
        uuids = [f[:-6] for f in os.listdir(d) if f.endswith(".jsonl")]
        shutil.move(d, os.path.join(park, "projects", os.path.basename(d)))
        moved += 1
        for u in uuids:
            fh = os.path.join(home, ".claude", "file-history", u)
            if os.path.isdir(fh):
                shutil.move(fh, os.path.join(park, "file-history", u))
    print(json.dumps({"moved": moved}))


def cmd_merge(args):
    """Move everything from staging into dest without overwriting: a clash is kept as
    name.teleporter-<time> beside the existing file."""
    staging, dest = args[0], args[1]
    stamp = time.strftime("%Y%m%d-%H%M%S")
    renamed = 0
    for dirpath, dirnames, filenames in os.walk(staging):
        rel = os.path.relpath(dirpath, staging)
        target_dir = dest if rel == "." else os.path.join(dest, rel)
        os.makedirs(target_dir, exist_ok=True)
        for f in filenames:
            src = os.path.join(dirpath, f)
            dst = os.path.join(target_dir, f)
            if os.path.exists(dst):
                dst = dst + ".teleporter-" + stamp
                renamed += 1
            os.replace(src, dst)
    shutil.rmtree(staging, ignore_errors=True)
    print(json.dumps({"renamed": renamed}))


def cmd_version(_):
    print(VERSION)


COMMANDS = {
    "manifest": cmd_manifest,
    "filelist": cmd_filelist,
    "chmod": cmd_chmod,
    "git-exec-bits": cmd_git_exec_bits,
    "sessions-list": cmd_sessions_list,
    "sessions-stage": cmd_sessions_stage,
    "sessions-park": cmd_sessions_park,
    "merge": cmd_merge,
    "version": cmd_version,
}

if __name__ == "__main__":
    if len(sys.argv) < 2 or sys.argv[1] not in COMMANDS:
        sys.stderr.write("usage: helper.py " + "|".join(COMMANDS) + " ...\n")
        sys.exit(2)
    COMMANDS[sys.argv[1]](sys.argv[2:])
