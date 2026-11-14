"""Summarize mobile_sync evidence without treating draw submissions as visible pixels."""
import argparse
from collections import Counter
import json
from pathlib import Path


def analyze(folder: Path):
    report = {}
    for role, other in (('a', 'b'), ('b', 'a')):
        peer = int((folder / (other + '.ready')).read_text())
        positions, offsets, directions = set(), set(), set()
        results, packets = Counter(), 0
        present, stale, equipment, pixel_checks, pixel_mismatches = 0, 0, 0, 0, 0
        with (folder / (role + '.jsonl')).open(encoding='utf-8-sig') as stream:
            for line in stream:
                row = json.loads(line)
                if row.get('kind') == 'pixel_check':
                    pixel_checks += 1
                    pixel_mismatches += not row['equal']
                elif row.get('kind') == 'present':
                    present += 1
                    stale += row['pending'] > 0
                elif row.get('serial') == peer:
                    if row.get('kind') == 'movement':
                        packets += 1
                    else:
                        positions.add((row['x'], row['y'], row['z']))
                        offsets.add((row['ox'], row['oy']))
                        directions.add(row['direction'])
                        results[row['result']] += 1
                        equipment += row['layer'] != 0 and row['result'] == 'submit'
        report[role] = dict(present_frames=present, stale_frames=stale,
                            pixel_checks=pixel_checks, pixel_mismatches=pixel_mismatches,
                            peer_positions=len(positions), peer_offsets=len(offsets),
                            peer_directions=sorted(directions), peer_movement_packets=packets,
                            peer_equipment_submissions=equipment, peer_draw_results=dict(results),
                            passed=present > 0 and stale == 0 and pixel_checks > 0 and pixel_mismatches == 0
                            and packets > 0 and len(positions) > 1
                            and len(offsets) > 1 and equipment > 0)
    (folder / 'analysis.json').write_text(json.dumps(report, indent=2))
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('folder', type=Path)
    args = parser.parse_args()
    print(json.dumps(analyze(args.folder), indent=2))
