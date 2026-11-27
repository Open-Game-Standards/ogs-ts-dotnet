#!/usr/bin/env python3
"""lockstep.py — the conversion-table lockstep check.

Asserts the bind-path unit-conversion tables are identical across:
  * ogs-ts-dotnet   src/Ogs.Telemetry/Conversions.cs (+ Unit.cs wire strings)
  * ogs-ts-unreal   Source/OgsTelemetry/Private/TelemetryPublisherComponent.cpp
  * OGS-Telemetry-Standard  IMPLEMENTING.md (the non-normative guidance table)

Pair sets must match everywhere; factors must match between the two code
tables (the markdown table is checked for pair presence — its factor
column uses typography like ÷3.6, not machine syntax). The degF→degC
affine row is presence-checked in all three.

Usage: lockstep.py [--dotnet DIR] [--unreal DIR] [--standard DIR]
Defaults assume the three repos are siblings (or, on CI, checked out
side by side): pass the dirs explicitly otherwise.
"""
import argparse, math, re, sys

AFFINE = ("degF", "degC")
NO_UNIT = "(no unit)"


def parse_csharp(dotnet_dir):
    unit_cs = open(f"{dotnet_dir}/src/Ogs.Telemetry/Unit.cs", encoding="utf-8").read()
    conv_cs = open(f"{dotnet_dir}/src/Ogs.Telemetry/Conversions.cs", encoding="utf-8").read()
    # wire strings live as enum comments (ToWireString covers target units
    # only; source-only units are documented in the enum: `KilometrePerHour, // km/h`)
    wire = {}
    for m in re.finditer(r'^\s*(\w+),\s*//\s*(\S+)', unit_cs, re.M):
        name, tok = m.groups()
        wire[name] = tok.strip('"')
    pairs = {}
    for m in re.finditer(r'\(Unit\.(\w+),\s*Unit\.(\w+)\)\s*=>\s*([^,]+),', conv_cs):
        src, tgt, expr = m.groups()
        pairs[(wire[src], wire[tgt])] = expr
    affine_ok = "Unit.Fahrenheit && target == Unit.Celsius" in conv_cs
    return pairs, affine_ok


def parse_unreal(unreal_dir):
    cpp = open(f"{unreal_dir}/Source/OgsTelemetry/Private/TelemetryPublisherComponent.cpp",
               encoding="utf-8").read()
    pairs = {}
    for m in re.finditer(r'Pair\("([^"]*)",\s*"([^"]*)",\s*([^)]+)\)', cpp):
        src, tgt, expr = m.groups()
        pairs[(src, tgt)] = expr
    affine_ok = 'Is(From, "degF") && Is(To, "degC")' in cpp
    return pairs, affine_ok


def parse_standard(standard_dir):
    md = open(f"{standard_dir}/IMPLEMENTING.md", encoding="utf-8").read()
    rows = set()
    affine_ok = False
    for m in re.finditer(r'^\| [^|]+ \| ([^|]+?) \| ([^|]+?) \| [^|]+ \|$', md, re.M):
        src, tgt = m.group(1).strip(), m.group(2).strip()
        if src.startswith("Source ("):  # header row
            continue
        if "°F" in src:
            affine_ok = True
            continue
        # the standard uses superscript typography (C-3); wire spelling is ^2
        norm = lambda s: s.strip("`").replace("²", "^2").replace("³", "^3")
        rows.add((norm(src), "" if tgt == NO_UNIT else norm(tgt)))
    return rows, affine_ok


def factor(expr):
    e = expr.replace("Math.PI", "PI").strip()
    e = re.sub(r'\bPI\b', 'math.pi', e)
    check = e.replace('math.pi', '1.0')  # letters OK only as the PI constant
    if not re.fullmatch(r'[\d.eE+\-*/() ]+', check):
        raise ValueError(f"unparseable factor: {expr!r}")
    return eval(e, {"__builtins__": {}}, {"math": math})


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dotnet", default="../ogs-ts-dotnet")
    ap.add_argument("--unreal", default="../ogs-ts-unreal")
    ap.add_argument("--standard", default="../OGS-Telemetry-Standard")
    a = ap.parse_args()

    cs_pairs, cs_affine = parse_csharp(a.dotnet)
    ue_pairs, ue_affine = parse_unreal(a.unreal)
    md_pairs, md_affine = parse_standard(a.standard)

    errors = []
    if set(cs_pairs) != set(ue_pairs):
        errors.append(f"C#/UE pair sets differ: only-C#={set(cs_pairs)-set(ue_pairs)} "
                      f"only-UE={set(ue_pairs)-set(cs_pairs)}")
    else:
        for pair in cs_pairs:
            fc, fu = factor(cs_pairs[pair]), factor(ue_pairs[pair])
            if not math.isclose(fc, fu, rel_tol=1e-12):
                errors.append(f"factor mismatch {pair}: C#={cs_pairs[pair].strip()} UE={ue_pairs[pair].strip()}")
    if {p for p in cs_pairs} != md_pairs:
        errors.append(f"IMPLEMENTING.md table differs: only-code={set(cs_pairs)-md_pairs} "
                      f"only-doc={md_pairs-set(cs_pairs)}")
    if not (cs_affine and ue_affine and md_affine):
        errors.append(f"degF->degC affine missing: C#={cs_affine} UE={ue_affine} md={md_affine}")

    n = len(cs_pairs) + 1
    if errors:
        print(f"LOCKSTEP FAILED ({n} pairs + affine checked):")
        for e in errors:
            print(f"  {e}")
        sys.exit(1)
    print(f"lockstep OK: {n} conversion entries identical across dotnet, unreal, standard")


if __name__ == "__main__":
    main()
