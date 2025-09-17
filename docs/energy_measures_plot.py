import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import sys
import json
from datetime import datetime
import os

# default data (keeps previous behavior when no input provided)
hours = list(range(24))

def default_measures_list():
    return [
        ("HVAC setpoint optimization", [1.5096, 1.5276, 1.6764, 1.8024, 1.998, 2.1468, 2.3376, 2.4636, 2.5284, 2.6184, 2.634, 2.5932, 2.5404, 2.388, 2.3028, 2.1924, 1.95, 1.8624, 1.7304, 1.5684, 1.4376, 1.4328, 1.3944, 1.4532]),
        ("LED retrofit", [1.0064, 1.0184, 1.1176, 1.2016, 1.332, 1.4312, 1.5584, 1.6424, 1.6856, 1.7456, 1.756, 1.7288, 1.6936, 1.592, 1.5352, 1.4616, 1.3, 1.2416, 1.1536, 1.0456, 0.9584, 0.9552, 0.9296, 0.9688]),
        ("Occupancy sensors", [0.80512, 0.81472, 0.89408, 0.96128, 1.0656, 1.14496, 1.24672, 1.31392, 1.34848, 1.39648, 1.4048, 1.38304, 1.35488, 1.2736, 1.22816, 1.16928, 1.04, 0.99328, 0.92288, 0.83648, 0.76672, 0.76416, 0.74368, 0.77504])
    ]

measures = default_measures_list()
input_source = None
if len(sys.argv) > 1:
    input_path = sys.argv[1]
    input_source = input_path
    try:
        with open(input_path, 'r', encoding='utf-8') as f:
            payload = json.load(f)
        measures = []
        # navigate into payload to find measures
        data = payload.get('data') if isinstance(payload, dict) else payload

        # Prefer explicit 'measures' array
        if isinstance(data, dict) and 'measures' in data and isinstance(data['measures'], list):
            for m in data['measures']:
                if isinstance(m, dict):
                    name = m.get('name') or m.get('label') or 'measure'
                    profile = m.get('impact_profile') or m.get('impact') or m.get('profile')
                    if isinstance(profile, list) and len(profile) == 24:
                        measures.append((name, profile))
        else:
            # Try common alternative structures: measure_1, measure_2, measure_3
            # Or keys like 'measure_1_kwh' or 'measure_1' containing lists or dicts with impact_profile
            if isinstance(data, dict):
                # collect keys that look like measures
                keys = [k for k in data.keys() if k.lower().startswith('measure') or k.lower().startswith('measure_')]
                keys_sorted = sorted(keys)
                for k in keys_sorted:
                    val = data.get(k)
                    name = k
                    profile = None
                    if isinstance(val, dict):
                        profile = val.get('impact_profile') or val.get('impact') or val.get('impact_profile_kwh')
                        name = val.get('name') or val.get('label') or k
                    elif isinstance(val, list) and len(val) == 24:
                        profile = val
                    if profile and isinstance(profile, list) and len(profile) == 24:
                        measures.append((name, profile))

                # Another pattern: top-level keys like 'measure_1_kwh' or 'measure_2_kwh'
                if not measures:
                    for k, v in data.items():
                        if k.lower().endswith('_kwh') and isinstance(v, list) and len(v) == 24:
                            measures.append((k, v))

                # As a last attempt, check for baseline + measure_?_kwh naming in nested data
                if not measures:
                    # look for keys inside data that are dicts containing 'impact_profile'
                    for k, v in data.items():
                        if isinstance(v, dict) and 'impact_profile' in v and isinstance(v['impact_profile'], list) and len(v['impact_profile']) == 24:
                            name = v.get('name') or k
                            measures.append((name, v['impact_profile']))

        if not measures:
            print('No valid measures found in provided JSON; falling back to defaults.')
            measures = default_measures_list()
    except Exception as e:
        print('Failed to parse JSON input:', e)
        measures = default_measures_list()

# Plot
plt.figure(figsize=(10,5))
for name, profile in measures:
    if not (isinstance(profile, list) and len(profile) == 24):
        # simple safeguard
        continue
    plt.plot(hours, profile, marker='o', label=name)

plt.xticks(hours)
plt.xlabel('Hour of day')
plt.ylabel('kWh impact (per-hour)')
plt.title('Measure impact profiles (24 hours)')
plt.grid(alpha=0.3)
plt.legend()
plt.tight_layout()

# ensure output dir exists
out_dir = os.path.join('docs')
os.makedirs(out_dir, exist_ok=True)
# timestamped filename
ts = datetime.now().strftime('%Y%m%dT%H%M%S')
out_path = os.path.join(out_dir, f'energy_measures_{ts}.png')
plt.savefig(out_path, dpi=150)
print('Saved plot to', out_path)
