**Audit all Claude-authored documentation in this project for accuracy against the current codebase.** Every claim must be verifiable against code, git history, or filesystem state. This is a maintenance pass, not a rewrite.

## Scope

In-scope files:
1. `CLAUDE.md` at the repo root (project instructions checked into the codebase)
2. `~/.claude/CLAUDE.md` (global user instructions) - audit only if it makes claims about *this* project; leave generic personal-convention rules alone
3. Every file under `C:\Users\Nathan\.claude\projects\C--Users-Nathan-OneDrive-ECA-Resources-Visual-Studio-GM-ECU-Simulator\memory\` - both `MEMORY.md` (the index) and every `*.md` it points to
4. `Core/Dps/README.md` and any other in-tree README under `Core/` or `tools/` referenced from the above

Out-of-scope: workflow files under `~/.claude/workflows/`, plan files under `~/.claude/plans/`, anything that isn't a memory/CLAUDE/README.

## Procedure

**Phase 1 - Inventory (do this yourself, single pass).**
List every in-scope file with its declared `type` and `description` from frontmatter (or one-line summary if no frontmatter). Build a table mapping file -> set of factual claims that need verification. A "claim" is anything specific: a file path, a function name, a line number, a constant value, an offset, a date, a behavior assertion, a "X is shipped / wired / removed" statement. Skip pure opinion ("user prefers X") - those aren't verifiable.

**Phase 2 - Delegate verification in parallel.** Group claims by topic and dispatch one Explore agent per group, in parallel (single message, multiple Agent tool calls). Topic groupings to use:

- **Architecture & file-path claims** (does `Core/Bus/VirtualBus.cs::DispatchHostTx` exist? do the 14 PassThru exports listed in CLAUDE.md match `PassThruShim/exports.def`? does `Core/Dps/ArchivePrimer.cs` have the public methods named?)
- **DPS / Prime-From-Archive claims** (does `FamilyFromOsPartNumber` still exist or was it removed per `feedback_no_os_pn_database.md`? is `BinIdentificationReader.ReadArchiveOsHeader` real? is the boot-region offset survey at the cited path?)
- **Security module claims** (do `gm-e92`, `gm-t43`, `gm-e67`, `gm-programming-bypass` all exist in `SecurityModuleRegistry`? does the bypass-by-policy `00 00` seed unlock at the cited line number?)
- **Schema/persistence claims** (do `LengthBytes` and `StaticBytes` fields exist on `PidDto` and `Pid`? is `SpsType` actually removed from the schema?)
- **Tooling claims** (do `tools/dps_utility_builder/*.py` scripts named in CLAUDE.md exist? do referenced reports under `tools/dps_utility_builder/reports/` exist?)
- **Test / build claims** (do the `dotnet test` filters resolve? do `Tests/test_*.ps1` files exist as named?)
- **Status claims** (is `IdleBusSupervisor` actually stubbed per the memory? is glitch injection still un-wired per CLAUDE.md? was the validate-download CRC fix actually landed?)

Each agent gets the relevant memory excerpts + claims to verify and reports back per claim: accurate / partially accurate (with delta) / wrong / stale (refers to thing that no longer exists). Cap each agent at ~300 words.

**Phase 3 - Synthesize.** Aggregate agent findings into a single report with three sections:
1. **Files that are fully accurate** (just list them)
2. **Files needing edits** - for each, list the specific stale/wrong claim and the corrected wording, with the verifying file:line reference
3. **Files that should be deleted** - memories that describe state that no longer exists or has been fully superseded

**Phase 4 - Apply fixes.** After I review the report, edit the affected files. Update `MEMORY.md` index lines to match any renamed/refocused memory bodies. Don't add new memories during this pass - only correct or prune existing ones.

## Rules

- Verify with Read/Grep/Glob - never trust the memory's own claim about itself. If a memory says "X is at line 128," open the file and check.
- Dates in memories are frozen snapshots; don't "update" a date just because time has passed. Only flag a dated claim if the underlying fact changed.
- If a memory and the code disagree, the code wins - update the memory, don't "fix" the code.
- Don't expand scope. If you find a real bug in the code, flag it via `spawn_task`, don't fix it inline.
- Report findings as you go for any agent that returns wrong or stale results, so I can sanity-check before you edit.

Start with Phase 1 and show me the inventory + claim table before dispatching agents.
