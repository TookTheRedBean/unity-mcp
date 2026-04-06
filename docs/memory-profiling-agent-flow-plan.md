# Memory Profiling Agent Flow Plan

This document captures the current plan for memory profiling workflows that combine:

- generic `unity-mcp` profiler tools
- HeroAge-specific Unity scenario orchestration
- offline snapshot analysis using the existing HeroAge tooling

It is intended as a durable implementation reference for future work.

## Goals

- Support reliable memory snapshot capture through MCP.
- Make snapshot-driven investigations usable by an agent end-to-end.
- Reuse the existing HeroAge offline analysis tools instead of rebuilding them inside `unity-mcp`.
- Keep upstream `unity-mcp` changes generic and upstreamable.
- Keep project-specific scenario logic in HeroAge custom tools.

## Current State

### In `unity-mcp`

- `manage_profiler` already exists and exposes:
  - `memory_take_snapshot`
  - `memory_list_snapshots`
  - `memory_compare_snapshots`
- Current snapshot compare is intentionally shallow:
  - file metadata only
  - not object/type-level analysis
- Addressables support exists via `manage_addressables`.

Relevant files:

- `MCPForUnity/Editor/Tools/Profiler/ManageProfiler.cs`
- `MCPForUnity/Editor/Tools/Profiler/Operations/MemorySnapshotOps.cs`
- `Server/src/services/tools/manage_profiler.py`
- `MCPForUnity/Editor/Tools/Addressables/ManageAddressables.cs`
- `Server/src/services/tools/manage_addressables.py`

### In HeroAge

- Memory Profiler package is installed:
  - `com.unity.memoryprofiler`
- Addressables package is installed:
  - `com.unity.addressables`
- Offline analysis tooling already exists:
  - `/Users/kminer/heroage/tools/memory-analyze.sh`
  - `/Users/kminer/heroage/tools/memory-diff.sh`
  - `/Users/kminer/heroage/tools/memory-diff.py`
  - `/Users/kminer/heroage/tools/README.md`
- HeroAge now points `com.coplaydev.unity-mcp` at the `beta` branch:
  - `/Users/kminer/heroage/client/Packages/manifest.json`

Note:

- `packages-lock.json` was intentionally left alone so Unity can re-resolve the package.

## Existing Package-Gating Pattern In `unity-mcp`

The repo already has an established pattern for optional Unity packages.

### Pattern A: graceful fallback plus `ping`

Used when a tool can still do useful work without the package.

Example:

- `manage_camera`
  - works without Cinemachine for basic camera operations
  - exposes `ping`
  - rejects only Cinemachine-specific actions when the package is missing

### Pattern B: hard gate with explicit install/init error

Used when the tool is not meaningful without the package.

Examples:

- `manage_probuilder`
  - checks package availability via reflection
  - returns a direct install message if missing
- memory snapshot operations
  - return a `com.unity.memoryprofiler` missing-package error
- Addressables operations
  - `ping` reports availability
  - actual operations fail with a clear initialize/install message if settings are missing

### Pattern C: route remediation through `manage_packages`

When a package is missing, the tool should point the user or agent to install it through:

- `manage_packages(action="add_package", package="...")`

This is the right pattern to continue using.

## Recommended Architecture

Split the system into three layers.

### 1. Upstreamable generic layer in `unity-mcp`

Responsibility:

- capture snapshots
- expose basic snapshot metadata
- perform lightweight listing/comparison

What belongs here:

- better `memory_take_snapshot` parameters
- better metadata in responses
- optional capture flag support
- optional screenshot capture support if exposed safely
- lightweight validation and package checks

What does not belong here:

- HeroAge-specific scenario setup
- direct coupling to HeroAge analysis scripts
- highly project-specific experiment recipes

### 2. HeroAge-specific Unity orchestration layer

Responsibility:

- put the game into deterministic states before capture
- coordinate Addressables warmup/reset flows
- define repeatable scenarios an agent can invoke

This should live as HeroAge custom tools under the Unity project.

Good candidate tools:

- `hero_memory_prepare_state`
- `hero_memory_run_scenario`
- `hero_memory_capture_checkpoint`
- `hero_addressables_prepare`

These tools should encapsulate brittle project-specific logic such as:

- navigate to home screen
- enter battle
- return to home
- wait for loads/animations/settle frames
- warm specific Addressables labels or groups
- optionally clear caches or force GC

### 3. Offline analysis layer in HeroAge tools

Responsibility:

- analyze a single `.snap`
- diff two `.snap` files
- produce machine-readable JSON plus concise summaries

This already exists and should be reused.

Preferred interface:

- shell wrappers stay as the source of truth
- optionally add a thin MCP wrapper later if agent ergonomics require it

## Agentic Flow

The preferred end-to-end flow is:

1. Verify environment
   - check profiler tool availability
   - check Addressables availability
   - confirm HeroAge is on the intended `unity-mcp` branch

2. Prepare scenario
   - invoke HeroAge custom tool to move into a canonical state
   - optionally warm or clear Addressables
   - wait for stabilization

3. Capture baseline snapshot
   - call `manage_profiler(action="memory_take_snapshot", ...)`

4. Run scenario
   - invoke HeroAge custom tool for the target action or journey

5. Capture follow-up snapshot
   - call `manage_profiler(action="memory_take_snapshot", ...)`

6. Analyze snapshots offline
   - run `memory-analyze.sh` for one-off drilldown
   - run `memory-diff.sh` for before/after comparison

7. Summarize
   - report top growers
   - report new/removed types
   - identify likely leak candidates
   - decide whether another narrower experiment is needed

## What To Implement First

### Slice 1: improve generic snapshot capture

Extend `memory_take_snapshot` to support:

- `snapshot_path`
- `capture_flags`
- `include_screenshot`
- `snapshot_label`
- better returned metadata

At minimum, return:

- snapshot path
- size in bytes/MB
- UTC timestamp
- effective capture flags

### Slice 2: keep compare lightweight in `unity-mcp`

Do not try to embed full analyzer logic into `unity-mcp`.

Keep `memory_compare_snapshots` lightweight unless there is a clearly generic improvement such as:

- stronger validation
- better metadata diff
- capture-flags compatibility warnings

Deep diffing should stay in HeroAge tools.

### Slice 3: add one HeroAge scenario tool

Implement one narrow scenario first:

- `home -> battle -> home`

This gives a stable path for:

- baseline capture
- action capture
- post-action diff

### Slice 4: add one thin wrapper for offline analysis

Either:

- a HeroAge MCP/custom tool wrapper around `memory-diff.sh`

or:

- a documented Codex workflow script

The wrapper should return:

- diff JSON path
- top growers
- top shrinkers
- summary totals

## Addressables Integration

Addressables should be treated as a setup primitive for memory experiments.

Typical usage:

- set active profile
- build or update content if needed
- warm a label or group
- capture a baseline snapshot
- perform gameplay action
- capture the after snapshot

This is valuable because it creates controlled memory boundaries for:

- cold loads
- warm loads
- cache retention checks
- Addressables leak investigations

## Different Targets

There are two meanings of "different targets":

### A. Different game states or workloads

This should be supported early.

Examples:

- home screen
- shop
- battle
- post-battle
- after Addressables warmup

This belongs in HeroAge custom tools.

### B. Different profiler capture endpoints

Examples:

- Editor
- current connected Player
- specific connected device or player target

This should be treated cautiously.

The installed Memory Profiler package clearly supports:

- Editor capture
- running Player capture
- internal target-selection UI

However, the target-selection behavior lives behind package internals and editor window services. Do not build a broad "select any target by ID" MCP surface in v1 unless a stable public automation entrypoint is confirmed.

Recommendation:

- v1 supports Editor / current active target capture only
- explicit multi-target selection is deferred

## Metadata

Snapshot metadata is worth exposing because it makes agent analysis much more useful.

Future enhancement:

- attach scenario metadata to snapshots
- include values such as:
  - scenario name
  - level/screen
  - player/account ID if relevant
  - Addressables profile or warmed labels
  - frame count / uptime

If possible, use the Memory Profiler package's metadata facilities instead of inventing a parallel file-naming convention.

## Open Questions

- Should `memory_take_snapshot` remain a single synchronous call, or become a polled async job for larger captures?
- Can capture flags be exposed cleanly across supported Unity/package versions without fragile reflection?
- Is there a safe public path for current-player capture, or only internal editor-window plumbing?
- Should HeroAge get a dedicated MCP wrapper for `memory-diff.sh`, or is a documented script workflow enough?

## Recommended Immediate Next Step

Start with Slice 1:

- improve `manage_profiler memory_take_snapshot`
- add better response metadata
- add optional capture flags

Then implement one HeroAge scenario tool for a single repeatable memory experiment.

That gives a usable vertical slice quickly without overcommitting to target-selection or deep upstream analyzer work.
