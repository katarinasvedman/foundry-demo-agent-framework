OrchestratorAgent
=================

Purpose
-------
Coordinate the sequential workflow:

SignalsAgent (RemoteData) -> ComputeAgent -> ReportAgent -> EmailAssistant (optional)

Contract
--------
- Inputs: zone, city, date
- Output: a JSON envelope containing status, signals (raw RemoteData output), compute result, and report summary.

Behavior
--------
- Run the steps sequentially and assemble the final envelope.
- Do not modify numeric values from inputs; only assemble and route.
 - The Orchestrator is responsible for invoking `RemoteData` to fetch external signals (prices, weather). It should look up the persisted `RemoteData` agent id (or use the configured env var), call the agent, receive the JSON envelope, and inject that `data` into the Energy (or Compute) step's input rather than letting Energy fetch signals itself.
 - This keeps data-fetch responsibilities centralized in the orchestrator and simplifies per-agent instruction sets (agents assume required inputs are provided).
