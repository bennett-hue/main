# Project: NT8 NinjaScript

This repo holds NinjaTrader 8 NinjaScript source files (`.cs`).

## Packaging rule (always)

Whenever you create or modify a `.cs` file in this repo, also produce a zip
containing that `.cs` file alongside it, named `<BaseName>.zip` (same base name
as the source). NT8 expects `.cs` uploads to be wrapped in a zip — without it
the user can't import the file. Commit the zip together with the source.

Example: editing `BK_PivotReversal.cs` → also write `BK_PivotReversal.zip`
containing the updated `.cs`, then commit both.
