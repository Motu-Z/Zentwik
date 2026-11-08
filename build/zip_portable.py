import os
import re
import sys
import time
import zipfile

SRC = "dist/portable"

# 版本号唯一来源：Zentwik.Core.csproj 的 <Version>。
# 以前这里硬编码 "2.0.0-can3"，每次发版都要手工改两处（TOP / OUT），
# 漏改的后果是 zip 内的顶层目录名与文件名各带一个版本号，且都可能是旧的。
_CSPROJ = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                       "..", "Zentwik.Core", "Zentwik.Core.csproj")

with open(_CSPROJ, encoding="utf-8") as f:
    m = re.search(r"<Version>([^<]+)</Version>", f.read())

if not m:
    print("ERROR: Zentwik.Core.csproj 里找不到 <Version>")
    sys.exit(1)

VERSION = m.group(1).strip()
TOP = "Zentwik-" + VERSION
OUT = "dist/Zentwik-" + VERSION + "-portable-win-x64.zip"

t0 = time.time()
count = 0

with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
    for root, _dirs, files in os.walk(SRC):
        for name in files:
            full = os.path.join(root, name)
            rel = os.path.relpath(full, SRC).replace(os.sep, "/")
            zf.write(full, TOP + "/" + rel)
            count += 1

print("files=%d size=%.1fMB elapsed=%.0fs" % (
    count, os.path.getsize(OUT) / 1048576.0, time.time() - t0))
sys.exit(0)
