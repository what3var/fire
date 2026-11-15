#!/usr/bin/env python3
"""The cases "native == VM" of src/fire.Testing/Program.cs as files (docs/tools/testing.md "The cases as files").

  cases.py strip <Program.cs> <output.cs> [<cases folder>]

reads a Program.cs that holds the cases as text (the way master has them: `var natCases = new (string Name, string Source)[] { ("title", \"\"\" ... \"\"\"), ... }`
and the `natCases = natCases.Concat(...)` / `.Append(...)` that follow), writes it without the cases whose source is a plain text (they are loaded from the
files instead) and - with a folder - brings the files up to date: a case that is new gets a number between its neighbours, a case whose source changed is
written again and loses its expected output (record it: FIRE_TESTS_UPDATE=1), a case that is gone loses its files. A case keeps its file as long as it keeps
its title. Cases whose source is made at run time stay in the code. Used by tests/tools/merge-master.sh when master brings new cases."""
import os, re, sys, unicodedata

HEAD_VAR = '    var natCases = new (string Name, string Source)[]'
HEAD_CONCAT = '    natCases = natCases.Concat(new (string Name, string Source)[]'
ENTRY = re.compile(r'^        \("((?:[^"\\]|\\["\\])*)", """$')
SINGLE = re.compile(r'^    natCases = natCases\.Append\(\("((?:[^"\\]|\\["\\])*)", """$')

def unescape(s): return s.replace('\\"', '"').replace('\\\\', '\\')

def body_of(lines, indent):
    """The text of a raw string literal: its lines without the indentation of the closing quotes."""
    out = []
    for l in lines:
        if l.strip() == '': out.append(l[len(indent):] if l.startswith(indent) else '')
        elif not l.startswith(indent): return None
        else: out.append(l[len(indent):])
    return '\n'.join(out)

def strip(text):
    """(the text without the literal cases, the cases as (title, source, notes) in their order)"""
    crlf = '\r\n' in text[:3000]
    lines = text.split('\r\n' if crlf else '\n')
    moved, out, i, n = [], [], 0, len(lines)
    def prev_code(idx):
        j = idx - 1
        while j >= 0 and (lines[j].strip() == '' or lines[j].strip().startswith('//')): j -= 1
        return lines[j] if j >= 0 else ''
    while i < n:
        l = lines[i]
        if l in (HEAD_VAR, HEAD_CONCAT) and lines[i + 1] == '    {':
            isvar = l == HEAD_VAR
            end_marker = '    };' if isvar else '    }).ToArray();'
            j = i + 2
            while lines[j] != end_marker: j += 1
            inner, kept, pending, k = lines[i + 2:j], [], [], 0
            while k < len(inner):
                m = ENTRY.match(inner[k])
                if m:
                    e = k + 1
                    while e < len(inner) and not re.match(r'^\s*"""\),?\s*$', inner[e]): e += 1
                    closing = inner[e] if e < len(inner) else None
                    src = body_of(inner[k + 1:e], re.match(r'^\s*', closing).group(0)) if closing is not None and re.match(r'^\s*"""\),$', closing) else None
                    if src is not None and '"""' not in src:
                        moved.append((unescape(m.group(1)), src, [c.strip()[2:].strip() for c in pending])); pending = []
                        k = e + 1; continue
                    kept += pending + inner[k:e + 1]; pending = []
                    k = e + 1; continue
                if inner[k].strip().startswith('//'): pending.append(inner[k]); k += 1; continue
                kept += pending; pending = []; kept.append(inner[k]); k += 1
            kept += pending
            if isvar:
                out.append('    // the cases "native == VM" are files: tests/cases/native (TestCases); here only those whose source is made at run time')
                out.append('    var natCases = TestCases.Load("native").Select(c => (c.Name, c.Source)).Concat(new (string Name, string Source)[]')
                out.append('    {'); out += kept; out.append('    }).ToArray();')
            elif any(x.strip() and not x.strip().startswith('//') for x in kept):
                out.append(l); out.append('    {'); out += kept; out.append(end_marker)
            else:
                out += kept
                if j + 1 < n and lines[j + 1].strip() == '' and out and out[-1].strip() == '': j += 1
            i = j + 1; continue
        m = SINGLE.match(l)
        if m and not prev_code(i).strip().startswith(('if', 'else', 'foreach', 'for ', 'while')):
            e = i + 1
            while e < n and not re.match(r'^\s*"""\)\)\.ToArray\(\);\s*$', lines[e]) and not lines[e].startswith('    natCases'): e += 1
            if e < n and re.match(r'^\s*"""\)\)\.ToArray\(\);\s*$', lines[e]):
                src = body_of(lines[i + 1:e], re.match(r'^\s*', lines[e]).group(0))
                if src is not None and '"""' not in src:
                    notes = []
                    while out and out[-1].strip().startswith('//'): notes.insert(0, out.pop().strip()[2:].strip())
                    moved.append((unescape(m.group(1)), src, notes))
                    i = e + 1
                    if i < n and lines[i].strip() == '' and out and out[-1].strip() == '': i += 1
                    continue
        out.append(l); i += 1
    return ('\r\n' if crlf else '\n').join(out), moved

def slug(name):
    b = ''
    for ch in unicodedata.normalize('NFD', name):
        if len(b) >= 48: break
        c = ch.lower()
        if 'a' <= c <= 'z' or '0' <= c <= '9': b += c
        elif c in ' -_/:.,()' and b and b[-1] != '-': b += '-'
    return b.strip('-')

def file_text(name, src, notes): return src + '\n' + ''.join('//# note: ' + x + '\n' for x in notes) + '//# name: ' + name + '\n'

def title_of(text):
    for line in reversed(text.split('\n')):
        if line.startswith('//# name: '): return line[len('//# name: '):]
        if line and not line.startswith('//# '): break
    return None

def sync(moved, folder):
    os.makedirs(folder, exist_ok=True)
    existing = {}   # title -> id
    for f in sorted(os.listdir(folder)):
        if f.endswith('.fire'):
            t = title_of(open(os.path.join(folder, f), encoding='utf-8', newline='').read())
            if t is not None: existing[t] = f[:-5]
    titles = [m[0] for m in moved]
    if len(set(titles)) != len(titles): sys.exit('two cases have the same title: ' + ', '.join(sorted({t for t in titles if titles.count(t) > 1})))
    ids, last = {}, 0
    used = {int(i.split('-')[0]) for i in existing.values()}
    for idx, (name, _, _) in enumerate(moved):
        if name in existing: ids[name] = existing[name]; last = max(last, int(existing[name].split('-')[0])); continue
        # a number between the case before and the next case that has a file
        nxt = next((int(existing[t].split('-')[0]) for t in titles[idx + 1:] if t in existing and int(existing[t].split('-')[0]) > last), None)
        number = last + 10 if nxt is None else last + (nxt - last) // 2
        if number == last or number in used: sys.exit(f'no free number for the new case "{name}" after {last:04d}: renumber the files around it')
        used.add(number); last = number
        ids[name] = '%04d-%s' % (number, slug(name))
    new = changed = gone = 0
    for name, src, notes in moved:
        path = os.path.join(folder, ids[name] + '.fire')
        text = file_text(name, src, notes)
        old = open(path, encoding='utf-8', newline='').read() if os.path.exists(path) else None
        if old == text: continue
        if old is None: new += 1; print('new:     ' + ids[name])
        else:
            # (its own lines about the case stay: `//# output: varies`)
            keep = [l for l in old.split('\n') if l == '//# output: varies']
            if keep: text = text[:text.rindex('//# name: ')] + keep[0] + '\n' + text[text.rindex('//# name: '):]
            if old == text: continue
            changed += 1; print('changed: ' + ids[name])
            for ext in ('.expected', '.unhandled'):
                if os.path.exists(path[:-5] + ext): os.remove(path[:-5] + ext)
        open(path, 'w', encoding='utf-8', newline='').write(text)
    for title, fid in existing.items():
        if title not in ids:
            gone += 1; print('gone:    ' + fid)
            for ext in ('.fire', '.expected', '.unhandled'):
                if os.path.exists(os.path.join(folder, fid + ext)): os.remove(os.path.join(folder, fid + ext))
    print(f'{len(moved)} cases as files: {new} new, {changed} changed, {gone} gone')
    return new + changed


# ---- the families of checks that only run in the VM: CheckPerf("title", script, expected, ...) and the like ----

FAMILIES = {   # helper -> (folder under tests/cases, the call that runs a case from its file)
    'CheckExt': ('vm-ext', 'CheckExt(c.Name, c.Source, c.Lines)'), 'CheckExtError': ('vm-ext-error', 'CheckExtError(c.Name, c.Source, c.Error!)'),
    'CheckArr': ('vm-arr', 'CheckArr(c.Name, c.Source, c.Lines)'), 'CheckArrError': ('vm-arr-error', 'CheckArrError(c.Name, c.Source, c.Error!)'),
    'CheckPerf': ('vm-perf', 'CheckPerf(c.Name, c.Source, c.Lines, c.Modes)'),
    'CheckClone': ('vm-clone', 'CheckClone(c.Name, c.Source, c.Lines)'), 'CheckCloneError': ('vm-clone-error', 'CheckCloneError(c.Name, c.Source, c.Error!)'),
    'CheckLife': ('vm-life', 'CheckLife(c.Name, c.Source, c.Lines, c.Modes)'), 'CheckShutdown': ('vm-shutdown', 'CheckShutdown(c.Name, c.Source, c.Lines)'),
    'CheckGl': ('vm-gl', 'CheckGl(c.Name, c.Source, c.Lines, c.Modes)'), 'CheckLq': ('vm-lq', 'CheckLq(c.Name, c.Source, c.Lines, c.Modes)'),
    'CheckRf': ('vm-rf', 'CheckRf(c.Name, c.Source, c.Lines, c.Modes, c.Packed)'),
    'CheckSc': ('vm-sc', 'CheckSc(c.Name, c.Source, c.Lines)'), 'CheckScChecked': ('vm-sc-checked', 'CheckScChecked(c.Name, c.Source, c.Lines)'),
}

def statement_end(text, start):
    """The index behind the `;` of the call that starts at `start` (a helper name followed by `(`): strings, raw strings, chars and comments are skipped."""
    i, depth, n = start, 0, len(text)
    while i < n:
        c = text[i]
        if c == '/' and text.startswith('//', i): i = text.index('\n', i); continue
        if c == '/' and text.startswith('/*', i): i = text.index('*/', i) + 2; continue
        if c == '"' or (c in '$@' and re.match(r'[$@]+"', text[i:])):
            j = i
            while text[j] in '$@': j += 1
            verbatim = '@' in text[i:j]
            q = 0
            while j + q < n and text[j + q] == '"': q += 1
            if q >= 3:   # a raw string: ends with as many quotes
                i = text.index('"' * q, j + q) + q
                while i < n and text[i] == '"': i += 1
                continue
            if q == 2: i = j + 2; continue   # ""
            j += 1
            while True:
                if text[j] == '\\' and not verbatim: j += 2; continue
                if text[j] == '"':
                    if verbatim and text[j + 1] == '"': j += 2; continue
                    break
                j += 1
            i = j + 1; continue
        if c == "'":
            j = i + 1
            while text[j] != "'": j += 2 if text[j] == '\\' else 1
            i = j + 1; continue
        if c == '(': depth += 1
        elif c == ')':
            depth -= 1
            if depth == 0:
                m = re.match(r'\s*;', text[i + 1:])
                return i + 1 + m.end() if m else None
        i += 1
    return None

def move_calls(text, collected):
    """(the text with the calls of the families replaced by a loop over their files, the cases per folder as (title, source, notes, entry))"""
    crlf = '\r\n' in text[:3000]
    if crlf: text = text.replace('\r\n', '\n')
    by = {}   # (folder, title) -> entry, only titles that one check has
    count = {}
    for e in collected: count[(e['family'], e['title'])] = count.get((e['family'], e['title']), 0) + 1
    for e in collected:
        if count[(e['family'], e['title'])] == 1: by[('vm-' + e['family'], e['title'])] = e
    call = re.compile(r'^    (' + '|'.join(FAMILIES) + r')\("((?:[^"\\]|\\["\\])*)", ', re.M)
    out, pos, cases, started, between = [], 0, {}, set(), []
    for m in call.finditer(text):
        if m.start() < pos: continue
        helper, title = m.group(1), unescape(m.group(2))
        folder, loop = FAMILIES[helper]
        e = by.get((folder, title))
        end = statement_end(text, m.start() + 4)
        if e is None or end is None or text[end:end + 1] != '\n': continue
        if e['expected'] is not None and any('\n' in x or '\r' in x for x in e['expected']): continue
        if '\n' in title or '\r' in e['script']: continue
        # comments right above the call belong to the case
        head = text[pos:m.start()]
        lines = head.split('\n')   # (the last element is '' : the call starts a line)
        notes = []
        while len(lines) > 1 and lines[-2].strip().startswith('//'): notes.insert(0, lines.pop(-2).strip()[2:].strip())
        head = '\n'.join(lines)
        out.append(head)
        if folder not in started:
            started.add(folder)
            out.append(f'    // the cases of this check are files: tests/cases/{folder} (TestCases)\n    foreach (var c in TestCases.Load("{folder}")) {loop};\n')
        cases.setdefault(folder, []).append((title, e['script'], notes, e))
        pos = end + 1
        # (one empty line where the calls were, not one per call)
        tail = ''.join(out[-3:])
        while text[pos:pos + 1] == '\n' and (tail.endswith('\n\n') or tail.strip() == ''): pos += 1
    out.append(text[pos:])
    result = ''.join(out)
    if crlf: result = result.replace('\n', '\r\n')
    return result, cases

def write_vm_cases(cases, root):
    total = 0
    for folder, items in sorted(cases.items()):
        d = os.path.join(root, folder); os.makedirs(d, exist_ok=True)
        for idx, (title, script, notes, e) in enumerate(items):
            fid = '%04d-%s' % ((idx + 1) * 10, slug(title))
            extra = ''
            if e.get('modes'): extra += '//# modes: ' + ' '.join(e['modes']) + '\n'
            if e.get('packed'): extra += '//# packed\n'
            text = script + '\n' + ''.join('//# note: ' + x + '\n' for x in notes) + extra + '//# name: ' + title + '\n'
            open(os.path.join(d, fid + '.fire'), 'w', encoding='utf-8', newline='').write(text)
            if e['error'] is not None: open(os.path.join(d, fid + '.error'), 'w', encoding='utf-8', newline='').write(e['error'] + '\n')
            else: open(os.path.join(d, fid + '.expected'), 'w', encoding='utf-8', newline='').write(''.join(x + '\n' for x in e['expected']))
            total += 1
        print(f'{folder}: {len(items)} cases')
    print(f'{total} cases as files')

def record(forge, folder):
    """What forge says about the cases of a folder, written next to them: the output (.expected) and everything on the error output - warnings, errors, an
    exception nothing catches - (.diagnostics). For the groups whose cases are about the messages of the compiler; look at the files before you check them in."""
    import subprocess
    n = 0
    for f in sorted(os.listdir(folder)):
        if not f.endswith('.fire'): continue
        tail = open(os.path.join(folder, f), encoding='utf-8', newline='').read().split('\n')[-12:]
        args = [forge, 'run', f]
        for l in tail:
            if l.startswith('//# types: '): args += ['--types', l[len('//# types: '):]]
            if l.startswith('//# modes: '): args += ['-m', l[len('//# modes: '):].split(' ')[0].upper()]
        try: r = subprocess.run(args, capture_output=True, cwd=folder, timeout=120)
        except subprocess.TimeoutExpired: print('timeout: ' + f); continue
        # (what forge says about its own work - a library it builds on first use - is not about the program)
        stderr = b''.join(l for l in r.stderr.splitlines(True) if not l.startswith((b'Building the native part of ', b'Installing the standard package ')))
        for ext, data in (('.expected', r.stdout), ('.diagnostics', stderr)):
            path = os.path.join(folder, f[:-5] + ext)
            if not os.path.exists(path) or open(path, 'rb').read() != data: open(path, 'wb').write(data); n += 1
    print(f'{folder}: {n} files written')

def write_sources(collected, root, forge):
    """The sources that the checks of diagnostics, types and syntax compile (FIRE_TESTS_COLLECT: family `source:<group>`) as cases, each once."""
    groups = {}
    for e in collected:
        if not e['family'].startswith('source:'): continue
        g = groups.setdefault(e['family'][7:], {})
        g.setdefault((e['script'], e.get('types')), None)
    for group, items in sorted(groups.items()):
        d = os.path.join(root, group); os.makedirs(d, exist_ok=True)
        for idx, (script, types) in enumerate(items):
            if '\r' in script: continue
            first = next((l.strip() for l in script.split('\n') if l.strip() and not l.strip().startswith('//')), 'empty')
            title = f'{group} {idx + 1}: {first[:70]}'
            text = script + '\n' + ('//# types: ' + types + '\n' if types else '') + '//# modes: Release\n//# name: ' + title + '\n'
            open(os.path.join(d, '%04d-%s.fire' % ((idx + 1) * 10, slug(first)[:40].strip('-') or 'case')), 'w', encoding='utf-8', newline='').write(text)
        print(f'{group}: {len(items)} cases')
        record(forge, d)

if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == 'record':
        # cases.py record <forge> <folder of a group>: writes .expected and .diagnostics as this forge gives them
        record(os.path.abspath(sys.argv[2]), sys.argv[3]); sys.exit(0)
    if len(sys.argv) == 5 and sys.argv[1] == 'sources':
        # cases.py sources <collected.jsonl> <tests/cases> <forge>: the sources of the diagnostic checks as cases (done once)
        import json
        write_sources([json.loads(l) for l in open(sys.argv[2], encoding='utf-8') if l.strip()], sys.argv[3], os.path.abspath(sys.argv[4])); sys.exit(0)
    if len(sys.argv) == 6 and sys.argv[1] == 'calls':
        # cases.py calls <Program.cs> <collected.jsonl of FIRE_TESTS_COLLECT> <output.cs> <tests/cases>: the checks of the VM families as files (done once)
        import json
        collected = [json.loads(l) for l in open(sys.argv[3], encoding='utf-8') if l.strip()]
        result, cases = move_calls(open(sys.argv[2], 'rb').read().decode('utf-8'), collected)
        open(sys.argv[4], 'wb').write(result.encode('utf-8'))
        write_vm_cases(cases, sys.argv[5])
        sys.exit(0)

    if len(sys.argv) < 4 or sys.argv[1] != 'strip': sys.exit(__doc__)
    text = open(sys.argv[2], 'rb').read().decode('utf-8')
    stripped, moved = strip(text)
    open(sys.argv[3], 'wb').write(stripped.encode('utf-8'))
    if len(sys.argv) > 4: sync(moved, sys.argv[4])
    else: print(f'{len(moved)} cases taken out')
