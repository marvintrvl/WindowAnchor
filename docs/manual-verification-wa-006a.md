# WA-006A Manual Verification — Checkpoint and Undo

Use a disposable Windows test session. Exact Switch sends normal close requests to windows outside
the target workspace; save all real work and use only test applications such as separate Notepad
windows. Do not use this procedure with unsaved documents.

## Setup

1. Start two Notepad windows named **Baseline A** and **Baseline B** with distinct, saved test files.
   Put them at visibly different locations and record their positions and show states.
2. In WindowAnchor Settings, enable **Checkpoint routine restores**. Save a workspace named **WA-006A
   Baseline** containing both windows.
3. Move **Baseline A**, close **Baseline B**, and open a third disposable Notepad window named
   **Introduced**. Save a second workspace named **WA-006A Target** containing only the moved A.

## Verify the transaction

1. Restore **WA-006A Baseline** in Resume mode. Confirm the diagnostic result reports a created
   `Restore` checkpoint before any placement action and that both baseline windows return to their
   saved geometry/show state.
2. Run **Exact Switch** for **WA-006A Target**. Confirm the preview identifies the windows outside
   the target; after approval, they receive only normal close requests. Confirm the diagnostic
   result reports a created `WorkspaceSwitch` checkpoint before the close stage.
3. From the tray, run **Undo Last Restore**. Confirm the baseline windows return to their recorded
   geometry/show state and **Introduced** is reconciled by a normal close request. Confirm the
   diagnostic result reports a created `Undo` checkpoint before its close stage.
4. Run **Undo Last Restore** again. Confirm the desktop returns to the Target arrangement, proving
   that the first Undo captured the state it replaced.

## Record the result

For each operation, use **Copy Last Restore Diagnostics** and record the redacted report's status,
checkpoint trigger/status, and any entries needing attention. A failed checkpoint must leave the
desktop unchanged and must not remove the earlier healthy checkpoint. Do not put screenshots or
unredacted workspace data in checkpoint storage.

Mark WA-006A complete only when all four operations succeed on a real Windows desktop or when each
exception is recorded with its redacted diagnostics and a follow-up defect.
