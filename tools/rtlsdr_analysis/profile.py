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
bin_mhz = float(sys.argv[3]) if len(sys.argv) > 3 else 0.5

base = flatten(load(base_path))
active = flatten(load(active_path))

base_d = {round(f): v for f, v in base}
active_d = {round(f): v for f, v in active}

# Bucket into coarse bins (default 0.5MHz) and print mean delta per bucket, to see the
# actual shape of the elevated region rather than just top-N spikes.
buckets = {}
for f in sorted(base_d.keys()):
    if f in active_d:
        d = active_d[f] - base_d[f]
        bucket = round(f / (bin_mhz * 1e6)) * bin_mhz
        buckets.setdefault(bucket, []).append(d)

print(f"{'Freq(MHz)':>10} {'MeanDelta':>10} {'MinDelta':>10} {'MaxDelta':>10} {'N':>4}")
for freq in sorted(buckets.keys()):
    ds = buckets[freq]
    mean_d = sum(ds) / len(ds)
    print(f"{freq:>10.2f} {mean_d:>10.2f} {min(ds):>10.2f} {max(ds):>10.2f} {len(ds):>4}")
