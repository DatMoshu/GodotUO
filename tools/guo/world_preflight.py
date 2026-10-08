"""Read-only world authoring preflight. No engine, client or project mutations."""
import argparse
import json
from collections import Counter
from pathlib import Path

def check_fingerprint(expected, actual):
    if not expected or expected != actual:
        raise ValueError(f'World base fingerprint mismatch: project={expected!r}, install={actual!r}')

def load_static_overrides(projects):
    """Ordered overlays merge supplied fields; omitted fields retain earlier values."""
    rows = {}
    for project in projects:
        path = Path(project) / 'assets/tiledata.json'
        if not path.exists():
            continue
        for key, row in json.loads(path.read_text(encoding='utf-8-sig')).items():
            item = int(key, 0)
            if not 0 <= item < 0xffff:
                raise ValueError(f'Invalid static ID {key}')
            row = dict(row)
            if 'flags' in row and isinstance(row['flags'], str):
                row['flags'] = int(row['flags'], 0)
            rows[item] = {**rows.get(item, {}), **row}
    return rows

def inspect(project, client_data):
    from .uomap import install_fingerprint
    project = Path(project)
    meta = json.loads((project / 'project.json').read_text(encoding='utf-8-sig'))
    fingerprint = install_fingerprint(Path(client_data))
    check_fingerprint(meta.get('base', {}).get('fingerprint'), fingerprint)
    counts = Counter()
    blocks = 0
    for p in sorted((project / 'blocks').glob('*/*.json')):
        block = json.loads(p.read_text(encoding='utf-8-sig'))
        if len(block['land']) != 8 or any(len(r.split()) != 8 for r in block['land']):
            raise ValueError(f'Expected 8x8 land: {p}')
        for row in block['land']:
            for cell in row.split():
                key, z = cell.split(':')
                if not -128 <= int(z) <= 127:
                    raise ValueError(f'Land z out of range: {p}')
                counts['land', int(key, 16)] += 1
        for s in block['statics']:
            if not (0 <= s['x'] < 8 and 0 <= s['y'] < 8 and -128 <= s['z'] <= 127):
                raise ValueError(f'Static coordinate out of range: {p}')
            counts['statics', int(s['id'], 16)] += 1
        blocks += 1
    return {'fingerprint': fingerprint, 'blocks': blocks,
            'static_tiledata_overrides': len(load_static_overrides([project])),
            'placements': [{'namespace': ns, 'id': f'0x{i:04X}', 'count': n}
                           for (ns, i), n in counts.most_common()],
            'evidence': 'structural preflight only; not collision, roof hiding, or gameplay proof'}

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--project', type=Path, required=True)
    p.add_argument('--client-data', type=Path, required=True)
    p.add_argument('--out', type=Path, required=True)
    a = p.parse_args()
    result = inspect(a.project, a.client_data)
    a.out.parent.mkdir(parents=True, exist_ok=True)
    a.out.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(f"PASS: {result['blocks']} blocks; fingerprint matched; report {a.out}")

if __name__ == '__main__':
    main()
