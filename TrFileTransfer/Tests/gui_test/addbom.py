import sys, os
for f in sys.argv[1:]:
    data = open(f, 'rb').read()
    if not data.startswith(b'\xef\xbb\xbf'):
        open(f, 'wb').write(b'\xef\xbb\xbf' + data)
        print('BOM added:', os.path.basename(f))
    else:
        print('BOM ok:', os.path.basename(f))
