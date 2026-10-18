<p align="center"><img src="docs/icon.png" width="96" alt="EQ Collector"></p>

# EQ Collector

A single Windows program that reads what the EverQuest server sends -- live while you play, or from a saved capture
file -- and decodes **items** (all 282 fields) and **alternate advancement abilities** (every rank the server sends, with
titles and descriptions). No MacroQuest, no game files modified, and it runs as a normal user.

It can also share what it finds with [Norrath Codex](https://norrathcodex.com): it keeps a copy of the list of what the
site already has and uploads only what is new, into the site's review queue. Off by default -- see
[Website upload](#website-upload).

* **Version 0.4.0** -- built for .NET 10; the published `EQCollector.exe` is one self-contained file (about 52 MB).
* Tested on real EverQuest traffic, live and from saved captures (see [What has been checked](#what-has-been-checked)).

## Run it

Use the newest `publish-x.y.z\EQCollector.exe` (the window title shows the version). Nothing else to install except
[Npcap](https://npcap.com) for live capture.

* **Live capture** -- install [Npcap](https://npcap.com) with its default options, pick your adapter (the one with the
  default gateway is chosen), press *Start*, then play: zone, look at / loot / shop for items, buy or reset AAs (the
  server sends the AA table then). A session is recognised when it is set up, so zone or log in after starting.
  **No administrator rights are needed** as long as Npcap was installed with *"Restrict Npcap driver's access to
  Administrators only"* left **unticked** (the default). If Windows refuses to open the adapter, EQ Collector says so and
  offers to restart itself as administrator.
* **Capture file** -- pick a `.pcap` / `.pcapng`, set the EverQuest folder (for AA titles and descriptions), press
  *Process*. A 258 MB capture takes about 10 seconds. No Npcap needed. A damaged or cut-off file is reported as such.
* **From a script** -- `EQCollector.exe --file capture.pcapng --out folder --eq "C:\path\to\EverQuest"`

## What it writes

In the chosen results folder (default `Documents\EQCollector`):

| File | What |
| --- | --- |
| `items.jsonl` | one distinct item per line, all 282 fields, written as found; items already in the file are not written again |
| `aa.jsonl` | one AA rank per line; rewritten every couple of seconds while ranks arrive in live capture, and merged into what the file already holds, so a short run never shrinks it |
| `capture-*.pcap` | the raw traffic of a live session (switch off with the checkbox; stops by itself at 2 GB) |
| `upload-state.json`, `site-manifest.json` | what has been sent to the website and the copy of its list (only when uploading) |
| `aa.partial*.jsonl`, `aa.jsonl.damaged-*` | safety copies if `aa.jsonl` could not be read or held a damaged line; merged back automatically |

Settings are kept in `%AppData%\EQCollector\settings.json`; the upload token is stored encrypted for your Windows user (DPAPI).

## Website upload

Optional. Tick *Upload new items and AA ranks to the website while capturing*, enter the website (default
`https://norrathcodex.com`) and your upload token (the site's *capture-token* page, the same token the MacroQuest tools
use), press *Test*.

* The app **pulls the website's list of what it has** (every item id and AA rank, about 90 KB, kept in
  `site-manifest.json` and refreshed every 30 minutes), so it knows on your PC what is new. While you capture, about every
  20 seconds it sends only what that list says the site lacks -- after confirming with the site, in case someone else
  uploaded it meanwhile. AA ranks the site already has, with their text, are not sent.
* Everything you upload goes into the website's **review queue** and is added only after a person approves it, whoever's
  token it is.
* Nothing existing is changed: items the site already has are skipped.
* What was sent is remembered, so nothing is sent twice (to send everything again, delete `upload-state.json`). If the
  website or your connection is down, items stay queued and are retried with a growing pause; a refused token stops the
  upload with one message. Capturing is never affected.
* The token is only ever sent over **https** (plain http is accepted for this computer only; a test site on a private
  network address needs the environment variable `EQC_ALLOW_INSECURE_LAN=1`). No character names, chat, addresses or raw
  captures are sent, and a crafter's name on an item is dropped.
* *Upload saved files now* sends what a results folder already holds; a capture-file run uploads when it finishes.
* From a script: `EQCollector.exe --upload-folder folder [--server https://...]`, or add `--upload` to a `--file` run;
  the token comes from the `EQC_TOKEN` environment variable or what the window saved.

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The published exe carries its own runtime, so
users need nothing installed.

    dotnet build
    dotnet test                                   # unit tests (the real-capture tests skip themselves)
    set EQC_CAPTURE=C:\path\to\capture.pcapng
    set EQC_STRINGS=C:\path\to\EverQuest          # optional: titles and descriptions
    dotnet test                                   # now also decodes the capture; the confirmed items are the author's own
    set EQC_FULL=1 & dotnet test                  # also the whole-capture AA test (slow)
    dotnet publish src/EQCollector.App -c Release -r win-x64 --self-contained true ^
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish

* `src/EQCollector.Core` -- session reader (`EQStreamReader`), pcap/pcapng reader, item and AA decoders, engine, website
  uploader.
* `src/EQCollector.App` -- the window, live capture (SharpPcap / Npcap), command-line mode, settings.
* `tests/EQCollector.Tests` -- 99 tests: synthetic session packets, decoders, robustness against hostile input, the
  uploader against a fake website, and a test against a real capture whose items were confirmed in game (skipped quietly
  when the capture file is not on the machine).

## Credit

The record layouts for items and AAs, the session-layer details and the list of well-known AAs used to find the AA
packet type come from **kauffman12's [eqaainfo](https://github.com/kauffman12/eqaainfo)**, 
The code here is our own rewrite. Also credited on the site's Credits page.

## What has been checked

* **Real capture files:** 1,951 distinct items from `2.pcapng` (1,937 of the first 1,950 are in the site's items table
  and nearly every field agrees, 99.4-100%); 781 AA ranks, identical to the earlier Python decoder's output; four items
  confirmed against in-game tooltips; synthetic tests for handshake, checksums, compression, fragments and bundles.
* **Live capture:** run against real traffic as a normal (non-administrator) user -- items and AA ranks decoded while
  playing, with the AA packet type detected automatically.
* **Reviews:** the whole app has been reviewed by Codex and CodeRabbit; the findings (untrusted-input limits, file
  safety, upload safety) are fixed and covered by tests.
* The packet layouts change when the game patches. When nothing decodes, the window shows zero items/AAs and the log says
  no game session was seen.
