# EQ Collector

A single Windows program that reads what the EverQuest server sends -- live while you play, or from a saved capture
file -- and decodes **items** (all 282 fields) and **alternate advancement abilities** (every rank, with titles and
descriptions). No MacroQuest.

Results are written as JSON lines in a folder on your computer. Nothing is sent anywhere.

## Run it

`publish\EQCollector.exe` (one file, about 48 MB, nothing else to install except Npcap for live capture).

* **Capture file** -- pick a `.pcap` / `.pcapng`, set the EverQuest folder (for AA titles and descriptions), press
  *Process*. A 258 MB capture takes about 10 seconds. No Npcap or administrator needed.
* **Live capture** -- needs [Npcap](https://npcap.com) installed and EQ Collector running as administrator (it offers
  to restart itself). Pick your adapter (the one with the default gateway is chosen), press *Start*, then play: zone,
  look at / loot / shop for items, and buy or reset AAs (the server sends the AA table then).
* **From a script** -- `EQCollector.exe --file capture.pcapng --out folder --eq "D:\EQ test"`

Output (in the chosen folder): `items.jsonl` (one distinct item per line, written as found) and `aa.jsonl` (one AA
rank per line; written when you stop, press *Save AAs now*, or the file finishes).

## Build

    dotnet build
    dotnet test                                   # unit tests + the real-capture test
    set EQC_FULL=1 & dotnet test                  # also the whole-capture AA test
    dotnet publish src/EQCollector.App -c Release -r win-x64 --self-contained true ^
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish

* `src/EQCollector.Core` -- session reader (`EQStreamReader`), pcap/pcapng reader, item and AA decoders, engine.
* `src/EQCollector.App` -- the window, live capture (SharpPcap / Npcap), command-line mode.
* `tests/EQCollector.Tests` -- synthetic session-packet tests, plus a test against a real capture whose items were
  confirmed in game (skipped quietly when the capture file is not on the machine).

## Credit

The record layouts for items and AAs, the session-layer details and the list of well-known AAs used to find the AA
packet type come from **kauffman12's [eqaainfo](https://github.com/kauffman12/eqaainfo)**, used with the author's
permission and credited as they asked. The code here is our own rewrite. Also credited on the site's Credits page.

## What has and has not been checked

* Checked on the real capture `2.pcapng`: 1,951 distinct items (1,937 of the first 1,950 are in our items table and
  nearly every field agrees, 99.4-100%); 781 AA ranks, identical to the earlier Python decoder's output; the four items
  confirmed against in-game tooltips; synthetic tests for handshake, checksums, compression, fragments and bundles.
* **Live capture has not been run against real traffic yet** -- it needs an elevated run while the game is open.
* The packet layouts change when the game patches. When nothing decodes, the window will show zero items/AAs.
