# EQ Collector

A single Windows program that reads what the EverQuest server sends -- live while you play, or from a saved capture
file -- and decodes **items** (all 282 fields) and **alternate advancement abilities** (every rank, with titles and
descriptions). No MacroQuest.

Results are written as JSON lines in a folder on your computer. Nothing is sent anywhere unless you turn on the
[website upload](#website-upload).

## Run it

The newest `publish-x.y.z\EQCollector.exe` (for example `publish-0.3.4\EQCollector.exe`; one file, about 48 MB, nothing else to
install except Npcap for live capture). The window title shows the version.

* **Capture file** -- pick a `.pcap` / `.pcapng`, set the EverQuest folder (for AA titles and descriptions), press
  *Process*. A 258 MB capture takes about 10 seconds. No Npcap or administrator needed.
* **Live capture** -- needs [Npcap](https://npcap.com) installed; no administrator rights are needed as long as Npcap was
  installed with *"Restrict Npcap driver's access to Administrators only"* left **unticked** (the default). If Windows
  refuses to open the adapter, EQ Collector says so and offers to restart itself as administrator. Pick your adapter (the one with the default gateway is chosen), press *Start*, then play: zone,
  look at / loot / shop for items, and buy or reset AAs (the server sends the AA table then).
* **From a script** -- `EQCollector.exe --file capture.pcapng --out folder --eq "C:\path\to\EverQuest"`

Output (in the chosen folder): `items.jsonl` (one distinct item per line, written as found) and `aa.jsonl` (one AA
rank per line; in live capture it is rewritten every couple of seconds while ranks arrive, and also when you stop or press
*Save AAs now*; a capture file writes it when it finishes. It is merged into what the file already holds, so a short run
never shrinks it).

## Website upload

Optional. Tick *Upload new items and AA ranks to the website while capturing*, enter the website (default
`https://norrathcodex.com`) and your upload token (the site's *capture-token* page, same token the MacroQuest tools use),
press *Test*. The app **pulls the website's list of what it has** (every item id and AA rank, about 90 KB, kept in
`site-manifest.json` in the results folder and refreshed every 30 minutes) so it knows on this PC what is new. While you
capture, about every 20 seconds (longer while the site is unreachable) it sends only what that list says the site lacks (it still confirms with the site just before
sending, in case someone else uploaded it meanwhile). If the list cannot be pulled it asks the site about each item
instead. AA ranks the site already has, with their text, are not sent.

* Everything you upload goes into the website's **review queue** and is added only after a person approves it, whoever's
  token it is (the website's `review_everything` setting; if it is switched off, a moderator/admin token writes directly).
* Nothing existing is changed: items the site already has are skipped.
* What was sent is remembered in `upload-state.json` in the results folder, so nothing is sent twice (to send everything
  again, e.g. after the website was restored from a backup, delete that file). If the website
  or your connection is down, items stay queued and are retried with a growing pause; a refused token stops the upload
  with one message. Capturing is never affected.
* The token is stored encrypted for your Windows user (DPAPI) and sent only over https (only localhost or a numeric private-network
  address like 192.168.x.x may use http, for a test site; a name such as .lan is not trusted). No character names, chat or anything else is sent; a crafter's name on an item
  is dropped.
* *Upload saved files now* sends what a results folder already holds; a capture-file run uploads when it finishes.
* From a script: `EQCollector.exe --upload-folder folder [--server https://...]` or add `--upload` to a `--file` run; the
  token comes from the `EQC_TOKEN` environment variable or what the window saved.

## Build

    dotnet build
    dotnet test                                   # unit tests (the real-capture tests skip themselves)
    set EQC_CAPTURE=C:\path\to\capture.pcapng
    set EQC_STRINGS=C:\path\to\EverQuest          # optional: titles and descriptions
    dotnet test                                   # now also decodes the capture; the confirmed items are the author's own
    set EQC_FULL=1 & dotnet test                  # also the whole-capture AA test (slow)
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
