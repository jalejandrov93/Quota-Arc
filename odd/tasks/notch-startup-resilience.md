# Notch startup resilience (Windows)

## Objective

The notch must never fail to appear because optional third-party telemetry
threw. Startup order and error handling are the fix; the notch rendering code
itself is already correct.

## Problem

On Windows the notch never appears. The tray icon works, Settings opens, but no
notch window is ever created — and nothing is logged anywhere.

Verified causal chain (all file:line in `windows/QuotaArc/`):

1. `Providers/SqliteStore.cs:43-54` — `Rows()` calls `cmd.ExecuteReader()` with
   no try/catch.
2. The user's Cursor store (`%APPDATA%\Cursor\User\globalStorage\state.vscdb`,
   257 MB) contains only the tables `ItemTable` and `cursorDiskKV`. The table
   `composerHeaders` does **not** exist — Cursor changed its schema.
3. `Sessions/CursorActivityMonitor.cs:34-35` queries
   `SELECT value FROM composerHeaders WHERE isArchived = 0 ORDER BY recency DESC LIMIT 40`
   → throws `SqliteException: no such table: composerHeaders`.
4. `App.xaml.cs:290-291` — `RefreshSessions()` runs
   `foreach (var (id, read) in _monitors) next[id] = read();` with no try/catch
   → propagates.
5. `App.xaml.cs:208` — `RefreshSessions()` is called here.
6. `App.xaml.cs:219` — `fleet.Show()` is **after** it, so it never runs.
7. `App.xaml.cs:32-36` — the `DispatcherUnhandledException` handler sets
   `args.Handled = true` and calls `Log.Error`, which is `Debug.WriteLine`
   (`App/Log.cs:10-11`) and therefore a no-op in Release. The exception vanishes
   without a trace.

The tray is created at `App.xaml.cs:114` and Settings at `:94`, both before line
208 — which is exactly why those two work and the notch does not.

## Why

Optional telemetry from a third-party product must not be able to take down the
application's primary UI. Codenotch, the upstream Rust project this was ported
from, guards the same query with `.ok()?` (`windows/codenotch/src/activity.rs`);
the C# port lost that tolerance.

## Evidence

- `QUOTAARC_DEMO=1` skips the whole provider block and reaches `fleet.Show()`
  directly → notch window visible, `exStyle=0x80800A8`
  (`WS_EX_NOACTIVATE|LAYERED|TOOLWINDOW|TRANSPARENT|TOPMOST`).
- Normal mode: zero visible windows after 75 s of polling.
- `codexbarEnabled=0`: identical failure — the Cursor monitor runs on both
  provider branches.
- Live query against the real store confirms `no such table: composerHeaders`.
- Existing tests never caught this: `QuotaArc.Tests/CursorActivityMonitorTests.cs:276`
  **creates** the `composerHeaders` table in its fixture, so the missing-table
  case was never exercised.
- Upstream (`vaiibhavkale/Quota-Arc`) HEAD still has both unguarded call sites.

## Scope

Authorized: `windows/QuotaArc/` and `windows/QuotaArc.Tests/`.
Out of scope: the macOS `Sources/` tree, provider business logic, any change to
what the notch renders.

## TDD

Mode: enabled (strict). Runner: `dotnet test` (dotnet 10.0.303, available on the
Windows side only — invoked from WSL through `powershell.exe`).
RED must be observed before implementation.

## Tasks

- [x] T1 — RED: add tests covering a `state.vscdb` whose `composerHeaders` table
      is absent, for both `SqliteStore.Rows` and `CursorActivityMonitor.Read`.
      Expected today: they throw.
      Observed: both new tests failed with
      `Microsoft.Data.Sqlite.SqliteException: SQLite Error 1: 'no such table: composerHeaders'`
      (173 passed, 2 failed, 175 total).
- [x] T2 — GREEN: guard `SqliteStore.Rows` so a failing query yields an empty
      list instead of throwing.
- [x] T3 — GREEN: isolate each monitor in `RefreshSessions()` so one broken
      monitor cannot abort the loop.
- [x] T4 — Move `fleet.Show()` ahead of the provider/session block so the notch
      never depends on telemetry succeeding.
      Verified dependency order first: everything `Reconcile()`/`MakeController`
      read (`_scope`, `_edge`, `_displayPreference`, `_resetTimeFormat`,
      `_accentColor`, `_visibility`) is already set by the `fleet.Apply(...)`
      calls right above the moved line. The callbacks assigned later
      (`OnRefresh`, `OnRefreshProvider`, `OnUnfold`, `OnOpenSettings`,
      `OnReposition`) are invoked only from `NotchWindowController`'s UI event
      handlers (click/hover/menu), never synchronously during `Show()`/
      `Relocate()`, and each is a closure that reads the field at invocation
      time — so assigning them after `Show()` is safe. `OnQuit` was moved up
      together with `Show()` rather than left for later. No unsafe dependency
      found; the move was made as planned.
- [x] T5 — Make `Log` write to a real file, so a swallowed exception leaves a
      trace in Release builds. Writes to
      `%LOCALAPPDATA%\QuotaArc\quotaarc.log` (reusing `AppBranding.ExeName`),
      guarded by a `lock` plus a try/catch that never lets a logging failure
      escape, with a 1 MB cap that deletes and restarts the file when exceeded.
- [x] T6 — Full suite green; verify the notch appears in a normal (non-demo) run.
      Full suite: 175 passed, 0 failed (173 baseline + 2 new), re-run
      independently by the parent.
      Runtime VERIFIED by the parent: the fixed build was published to a local
      Windows folder and launched with `--quiet` against the user's real
      registry configuration (no demo mode). Three notch windows became visible
      2.1 s after launch, one per display, each carrying
      `exStyle=0x80800A8` (`WS_EX_NOACTIVATE|LAYERED|TOOLWINDOW|TRANSPARENT|TOPMOST`):
      `rect=(767,0)`, `rect=(-1153,0)`, `rect=(2687,0)`, all `386x860`.
      Before the fix the same measurement found zero visible windows after 75 s.
      The new log file was created and written at
      `%LOCALAPPDATA%\QuotaArc\quotaarc.log`.

## Acceptance criteria

- A missing or unreadable Cursor table never prevents the notch from appearing.
- Every exception swallowed by the global handler is recoverable from a log file.
- No regression in the existing test suite.

## Checks

- `dotnet test windows/QuotaArc.sln` (run from Windows)
- Runtime verification: launch the app normally and confirm a visible window
  carrying the notch extended styles.

## Delivery

Strategy: `ask-on-risk`. Forecast: well under 400 authored changed lines, so a
single work-unit commit on `fix/notch-startup-resilience`.

## Progress

Route per task: T1–T5 delegated to one bounded writer (writer trigger: 2+
non-trivial files). T6 verified by the parent.

Files changed (all inside authorized scope):
- `windows/QuotaArc/Providers/SqliteStore.cs:43-68` — `Rows()` wrapped in
  try/catch, returns `[]` on failure. `Scalar()` deliberately left untouched:
  it had no existing error handling to mirror (the task's premise that it
  already did was checked and found false), and guarding it was not
  authorized by T2's scope.
- `windows/QuotaArc/App.xaml.cs:68-77` — `fleet.OnQuit = Shutdown;
  fleet.Show();` moved from the end of `OnStartup` to immediately after the
  `fleet.Apply(...)` calls, before the provider/session block.
- `windows/QuotaArc/App.xaml.cs:307-320` — `RefreshSessions()` now wraps each
  monitor's `read()` call individually, logs the failing monitor's id and
  message via `Log.Error`, and substitutes `[]` for that monitor on failure
  instead of aborting the loop.
- `windows/QuotaArc/App/Log.cs:5-45` — `Log.Info`/`Log.Error` now also append
  a timestamped line to `%LOCALAPPDATA%\QuotaArc\quotaarc.log` (folder name
  from `AppBranding.ExeName`), guarded by a `lock` object and a try/catch that
  never throws; a 1 MB size cap deletes and restarts the file.
- `windows/QuotaArc.Tests/SqliteStoreTests.cs` (new) — RED/GREEN test for
  `SqliteStore.Rows` against a store missing `composerHeaders`.
- `windows/QuotaArc.Tests/CursorActivityMonitorTests.cs:236-269` — RED/GREEN
  test for `CursorActivityMonitor.Read` against the same missing-table case,
  with its own `MakeStoreWithoutComposerHeaders` fixture (deliberately not
  reusing `MakeStore`, which always creates that table).

Real test output:
- RED (before T2): `Con error! - Con error: 2, Superado: 173, Omitido: 0,
  Total: 175` — both new tests failed with
  `Microsoft.Data.Sqlite.SqliteException: SQLite Error 1: 'no such table:
  composerHeaders'`.
- GREEN (final, after T2/T3/T4/T5): `Correctas! - Con error: 0, Superado: 175,
  Omitido: 0, Total: 175`.

Not verified: the notch's actual on-screen appearance in a normal (non-demo)
Windows run. This work was done from a sandboxed Linux/WSL agent with no
access to a Windows GUI session — there is no way to launch `QuotaArc.exe`
and observe a window here. The `dotnet test` run above is real Windows
execution (via PowerShell from WSL), but a running app was not launched.
Runtime verification is the acceptance criterion's other check and remains
open for the user to confirm on their machine.

Status: implementation complete (T1-T6 code changes), all 175 tests green.
Runtime UI verification pending (requires the user's own Windows session).
Not committed per instructions — the user commits.
