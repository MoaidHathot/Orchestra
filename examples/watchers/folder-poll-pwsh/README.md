# Watcher example: poll a folder with a PowerShell fetcher

A **watcher** is a poller Orchestra runs in-process: on a schedule it runs your *fetcher*
(here `fetch.ps1`), reads the JSON items it prints, deduplicates them per subscribing
orchestration and launches one run per genuinely new item. The orchestration's
`trigger: { type: watcher }` block *is* the subscription.

Files in this folder:

| File | Role |
|---|---|
| `orchestra.watchers.json` | The watcher library: declares `local-folder-pwsh` (poll every 5 s, presence dedup keyed on `item.path`). |
| `fetch.ps1` | The fetcher. Reads the protocol payload from stdin, lists `*.txt` files in `config.folder`, prints `{ protocolVersion, items, cursor }`. |
| `handle-new-file.yaml` | The subscriber: `trigger.watcher: local-folder-pwsh`, maps `item.path` / `item.name` to its inputs. |

## Try the fetcher without a host

```powershell
orchestra watcher test --watcher local-folder-pwsh --config-path .\orchestra.watchers.json
```

The report shows the exact stdin payload, the stdout received, schema validation, the
extracted items and the cursor. Simulate dedup with `--subscribers handle-new-file-pwsh`
and carry state between runs with `--save-state` / `--from-state`.

## Run it end to end

1. Put this folder's `orchestra.watchers.json` beside the `orchestra.json` the host uses
   (`orchestra config show` tells you which one), or copy the `local-folder-pwsh` entry into
   your existing library. Relative paths in the entry (`./fetch.ps1`, `./inbox`) resolve
   against the library file's folder.
2. Register `handle-new-file.yaml` (drop it in your scan directory or `orchestra register`).
3. Start the host (`orchestra portal`). The watcher starts as soon as the orchestration's
   trigger is registered - a watcher with no subscribers never runs.
4. Create `inbox/` and drop a `hello.txt` into it. Within one tick the orchestration fires
   once with `path` / `name` filled in. The next tick does **not** fire again for the same
   file: it is now in the subscriber's seen-set.

State lives in `.orchestra/watchers/` beside the `orchestra.json` in effect
(`local-folder-pwsh.state.json` plus one `local-folder-pwsh--<orchestration-id>.state.json`
per subscriber). Delete those files to reset.

## Notes on dedup

* This example uses `mode: presence`: a key is remembered while the file is visible and
  forgotten after it has been missing for `graceTicks` (default 3) ticks, so a file that
  is deleted and re-created fires again. Bounded by the working set of the folder.
* The default `mode: fifo` remembers the last `maxEntries` (10 000) keys for `ttlDays`
  (14). A fetcher that keeps returning the *entire* history every tick will eventually
  trim keys it still receives and re-dispatch them; Orchestra logs a
  `FIFO trim rate` warning when that happens. Prefer a cursor-aware fetcher (return only
  what is new since `cursor`) or `presence` mode for sources like this one.
* With no `dedup.key`, the key is a hash of the whole item - fine for immutable items, wrong
  for items with mutable fields such as timestamps.
