import os
import sys
import time
import zipfile

SRC = "dist/portable"
TOP = "Zentwik-2.0.0-can3"
OUT = "dist/Zentwik-2.0.0-can3-portable-win-x64.zip"

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
