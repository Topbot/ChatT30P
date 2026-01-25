import pathlib
import sys

paths = sys.argv[1:] or [
    'ChatT30P/Account/register.aspx.cs',
    'ChatT30P/Controllers/Api/YoutubeController.cs',
    'ChatT30P/Core/MemberEntity.cs',
    'ChatT30P/Controllers/Models/YoutubeItem.cs',
]

for p in paths:
    path = pathlib.Path(p)
    if not path.exists():
        print(f"MISSING\t{p}")
        continue
    b = path.read_bytes()
    print(f"{p}\tUTF8_BOM={b.startswith(b'\xef\xbb\xbf')}")
