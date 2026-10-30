import os, random
base = os.path.join(os.environ['TEMP'], 'gui_fec_test')
for d in ('send', 'recv'):
    os.makedirs(os.path.join(base, d), exist_ok=True)
p = os.path.join(base, 'send', 'fec_gui.bin')
rnd = random.Random(99)
with open(p, 'wb') as f:
    for _ in range(8 * 1024):
        f.write(bytes(rnd.getrandbits(8) for _ in range(1024)))
print('file:', p, os.path.getsize(p), 'bytes')

# Seed the shared config so GUI fields are identifiable and pre-filled
cfg = os.path.join(os.environ['APPDATA'], 'TrFileTransfer', 'config.ini')
seed = {
    'Language': '中文',
    'ClientIP': '127.0.0.1',
    'ClientPort': '47781',
    'ServerPortUDP': '47781',
    'ServerPortTCP': '47782',
    'ServerPortUDT': '47783',
    'UdpPasses': '1',
    'UdpFecParity': '4',
    'Concurrency': '5',
    'SpeedLimit': '512',
    'LastPath': os.path.join(base, 'send', 'fec_gui.bin'),
    'SaveDir': os.path.join(base, 'recv'),
}
with open(cfg, 'w', encoding='utf-8') as f:
    for k, v in seed.items():
        f.write(k + '=' + v + '\n')
print('config seeded:', cfg)
