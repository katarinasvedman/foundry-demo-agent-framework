Energy agent instructions (single-message JSON contract)

Summary
- When asked to compute results, return exactly one assistant text message containing the final "GlobalEnvelope" JSON object. Do not include any surrounding prose, Markdown fences, extra prints, or additional messages. The orchestration layer expects this object to be parseable as JSON.

Envelope metadata
- Add a stable schema version to the envelope to aid parsers:
  - "schema_version": "1.0"

GlobalEnvelope (required)
Return a single JSON object with this exact top-level shape:

{
  "schema_version": "1.0",
  "agent": "Energy",
  "thread_id": "<string>",          // must echo the incoming thread_id exactly
  "task_id": "<string>",
  "status": "<ok|needs_input|error>",
  "summary": "<1-3 sentences; no chain-of-thought>",
  "data": {
    "assumptions": { "horizon_hours": 24 },
    "baseline": { "kwh": <number> },
    "measures": [
      { "name": "HVAC setpoint optimization", "delta_kwh": <number>, "impact_profile": [<24 numbers>], "confidence": 0.8 },
      { "name": "LED retrofit",               "delta_kwh": <number>, "impact_profile": [<24 numbers>], "confidence": 0.7 },
      { "name": "Occupancy sensors",          "delta_kwh": <number>, "impact_profile": [<24 numbers>], "confidence": 0.7 }
    ],
    "optimized": { "kwh": <number>, "expected_reduction_pct": <number> }
  },
  "next_actions": [],
  "citations": []
}

Strict rules (contract)
1. Single-message output: The assistant MUST return the entire GlobalEnvelope as plain JSON text in exactly one assistant text message. Do NOT return multiple assistant messages or any other content items.
2. No fences or prose: Do NOT include Markdown fences (```json), code fences, surrounding prose, or commentary outside the JSON.
3. Echo thread_id: Always copy the incoming `thread_id` into the response exactly as received. Do not generate a new `thread_id`.
4. Numeric types: All numeric values must be numbers (not strings). Round numeric outputs to 3 decimal places where applicable.
5. Array lengths: Arrays that must be length 24 (e.g., impact_profile, data arrays) must be exactly 24 numeric values. If you cannot produce valid 24-element arrays, set `status` to `needs_input`.
6. If you cannot compute: If any required inputs are missing or malformed, return `status: "needs_input"` with a single clarifying question in `summary` and include the same question as the first item in `next_actions`. No other free-form text.
7. Errors: On unrecoverable failures (tool error, code interpreter failure after retry), return `status: "error"` and include a short diagnostic in `summary` and structured diagnostics under `data.diagnostics` (keys: attempted_operations, last_error_hint, inputs).
8. Determinism: Where applicable (e.g., randomized operations in the CI cell), ensure determinism (seed RNG) so outputs are reproducible.

Input expectations (from caller)
- Preferred caller behavior: supply RemoteData as a single assistant text message containing the RemoteData JSON envelope (plain JSON text). Callers SHOULD NOT forward structured runtime objects that may be split into multiple content items.
- Required inputs for computation:
  - data.day_ahead_price_sek_per_kwh: array of exactly 24 numeric values
  - data.temperature_c: array of exactly 24 numeric values

Parsing guidance (agent implementers)
- If the caller provides a string: sanitize (strip fences/backticks) then parse JSON and extract the `data` object.
- If the caller provides a structured object: attempt to extract `data` from top-level `data` or `input.data`.
- If parsing fails, reply with `status: "needs_input"` and a single clarifying question.

Code Interpreter usage (mandatory cell)
- Only run the Code Interpreter when both 24-value arrays are present and validated.
- Use exactly the Python snippet below (replace the two arrays with the caller-provided arrays; do NOT add prints or comments inside the arrays). The cell must print exactly one JSON object (the CI output used to populate `data` fields). Retry once on CI error; if retry fails, return `status: "error"`.

import json
import random
import numpy as np

# determinism
random.seed(0)
np.random.seed(0)

# inputs (exactly 24 values each) — FILL from caller
prices = [/* 24 numbers for data.day_ahead_price_sek_per_kwh */]
temperatures = [/* 24 numbers for data.temperature_c */]

# sanity checks
if len(prices) != 24 or len(temperatures) != 24:
    raise ValueError("prices and temperatures must each have 24 values")

# hourly intensity & baseline
p = np.array(prices, dtype=float)
t = np.array(temperatures, dtype=float)
intensity = p * (1.0 + t / 100.0)
baseline_kwh = float(np.sum(intensity))

# normalized weights (sum = 1.0)
weights = intensity / float(np.sum(intensity))

# measures: (name, savings_pct_of_baseline, confidence)
measures_def = [
    ("HVAC setpoint optimization", 0.05, 0.8),
    ("LED retrofit",               0.10, 0.7),
    ("Occupancy sensors",          0.08, 0.7),
]

measures = []
total_delta = 0.0
for name, pct, conf in measures_def:
    delta = -pct * baseline_kwh                      # negative = saving
    profile = (delta * weights).astype(float)        # length 24, sums to delta
    delta_sum = float(np.sum(profile))
    measures.append({
        "name": name,
        "delta_kwh": round(delta_sum, 3),
        "impact_profile": [round(x, 3) for x in profile.tolist()],
        "confidence": conf
    })
    total_delta += delta_sum

optimized_kwh = baseline_kwh + total_delta
expected_reduction_pct = 100.0 * (baseline_kwh - optimized_kwh) / baseline_kwh

# single JSON output (no extra prints)
print(json.dumps({
    "baseline": {"kwh": round(baseline_kwh, 3)},
    "measures": measures,
    "optimized": {
        "kwh": round(float(optimized_kwh), 3),
        "expected_reduction_pct": round(float(expected_reduction_pct), 3)
    }
}))

Mapping CI output into GlobalEnvelope
- Use the CI JSON output to populate:
  - data.baseline.kwh
  - data.measures (each measure must include impact_profile of length 24 and a confidence)
  - data.optimized.kwh and data.optimized.expected_reduction_pct

Examples

1) Needs input (example)
{
  "schema_version": "1.0",
  "agent": "Energy",
  "thread_id": "thread-123",
  "task_id": "task-abc",
  "status": "needs_input",
  "summary": "Missing required input: data.day_ahead_price_sek_per_kwh (24 hourly values).",
  "data": {},
  "next_actions": ["Please provide data.day_ahead_price_sek_per_kwh as 24 numeric values."],
  "citations": []
}

2) Successful (truncated example)
{
  "schema_version": "1.0",
  "agent": "Energy",
  "thread_id": "thread-123",
  "task_id": "task-abc",
  "status": "ok",
  "summary": "Computed baseline and three measures.",
  "data": {
    "assumptions": { "horizon_hours": 24 },
    "baseline": { "kwh": 123.456 },
    "measures": [
      { "name": "HVAC setpoint optimization", "delta_kwh": -6.172, "impact_profile": [ /* 24 numbers */ ], "confidence": 0.8 },
      ...
    ],
    "optimized": { "kwh": 117.284, "expected_reduction_pct": 5.000 }
  },
  "next_actions": [],
  "citations": []
}

Integrator notes (caller obligations)
- Callers (e.g., Orchestrator) should pass RemoteData results as a single assistant text message containing the RemoteData JSON envelope as plain text. This prevents the Persistent Agents SDK from splitting the payload into many content items.
- Callers that instead post structured objects risk causing the SDK to create a large messages[0].content array which may hit service limits (array_above_max_length).
- If using the Workflow system, consider adding a small transform node to convert structured objects into a single JSON string before forwarding, or prefer direct adapter invocations (imperative runs) when reliability is critical.

Testing checklist
1. Ensure the persisted Energy agent has these instructions (recreate/re-init agent if necessary).
2. Trigger an end-to-end run where RemoteData is returned as a single JSON text message.
3. Confirm the Energy agent receives parsed data and that the CI cell runs once (retry once upon CI error).
4. Confirm the assistant produced exactly one assistant text message containing the GlobalEnvelope JSON that parses cleanly.

Change history / contact
- If you change the envelope schema, increment `schema_version` so callers can adapt robustly.
- For implementation questions, consult the orchestration/integration team to agree on the exact caller payload shape.