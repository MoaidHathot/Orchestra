# Watcher example: poll a folder with a .NET single-file fetcher

Same source and behaviour as [`../folder-poll-pwsh`](../folder-poll-pwsh/README.md), but the
fetcher is a .NET 10 file-based program run with `dotnet run fetch.cs`, declared as a
`poll-command` watcher instead of `poll-script`. Use the two side by side to compare the
runtime conventions: both read the protocol payload from stdin and print
`{ protocolVersion, items, cursor }` to stdout.

| File | Role |
|---|---|
| `orchestra.watchers.json` | Declares `local-folder-dotnet`: `command: dotnet`, `arguments: ["run", "fetch.cs"]`, presence dedup on `item.path`. |
| `fetch.cs` | The fetcher. Lists `*.txt` files in `config.folder`. |
| `handle-new-file.yaml` | The subscriber (`trigger.watcher: local-folder-dotnet`). |

Try it without a host:

```powershell
orchestra watcher test --watcher local-folder-dotnet --config-path .\orchestra.watchers.json
```

`dotnet run fetch.cs` compiles on first use, so the first tick takes a few seconds; the
watcher's `timeoutSeconds: 60` leaves room for that. For a hot path, publish the fetcher to
an executable and point `command` at it instead.

The end-to-end walkthrough (register the library and the orchestration, drop a file into
`inbox/`, watch it fire once and not re-fire) is identical to the PowerShell example.
