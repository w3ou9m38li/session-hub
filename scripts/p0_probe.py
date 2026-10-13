"""P0: read-only discovery + capability evidence for the Claude launcher.

Writes nothing outside its own output directory. Never touches Claude's
profile, auth, MSIX package or session state.
"""
import json, os, glob, subprocess, datetime, collections, sys

OUT = r"D:\Temp\claude\claude-launcher\evidence"
os.makedirs(OUT, exist_ok=True)

LOCALAPPDATA = os.environ["LOCALAPPDATA"]
USERPROFILE = os.environ["USERPROFILE"]
PKG = "Claude_pzs8sxrjxfjjc"
PROFILE = os.path.join(LOCALAPPDATA, "Packages", PKG, "LocalCache", "Roaming", "Claude")
CONFIG_DIR = os.environ.get("CLAUDE_CONFIG_DIR") or os.path.join(USERPROFILE, ".claude")


def ps(cmd):
    try:
        r = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", cmd],
                           capture_output=True, text=True, timeout=120)
        return r.stdout.strip()
    except Exception as exc:
        return "ERROR: %s" % exc


# ---------- 1. installation identity ----------------------------------
pkg_json = ps("$p=Get-AppxPackage -Name Claude; "
              "[pscustomobject]@{name=$p.Name;version=$p.Version;"
              "familyName=$p.PackageFamilyName;installLocation=$p.InstallLocation;"
              "status=$p.Status} | ConvertTo-Json -Compress")
aumid = ps("(Get-StartApps | Where-Object AppID -like '*Claude*' | "
           "Select-Object -First 1 -ExpandProperty AppID)")
sdks = [s for s in ps("dotnet --list-sdks").splitlines() if s.strip()]

try:
    pkg = json.loads(pkg_json)
except Exception:
    pkg = {"raw": pkg_json}

installation = {
    "probed_at": datetime.datetime.now().isoformat(timespec="seconds"),
    "package": pkg,
    "aumid": aumid or None,
    "aumid_activation_verified": False,          # G01 未做激活验证
    "launch_strategy": {
        "primary": "shell:AppsFolder\\%s!Claude" % PKG,
        "rationale": "AUMID 与版本无关；InstallLocation 含版本号，禁止硬编码",
        "forbidden": [os.path.join(r"C:\Program Files\WindowsApps",
                                   "Claude_%s_x64__pzs8sxrjxfjjc" % pkg.get("version", "?"))],
    },
    "profile_root": PROFILE,
    "profile_exists": os.path.isdir(PROFILE),
    "claude_config_dir": CONFIG_DIR,
    "claude_config_dir_from_env": bool(os.environ.get("CLAUDE_CONFIG_DIR")),
    "dotnet_sdks": sdks,
    "dotnet_sdk_available": bool(sdks),
    "python": sys.version.split()[0],
}

# ---------- 2. source coverage ----------------------------------------
store = os.path.join(PROFILE, "claude-code-sessions")
desktop = []
for p in glob.glob(os.path.join(store, "*", "*", "local_*.json")):
    try:
        d = json.load(open(p, encoding="utf-8"))
    except Exception as exc:
        desktop.append({"file": os.path.basename(p), "error": str(exc)})
        continue
    desktop.append({
        "sessionId": d.get("sessionId"),
        "cliSessionId": d.get("cliSessionId"),
        "cwd": d.get("cwd"),
        "title": d.get("title"),
        "isArchived": bool(d.get("isArchived")),
        "lastActivityAt": d.get("lastActivityAt"),
    })

proj_root = os.path.join(CONFIG_DIR, "projects")
top_level, nested, total_bytes = [], 0, 0
for d in sorted(glob.glob(os.path.join(proj_root, "*"))):
    if not os.path.isdir(d):
        continue
    for jf in glob.glob(os.path.join(d, "*.jsonl")):
        top_level.append({"stem": os.path.basename(jf)[:-6],
                          "project": os.path.basename(d),
                          "bytes": os.path.getsize(jf)})
        total_bytes += os.path.getsize(jf)
    for jf in glob.glob(os.path.join(d, "*", "**", "*.jsonl"), recursive=True):
        nested += 1

stems = {t["stem"] for t in top_level}
matched = [r for r in desktop if r.get("cliSessionId") in stems]
unresolved = [r for r in desktop if r.get("cliSessionId") and r["cliSessionId"] not in stems]

agent_dir = os.path.join(PROFILE, "local-agent-mode-sessions")
agent_files = sum(len(f) for _, _, f in os.walk(agent_dir)) if os.path.isdir(agent_dir) else 0

coverage = {
    "probed_at": installation["probed_at"],
    "desktop_code": {
        "records": len(desktop),
        "archived": sum(1 for r in desktop if r.get("isArchived")),
        "with_cli_session_id": sum(1 for r in desktop if r.get("cliSessionId")),
        "store_root": store,
        "sourceCoverage": "Resolved" if os.path.isdir(store) else "Unreachable",
    },
    "cli_code": {
        "top_level_sessions": len(top_level),
        "nested_jsonl": nested,
        "projects": len({t["project"] for t in top_level}),
        "total_bytes": total_bytes,
        "root": proj_root,
        "note": "nested JSONL 含子代理，不计入主会话数",
        "sourceCoverage": "Resolved" if os.path.isdir(proj_root) else "Unreachable",
    },
    "link_analysis": {
        "desktop_cli_matched": len(matched),
        "desktop_unresolved": len(unresolved),
        "unresolved_ids": [r["cliSessionId"] for r in unresolved],
        "rule": "不做 desktop+cli 相加；未匹配标为 UnknownSource，不推断丢失",
    },
    "chat": {"sourceCoverage": "UnknownSource",
             "note": "未发现本地云端 Chat 会话来源；G04 未验证"},
    "cowork": {"candidate_dir": agent_dir, "files": agent_files,
               "sourceCoverage": "UnknownSource",
               "note": "文件数不等于会话数；G05 未验证，不读写该目录"},
}

# ---------- 3. capability registry ------------------------------------
U, V, N = "Unknown", "Verified", "Unsupported"
caps = {
    "app_version": pkg.get("version"),
    "capability_note": "没有实测证据的一律 Unknown，不得用空实现返回成功",
    "backends": {
        "DesktopCode": {
            "Discover": V, "EnumerateMetadata": V, "OpenApp": U,
            "OpenConversation": U, "ArchiveNative": U, "RestoreNative": U,
            "ReadBackNativeState": V, "DetectActive": U, "EnumerateAffectedSet": U,
            "gate": "G02/G03 未做；禁止写 isArchived",
        },
        "CliCode": {
            "Discover": V, "EnumerateMetadata": V, "OpenApp": U,
            "OpenConversation": U, "ArchiveNative": N, "RestoreNative": N,
            "ReadBackNativeState": V, "DetectActive": U, "EnumerateAffectedSet": U,
            "gate": "resume 走官方 --resume；CLI 无原生归档概念",
        },
        "Chat": {k: U for k in ("Discover", "EnumerateMetadata", "OpenApp",
                                "OpenConversation", "ArchiveNative", "RestoreNative",
                                "ReadBackNativeState", "DetectActive", "EnumerateAffectedSet")},
        "Cowork": {k: U for k in ("Discover", "EnumerateMetadata", "OpenApp",
                                  "OpenConversation", "ArchiveNative", "RestoreNative",
                                  "ReadBackNativeState", "DetectActive", "EnumerateAffectedSet")},
    },
}

for name, obj in (("installation.json", installation),
                  ("source-coverage.json", coverage),
                  ("capabilities.json", caps)):
    with open(os.path.join(OUT, name), "w", encoding="utf-8") as fh:
        json.dump(obj, fh, ensure_ascii=False, indent=1)

print("AUMID              :", installation["aumid"])
print("包版本             :", pkg.get("version"))
print("profile 存在       :", installation["profile_exists"])
print("CLAUDE_CONFIG_DIR  :", CONFIG_DIR, "(env)" if installation["claude_config_dir_from_env"] else "(default)")
print(".NET SDK           :", sdks or "无")
print()
print("Desktop Code 记录  : %d 条 (归档 %d)" % (len(desktop), coverage["desktop_code"]["archived"]))
print("CLI 顶层主会话     : %d 个 / %.2f GB / %d 个项目"
      % (len(top_level), total_bytes / 1024**3, coverage["cli_code"]["projects"]))
print("嵌套 JSONL(子代理) : %d 个" % nested)
print("cliSessionId 匹配  : %d 匹配 / %d 未解析" % (len(matched), len(unresolved)))
print()
print("证据写入:", OUT)
