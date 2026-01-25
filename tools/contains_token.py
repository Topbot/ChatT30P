import sys
import pathlib

path = pathlib.Path(sys.argv[1])
token = sys.argv[2] if len(sys.argv) > 2 else '????'

t = path.read_text(encoding='utf-8-sig', errors='replace')
print('contains', token in t)
if token in t:
    i = t.find(token)
    print('index', i)
    print('context', t[max(0,i-40):i+80].replace('\n','\\n'))
