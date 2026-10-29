"""Read-only attribution of a native RimWorld translation report; never claims live repair."""
import argparse
import collections
import csv
import hashlib
import json
import re
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

HANDLING = {
    "duplicate_keyed": "Keep the original diagnostic and selected text; report unresolved collision.",
    "missing_def_type": "Recheck loaded types; retain unavailable dependency/version targets as inapplicable.",
    "missing_def": "Recheck loaded Defs; retain absent targets as inapplicable without inventing or moving a Def.",
    "missing_handle": "Validate the live list; do not guess another component or index.",
    "handle_occurrence_out_of_bounds": "Validate the live occurrence; retain unresolved stale paths.",
    "index_out_of_bounds": "Validate the live index; retain unresolved stale paths.",
    "duplicate_target": "Merge only equal text at a proven identical live field; preserve differing candidates as conflicts.",
    "non_string_target": "Known rule-list XML corrections are fingerprint gated; otherwise require a valid native string/list target.",
    "missing_field": "Apply only explicit LoadAlias/TKey/BackCompatibility equivalence; retain unproven mappings as unresolved.",
    "null_reference": "Retain currently unavailable optional objects as inapplicable; do not allocate game data.",
    "shared_handle_buffer_exception": "ATC concurrent native handle calls were removed; manually retry valid native candidates and verify live values.",
}


def classify(line):
    rules = [
        ("Duplicate keyed translation", "duplicate_keyed"),
        ("doesn't correspond to any def type", "missing_def_type"),
        ("Found no ", "missing_def"),
        ("None of the list elements have a handle", "missing_handle"),
        ("handles matching this name", "handle_occurrence_out_of_bounds"),
        ("Index out of bounds", "index_out_of_bounds"),
        ("the list only has", "index_out_of_bounds"),
        ("refer to the same field", "duplicate_target"),
        ("does not exist", "missing_field"),
        ("Translated non-", "non_string_target"),
        ("Object reference not set", "null_reference"),
        ("chunkLength", "shared_handle_buffer_exception"),
    ]
    return next((kind for needle, kind in rules if needle in line), "unclassified")


def keys_for(line):
    for pattern in [r"^Duplicate keyed translation key: (\S+)", r"^Couldn't inject (\S+)",
                    r"^Found no .+? to match (\S+)", r" at path (\S+)\. Expected"]:
        match = re.search(pattern, line)
        if match:
            return [match[1]]
    match = re.search(r"Both (\S+)(?: \([^)]*\))? and (\S+)(?: \([^)]*\))? refer", line)
    return list(match.groups()) if match else []


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--root", type=Path, action="append", default=[])
    args = parser.parse_args()
    raw = args.report.read_bytes()
    text = raw.decode("utf-8-sig")
    headings = list(re.finditer(r"^========== (.*?) \((\d+)\) ==========\s*$", text, re.M))
    sections, rows = [], []
    for i, heading in enumerate(headings):
        body = text[heading.end():headings[i + 1].start() if i + 1 < len(headings) else len(text)]
        sections.append({"title": heading[1], "declaredCount": int(heading[2])})
        if "load errors" not in heading[1].lower():
            continue
        lines = [line for line in body.splitlines() if line.strip()]
        if len(lines) != int(heading[2]):
            raise ValueError(f"Report count mismatch: {heading[1]}")
        for line in lines:
            filename = re.findall(r"\(([^()\r\n]+\.xml)\)", line)
            type_match = re.search(r"(?:^Found no| into) ([\w.]+)", line)
            rows.append({"id": len(rows) + 1, "category": classify(line), "keys": keys_for(line),
                         "fileName": filename[-1] if filename else None,
                         "defType": type_match[1].split(".")[-1] if type_match else None,
                         "error": line, "handling": HANDLING.get(classify(line), "Requires review; no automatic modification."),
                         "sourceCandidates": []})

    filenames = {row["fileName"].lower() for row in rows if row["fileName"]}
    keyed_keys = {key for row in rows if row["category"] == "duplicate_keyed" for key in row["keys"]}
    by_file_key = collections.defaultdict(list)
    keyed_sources, malformed = collections.defaultdict(list), []
    for root in args.root:
        result = subprocess.run(["rg", "--files", "--hidden", str(root), "-g", "*.xml"],
                                capture_output=True, text=True, encoding="utf-8", check=True)
        for filename in result.stdout.splitlines():
            path = Path(filename)
            parts = [part.lower() for part in path.parts]
            if "languages" not in parts:
                continue
            lang = parts[parts.index("languages") + 1]
            if not (lang.startswith("chinesetraditional") or lang == "english"):
                continue
            keyed = "keyed" in parts
            if not keyed and ("definjected" not in parts or path.name.lower() not in filenames):
                continue
            try:
                document = ET.parse(path).getroot()
                if document.tag != "LanguageData":
                    continue
                for node in document:
                    key = node.tag
                    if key == "rep" and node.find("path") is not None:
                        key = node.findtext("path")
                    if not isinstance(key, str) or (keyed and key not in keyed_keys):
                        continue
                    source = {"path": str(path), "language": lang,
                              "value": "".join(node.itertext()),
                              "fullList": any(child.tag == "li" for child in node)}
                    if keyed:
                        keyed_sources[key].append(source)
                    else:
                        def_type = parts[parts.index("definjected") + 1]
                        by_file_key[(path.name.lower(), key)].append(dict(source, defType=def_type))
            except (OSError, ET.ParseError, ValueError) as error:
                malformed.append({"path": str(path), "error": str(error)})
    for row in rows:
        for key in row["keys"]:
            candidates = (keyed_sources[key] if row["category"] == "duplicate_keyed" else
                          by_file_key[(row["fileName"].lower(), key)] if row["fileName"] else [])
            row["sourceCandidates"].extend(dict(source, key=key) for source in candidates
                                           if not row["defType"] or source.get("defType") == row["defType"].lower())
    counts = dict(collections.Counter(row["category"] for row in rows))
    result = {"report": str(args.report), "reportSha256": hashlib.sha256(raw).hexdigest(),
              "reportLastWrite": args.report.stat().st_mtime, "loadErrors": len(rows), "categories": counts,
              "sections": sections, "rowsWithSourceCandidates": sum(bool(row["sourceCandidates"]) for row in rows),
              "explicitGeneratedFileErrors": sum("_AutoTranslated.xml" in row["error"] for row in rows),
              "scope": "Static source candidates across installed versions; runtime activation and successful injection are not inferred.",
              "malformedCandidateFiles": malformed, "rows": rows}
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "audit.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    (args.output / "TranslationReport.snapshot.txt").write_bytes(raw)
    with (args.output / "errors.tsv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.writer(stream, delimiter="\t")
        writer.writerow(["id", "category", "keys", "reported_filename", "handling", "source_candidates", "original_error"])
        for row in rows:
            writer.writerow([row["id"], row["category"], " | ".join(row["keys"]), row["fileName"], row["handling"],
                             " | ".join(dict.fromkeys(source["path"] for source in row["sourceCandidates"])), row["error"]])
    print(json.dumps({key: result[key] for key in ("loadErrors", "categories", "rowsWithSourceCandidates", "explicitGeneratedFileErrors", "scope")}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
