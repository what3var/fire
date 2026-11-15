#!/usr/bin/env python3
"""The size and the speed of native programs, to compare before and after a change (docs/ROADMAP_C_PORT.md "Pruefungen fuer den nativen Build": a native
build must not get bigger or slower).

  tests/native-baseline.py measure <forge> <result.tsv> [--runs N] [<more.fire>...]
  tests/native-baseline.py compare <before.tsv> <after.tsv> [--time <percent>]

measure builds every program of tests/bench (and an empty one, `print(0)`) with `forge build --engine native -m RELEASE` for this machine and writes a line
per program: the bytes of the executable, its code, data and bss (`size`), the build time, the shortest of N runs in milliseconds (the whole process;
default 30 runs) and what it printed. compare lists what changed: a size that grew, a time that grew by more than the tolerance (default 10 % - the
programs run for milliseconds, measure on a quiet machine and with more runs for less), an output that differs. Exit code 1 when something got worse."""
import os, subprocess, sys, tempfile, time

FIELDS = ['program', 'bytes', 'text', 'data', 'bss', 'build_ms', 'run_ms', 'output']

def measure(forge, result, runs, more):
    root = os.path.dirname(os.path.abspath(__file__))
    forge = os.path.abspath(forge)
    programs = sorted(os.path.join(root, 'bench', f) for f in os.listdir(os.path.join(root, 'bench')) if f.endswith('.fire')) + [os.path.abspath(m) for m in more]
    rows = []
    with tempfile.TemporaryDirectory() as tmp:
        empty = os.path.join(tmp, 'empty.fire'); open(empty, 'w').write('print(0)\n')
        for source in [empty] + programs:
            name = os.path.splitext(os.path.basename(source))[0]
            exe = os.path.join(tmp, name + '.bin')
            t = time.perf_counter()
            b = subprocess.run([forge, 'build', source, '--engine', 'native', '-m', 'RELEASE', '-o', exe], capture_output=True, text=True, cwd=tmp)
            build_ms = (time.perf_counter() - t) * 1000
            if b.returncode != 0 or not os.path.exists(exe):
                print(f'{name}: does not build\n{b.stdout}{b.stderr}'); rows.append([name, 'FAILED'] + [''] * 6); continue
            sizes = subprocess.run(['size', exe], capture_output=True, text=True).stdout.split('\n')[1].split()
            best, output = None, ''
            for _ in range(runs + 1):   # (the first run only warms the file cache)
                t = time.perf_counter()
                r = subprocess.run([exe], capture_output=True, text=True)
                ms = (time.perf_counter() - t) * 1000
                output = r.stdout.strip().replace('\t', ' ').replace('\n', ' | ')
                if _ > 0 and (best is None or ms < best): best = ms
            rows.append([name, str(os.path.getsize(exe)), sizes[0], sizes[1], sizes[2], f'{build_ms:.0f}', f'{best:.2f}', output])
            print(f'{name:10} {os.path.getsize(exe):8} bytes  {best:8.2f} ms  {output[:50]}')
    with open(result, 'w', newline='') as f:
        f.write('\t'.join(FIELDS) + '\n')
        for r in rows: f.write('\t'.join(r) + '\n')

def load(path):
    lines = open(path).read().split('\n')
    head = lines[0].split('\t')
    return {l.split('\t')[0]: dict(zip(head, l.split('\t'))) for l in lines[1:] if l}

def compare(before, after, tolerance):
    a, b = load(before), load(after)
    worse = 0
    print(f'{"program":10} {"bytes":>9} {"":>8} {"code":>8} {"":>7} {"run ms":>9} {"":>8}')
    for name in a:
        if name not in b: print(f'{name:10} missing afterwards'); worse += 1; continue
        x, y = a[name], b[name]
        if x['bytes'] == 'FAILED' or y['bytes'] == 'FAILED':
            if x['bytes'] != y['bytes']: print(f'{name:10} builds: {x["bytes"] != "FAILED"} -> {y["bytes"] != "FAILED"}'); worse += y['bytes'] == 'FAILED'
            continue
        db, dt = int(y['bytes']) - int(x['bytes']), int(y['text']) - int(x['text'])
        ms0, ms1 = float(x['run_ms']), float(y['run_ms'])
        pct = (ms1 - ms0) / ms0 * 100 if ms0 > 0 else 0
        marks = []
        if db > 0 or dt > 0 or int(y['data']) + int(y['bss']) > int(x['data']) + int(x['bss']): marks.append('BIGGER')
        if pct > tolerance: marks.append('SLOWER')
        if x['output'] != y['output']: marks.append('OUTPUT DIFFERS')
        worse += len(marks) > 0
        print(f'{name:10} {y["bytes"]:>9} {db:>+8} {y["text"]:>8} {dt:>+7} {ms1:>9.2f} {pct:>+7.1f}%  {" ".join(marks)}')
    for name in b:
        if name not in a: print(f'{name:10} new')
    print('nothing got worse' if worse == 0 else f'{worse} program(s) got worse')
    return 1 if worse else 0

if __name__ == '__main__':
    args = sys.argv[1:]
    def option(name, default):
        if name in args:
            i = args.index(name); value = args[i + 1]; del args[i:i + 2]; return value
        return default
    if len(args) >= 3 and args[0] == 'measure':
        runs = int(option('--runs', '30'))
        measure(args[1], args[2], runs, args[3:])
    elif len(args) >= 3 and args[0] == 'compare':
        tolerance = float(option('--time', '10'))
        sys.exit(compare(args[1], args[2], tolerance))
    else: sys.exit(__doc__)
