import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import sys
import json
from datetime import datetime
import os

# default data (keeps previous behavior when no input provided)
hours = list(range(24))

default_measures = [
    ("HVAC setpoint optimization", [1.5096, 1.5276, 1.6764, 1.8024, 1.998, 2.1468, 2.3376, 2.4636, 2.5284, 2.6184, 2.634, 2.5932, 2.5404, 2.388, 2.3028, 2.1924, 1.95, 1.8624, 1.7304, 1.5684, 1.4376, 1.4328, 1.3944, 1.4532]),
    ("LED retrofit", [1.0064, 1.0184, 1.1176, 1.2016, 1.332, 1.4312, 1.5584, 1.6424, 1.6856, 1.7456, 1.756, 1.7288, 1.6936, 1.592, 1.5352, 1.4616, 1.3, 1.2416, 1.1536, 1.0456, 0.9584, 0.9552, 0.9296, 0.9688]),
    ("Occupancy sensors", [0.80512, 0.81472, 0.89408, 0.96128, 1.0656, 1.14496, 1.24672, 1.31392, 1.34848, 1.39648, 1.4048, 1.38304, 1.35488, 1.2736, 1.22816, 1.16928, 1.04, 0.99328, 0.92288, 0.83648, 0.76672, 0.76416, 0.74368, 0.77504])
]

# If a JSON file is provided, parse measures from it. Expected format matches the agent's GlobalEnvelope:
# { "data": { "measures": [ {"name":..., "impact_profile": [...] }, ... ] } }
measures = default_measures
input_source = None
if len(sys.argv) > 1:
    input_path = sys.argv[1]
    input_source = input_path
    try:
        with open(input_path, 'r', encoding='utf-8') as f:
            payload = json.load(f)
        measures = []
        # navigate into payload to find measures
        data = payload.get('data') or payload
        measures_list = data.get('measures') if isinstance(data, dict) else None
        if not measures_list:
            # try top-level measures
            measures_list = payload.get('measures')
        if measures_list and isinstance(measures_list, list):
            for m in measures_list:
                name = m.get('name') if isinstance(m, dict) else str(m)
                profile = m.get('impact_profile') if isinstance(m, dict) else None
                if profile and len(profile) == 24:
                    measures.append((name, profile))
        if not measures:
            print('No valid measures found in provided JSON; falling back to defaults.')
            measures = default_measures
    except Exception as e:
        print('Failed to parse JSON input:', e)
        measures = default_measures

# Plot
plt.figure(figsize=(10,5))
for name, profile in measures:
    if len(profile) != 24:
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
