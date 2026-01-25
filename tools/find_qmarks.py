import os

ROOT = os.path.join(os.getcwd(), "ChatT30P")
EXTS = {".cs", ".aspx", ".ascx", ".cshtml", ".config", ".resx", ".txt", ".js", ".html"}
TOKEN = "????"


def read_text(path: str) -> str | None:
    data = None
    try:
        with open(path, "rb") as f:
            data = f.read()
    except OSError:
        return None

    for enc in ("utf-8-sig", "utf-8", "cp1251", "latin1"):
        try:
            return data.decode(enc)
        except UnicodeDecodeError:
            continue
    return None


def main() -> int:
    hits: list[tuple[int, str]] = []
    for dirpath, _, filenames in os.walk(ROOT):
        for fn in filenames:
            ext = os.path.splitext(fn)[1].lower()
            if ext not in EXTS:
                continue
            path = os.path.join(dirpath, fn)
            text = read_text(path)
            if not text:
                continue
            c = text.count(TOKEN)
            if c:
                hits.append((c, path))

    hits.sort(key=lambda x: (-x[0], x[1]))
    for c, path in hits:
        print(f"{c}\t{path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
