import sys

def load(path):
    rows = []
    with open(path, encoding='utf-8') as f:
        for line in f:
            parts = line.strip().split(', ')
            if len(parts) < 7:
                continue
            fstart = float(parts[2])
            fstep = float(parts[4])
            vals = [float(x) for x in parts[6:]]
            rows.append((fstart, fstep, vals))
    return rows

def flatten(rows):
    out = []
    for fstart, fstep, vals in rows:
        for i, v in enumerate(vals):
            freq = fstart + i * fstep
            out.append((freq, v))
    return out

base_path = sys.argv[1]
active_path = sys.argv[2]

base = flatten(load(base_path))
active = flatten(load(active_path))

base_d = {round(f): v for f, v in base}
active_d = {round(f): v for f, v in active}

diffs = []
for f in sorted(base_d.keys()):
    if f in active_d:
        d = active_d[f] - base_d[f]
        diffs.append((d, f))

diffs.sort(reverse=True)
print("Top 25 largest increases (active - baseline):")
print(f"{'Freq(MHz)':>12} {'Baseline(dB)':>14} {'Active(dB)':>12} {'Delta':>8}")
for d, f in diffs[:25]:
    print(f"{f/1e6:>12.4f} {base_d[f]:>14.2f} {active_d[f]:>12.2f} {d:>8.2f}")
