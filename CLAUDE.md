# Project: NT8 NinjaScript

This repo holds NinjaTrader 8 NinjaScript source files (`.cs`).

## Distribution rule

`.cs` source files in this repo are NOT importable via **Tools → Import →
NinjaScript Add-On…**. That menu only accepts archives produced by NT8's own
**Export** feature, which embed an NT-specific manifest. A plain zip — even
one containing the correct `.cs` — will fail with "from an older version of
NT8 or not an archive file."

Correct install path for the user:

1. Save the `.cs` to `Documents\NinjaTrader 8\bin\Custom\Strategies\<File>.cs`
   (or `…\Custom\Indicators\<File>.cs` for indicators).
2. Open **NinjaScript Editor** in NT8 and press **F5** to compile.

Therefore: do **not** auto-zip `.cs` files. Commit the source only. If a
genuine NT8 export package is ever needed, it has to be produced from inside
NT8 via Tools → Export.
