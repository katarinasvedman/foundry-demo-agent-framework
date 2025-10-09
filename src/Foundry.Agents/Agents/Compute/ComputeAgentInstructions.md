ComputeAgent
============

Purpose
-------
Run a single deterministic code-cell that processes numeric data and return JSON only. This agent is intentionally narrow: it must not call external services directly and must return strictly-structured JSON suitable for downstream ReportAgent and OrchestratorAgent consumption.

Contract
--------
- Input: text representing the code cell or a structured instruction
- Output: JSON string with fields like { status: "ok"|"error", data: { ... } }

Behavior
--------
- Only a single cell will be executed per call.
- Execution must be deterministic. Avoid randomness, external calls, or non-deterministic IO.
