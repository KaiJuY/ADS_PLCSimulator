#!/usr/bin/env python3
"""Generate Polar2 config/module-config.json from the spec workbook (Python 3 standard library only).

Usage: python spec-to-config.py "<spec.xlsx>" "<output module-config.json>"
"""
import html
import json
import posixpath
import re
import sys
import zipfile

SVID_SHEET = "ModuleVariableList_SVID"
ECID_SHEET = "ModuleConfigParamList_ECID"
QUANTITY_SHEET = "Object Quantity"

# Source: note on the "Nozzle1 Flow Offset" row of the ECID sheet (Arm1,2 have 3 nozzles; Arm3 has 2; Arm4 has 1).
ARM_NOZZLES = {1: 3, 2: 3, 3: 2, 4: 1}

TYPE_MAP = {"Integer_32bit": "DINT", "Float_32bit": "REAL", "Boolean": "BOOL", "String(80)": "STRING"}

NAME_LIMIT = 30
UNIT_LIMIT = 10

ABBREVIATIONS = [
    ("Chemical", "Chem"), ("Related", "Rel"), ("ServiceId", "SvcId"), ("ToFacility", "ToFac"),
    ("Drainer", "Drn"), ("Nozzle", "Noz"), ("Position", "Pos"), ("Temperature", "Temp"),
    ("Pressure", "Pres"), ("Enable", "En"), ("Count", "Cnt"), ("Supply", "Sup"),
    ("Exhaust", "Exh"), ("Speed", "Spd"), ("Timeout", "TO"), ("Limit", "Lim"),
]


class SpecError(Exception):
    pass


def column_letters(cell_ref):
    return re.match(r"[A-Z]+", cell_ref).group(0)


def read_shared_strings(zf):
    if "xl/sharedStrings.xml" not in zf.namelist():
        return []
    text = zf.read("xl/sharedStrings.xml").decode("utf-8")
    result = []
    for si in re.findall(r"<si\b[^>]*?(?:/>|>.*?</si>)", text, re.S):
        result.append(html.unescape("".join(re.findall(r"<t\b[^>]*>(.*?)</t>", si, re.S))))
    return result


def sheet_paths(zf):
    workbook = zf.read("xl/workbook.xml").decode("utf-8")
    rels = zf.read("xl/_rels/workbook.xml.rels").decode("utf-8")
    targets = {}
    for rel in re.findall(r"<Relationship\b[^>]*>", rels):
        rid = re.search(r'\bId="([^"]*)"', rel)
        target = re.search(r'\bTarget="([^"]*)"', rel)
        if rid and target:
            targets[rid.group(1)] = target.group(1)
    paths = {}
    for sheet in re.findall(r"<sheet\b[^>]*>", workbook):
        name = html.unescape(re.search(r'\bname="([^"]*)"', sheet).group(1))
        rid = re.search(r'\br:id="([^"]*)"', sheet).group(1)
        target = targets[rid]
        paths[name] = target.lstrip("/") if target.startswith("/") else posixpath.normpath("xl/" + target)
    return paths


def read_sheet(zf, path, shared):
    """Return {row number: {column letter: text}}."""
    text = zf.read(path).decode("utf-8")
    rows = {}
    for row in re.finditer(r"<row\b([^>]*?)(?:/>|>(.*?)</row>)", text, re.S):
        number = int(re.search(r'\br="(\d+)"', row.group(1)).group(1))
        cells = {}
        for cell in re.finditer(r"<c\b([^>]*?)(?:/>|>(.*?)</c>)", row.group(2) or "", re.S):
            attrs, body = cell.group(1), cell.group(2) or ""
            column = column_letters(re.search(r'\br="([A-Z]+\d+)"', attrs).group(1))
            kind = re.search(r'\bt="([^"]*)"', attrs)
            kind = kind.group(1) if kind else ""
            if kind == "s":
                v = re.search(r"<v>(.*?)</v>", body, re.S)
                value = shared[int(v.group(1))] if v else ""
            elif kind == "inlineStr":
                value = html.unescape("".join(re.findall(r"<t\b[^>]*>(.*?)</t>", body, re.S)))
            else:
                v = re.search(r"<v>(.*?)</v>", body, re.S)
                value = html.unescape(v.group(1)) if v else ""
            cells[column] = value
        rows[number] = cells
    return rows


def read_quantities(rows):
    quantities = {}
    for number in sorted(rows):
        if number == 1:
            continue
        name = rows[number].get("A", "").strip()
        count = rows[number].get("B", "").strip()
        if name and count:
            quantities[name.replace(" ", "").lower()] = int(float(count))
    return quantities


def expand_rows(sheet_name, rows, quantities):
    """Yield (row number, display name, type text, unit) in output order."""
    groups = []  # (group, [(row, item, type, unit)]) in first-appearance order
    group = ""
    for number in sorted(rows):
        if number == 1:
            continue
        cells = rows[number]
        item = cells.get("C", "").strip()
        if not item and cells.get("B", "").strip() and cells.get("D", "").strip():
            # A row with a type but no item name (SVID "Module Mode") is one ungrouped item named by column B.
            groups.append(("", [(number, cells["B"].strip(), cells["D"].strip(), cells.get("E", "").strip())]))
            continue
        if cells.get("B", "").strip():
            group = cells["B"].strip()
        if not item:
            continue
        entry = (number, item, cells.get("D", "").strip(), cells.get("E", "").strip())
        if groups and groups[-1][0] == group:
            groups[-1][1].append(entry)
        else:
            groups.append((group, [entry]))

    result = []
    for group, items in groups:
        if group in ("", "-"):
            result.extend((n, item, t, u) for n, item, t, u in items)
            continue
        if not group.endswith("_X"):
            result.extend((n, f"{group} {item}", t, u) for n, item, t, u in items)
            continue
        base = group[:-2].strip()
        key = base.replace(" ", "").lower()
        if key not in quantities:
            raise SpecError(f"{sheet_name} row {items[0][0]}: no Object Quantity for '{base}'")
        base_name = base.replace(" ", "")
        for index in range(1, quantities[key] + 1):
            for n, item, t, u in items:
                if base_name == "Arm" and exceeds_nozzles(item, index):
                    continue
                result.append((n, f"{base_name}{index} {item}", t, u))
    return result


def exceeds_nozzles(item, arm_index):
    match = re.search(r"No(?:z|zzle)(\d)", item)
    if not match:
        return False
    if arm_index not in ARM_NOZZLES:
        raise SpecError(f"no nozzle count for Arm{arm_index}")
    return int(match.group(1)) > ARM_NOZZLES[arm_index]


def shorten(name):
    for long, short in ABBREVIATIONS:
        if len(name.encode("utf-8")) <= NAME_LIMIT:
            break
        name = name.replace(long, short)
    return name


def build_list(sheet_name, rows, quantities):
    entries = []
    names = set()
    for number, name, type_text, unit in expand_rows(sheet_name, rows, quantities):
        if type_text not in TYPE_MAP:
            raise SpecError(f"{sheet_name} row {number}: unsupported type '{type_text}' ({name})")
        if not name.isascii() or not unit.isascii():
            raise SpecError(f"{sheet_name} row {number}: non-ASCII name or unit ({name!r}, {unit!r})")
        comments = []
        short_name = shorten(name)
        if short_name != name:
            comments.append(name)
        short_unit = unit
        if len(unit.encode("utf-8")) > UNIT_LIMIT:
            short_unit = unit.replace("ml/sec", "ml/s")
            comments.append(f"unit: {unit}")
        if len(short_name.encode("utf-8")) > NAME_LIMIT:
            raise SpecError(f"{sheet_name} row {number}: name too long after abbreviation ({short_name})")
        if len(short_unit.encode("utf-8")) > UNIT_LIMIT:
            raise SpecError(f"{sheet_name} row {number}: unit too long after abbreviation ({short_unit})")
        if short_name in names:
            raise SpecError(f"{sheet_name} row {number}: duplicate name ({short_name})")
        names.add(short_name)
        entry = {"id": len(entries) + 1, "name": short_name, "unit": short_unit, "dataType": TYPE_MAP[type_text]}
        if comments:
            entry["comment"] = "; ".join(comments)
        entries.append(entry)
    return entries


def generate(xlsx_path):
    with zipfile.ZipFile(xlsx_path) as zf:
        paths = sheet_paths(zf)
        for required in (QUANTITY_SHEET, SVID_SHEET, ECID_SHEET):
            if required not in paths:
                raise SpecError(f"sheet '{required}' not found")
        shared = read_shared_strings(zf)
        sheets = {name: read_sheet(zf, paths[name], shared) for name in (QUANTITY_SHEET, SVID_SHEET, ECID_SHEET)}
    quantities = read_quantities(sheets[QUANTITY_SHEET])
    return {
        "schemaVersion": 4,
        "modules": [{
            "enabled": True,
            "moduleType": "Polar2",
            "slot": 1,
            "id": 1,
            "variableList": build_list(SVID_SHEET, sheets[SVID_SHEET], quantities),
            "configParamList": build_list(ECID_SHEET, sheets[ECID_SHEET], quantities),
        }],
    }


def main(argv):
    if len(argv) != 3:
        print('usage: python spec-to-config.py "<spec.xlsx>" "<module-config.json>"', file=sys.stderr)
        return 2
    try:
        config = generate(argv[1])
    except SpecError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    text = json.dumps(config, indent=2, ensure_ascii=False) + "\n"
    with open(argv[2], "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    module = config["modules"][0]
    print(f"SVID {len(module['variableList'])}, ECID {len(module['configParamList'])}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
