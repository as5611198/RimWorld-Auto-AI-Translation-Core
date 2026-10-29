"""Read-only, offline baseline replay. Never treats key matches as successful injection."""
import argparse, collections, hashlib, json, re, shutil
from pathlib import Path
import xml.etree.ElementTree as ET

p = argparse.ArgumentParser()
p.add_argument('--audit-root', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
p.add_argument('--pack', type=Path, required=True)
a = p.parse_args(); a.output.mkdir(parents=True, exist_ok=True)
source = a.audit_root / 'TranslationReport.snapshot.txt'
raw = source.read_bytes(); text = raw.decode('utf-8-sig')
matches = list(re.finditer(r'^========== (.*?) \((\d+)\) ==========\s*$', text, re.M))
errors = []
for i, m in enumerate(matches):
    if 'load errors' in m[1].lower():
        lines = [l for l in text[m.end():matches[i+1].start() if i+1 < len(matches) else len(text)].splitlines() if l.strip()]
        assert len(lines) == int(m[2]); errors += lines
def category(line):
    for term, label in [('None of the list elements have a handle', 'missing_handle'), ('Index out of bounds', 'index_out_of_bounds'),
        ('Object reference not set', 'null_reference'), ('Verse.IntVec3', 'coordinate_index_type'), ('Found no ', 'missing_def'),
        ('Duplicate', 'duplicate_key'), ('does not exist', 'missing_field'), ('Exception loading', 'file_parse'),
        ('Translated non-', 'non_text_field'), ('untranslatable field', 'untranslatable_field')]:
        if term.lower() in line.lower(): return label
    return 'other'
other = []
for line in errors:
    if category(line) != 'other': continue
    if "doesn't correspond to any def type" in line: reason = 'unavailable_def_type_directory'
    elif 'Trying to translate' in line and 'the list only has' in line: reason = 'terminal_index_out_of_bounds'
    elif 'missing' in line.lower() and ('Item' in line or 'Count' in line): reason = 'unsupported_container'
    elif 'handles matching this name' in line: reason = 'handle_occurrence_out_of_bounds'
    elif 'same time' in line: reason = 'whole_list_and_item_conflict'
    elif 'Def named ' in line and 'not found' in line: reason = 'missing_def_during_traversal'
    else: reason = 'needs_review'
    other.append({'reason': reason, 'error': line})
generated_matches = json.loads((a.audit_root / 'generated-key-matches.json').read_text(encoding='utf-8-sig'))
assert len(generated_matches) == 14804
for filename in ['report-analysis.json', 'generated-key-matches.json', 'result.json', 'TranslationReport.snapshot.txt', 'analyze_report.py']:
    shutil.copy2(a.audit_root / filename, a.output / filename)
files, entries, malformed = [], 0, []
for f in sorted(a.pack.rglob('*.xml')):
    content = f.read_bytes()
    files.append({'path': str(f), 'sha256': hashlib.sha256(content).hexdigest(), 'bytes': len(content)})
    try: entries += len(ET.fromstring(content))
    except ET.ParseError as ex: malformed.append({'path': str(f), 'error': str(ex)})
result = {'reportSha256': hashlib.sha256(raw).hexdigest(), 'errors': len(errors), 'categories': dict(collections.Counter(map(category, errors))),
    'otherCategories': dict(collections.Counter(r['reason'] for r in other)), 'other': other,
    'packFiles': len(files), 'packEntries': entries, 'packManifest': files, 'packMalformed': malformed,
    'matchedErrorRecords': len(generated_matches), 'translationApiCalls': 0,
    'scope': 'Static read-only inventory; runtime type decisions deferred to loaded game. No injected-success claim.'}
(a.output/'dry-run.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({k: v for k, v in result.items() if k not in ['other', 'packManifest']}, ensure_ascii=False, indent=2))
