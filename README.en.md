# N_m3u8DL-RE (download-queue build)

A fork of [nilaoda/N_m3u8DL-RE](https://github.com/nilaoda/N_m3u8DL-RE) that adds a **download queue** on top of the original CLI.

Upstream features (HLS / DASH / MSS, multi-threaded segment download, AES-128 / CENC decryption, FFmpeg muxing,
live recording, utility commands) are untouched. This fork only answers one problem: clicking a dozen links at once
used to spawn a dozen processes and choke the machine.

## What's new

- **Queue enabled by default, 2 downloads at a time** (`--queue-concurrency`, 1-8). The first two invocations start
  immediately, the rest enqueue and exit, then start automatically when a slot frees up.
- **Queue audit log** `Logs/下载队列.log` — every enqueue / start / finish is appended with a complete, copy-pasteable
  command line, so a hung task can always be re-run even after the window and the browser tab are gone.
- **`queue` command** — list pending tasks, print the audit log, clear the queue, or start consuming it now.
- **Crash-safe**: each task runs in its own child process, slots are OS-released lock files, and unfinished tasks are
  requeued on the next start without stealing jobs that a living worker still owns.

## Options

| Option | Default | Description |
| --- | --- | --- |
| `--queue <true\|false>` | `true` | Enable/disable the queue |
| `--queue-concurrency <N>` | `2` | Simultaneous downloads (1-8) |
| `--queue-dir <DIR>` | `%APPDATA%\N_m3u8DL-RE\queue` | Queue directory |
| `--queue-delay <sec>` | `1` | Cooldown between two tasks of the same worker |

```text
N_m3u8DL-RE queue          # list pending tasks
N_m3u8DL-RE queue --log    # last 60 lines of the audit log
N_m3u8DL-RE queue --clear  # drop everything
N_m3u8DL-RE queue --run    # start processing now
```

## Build

Requires the .NET 10 SDK and ffmpeg (in PATH, next to the exe, or via `--ffmpeg-binary-path`).

```bash
dotnet build src/N_m3u8DL-RE.sln -c Release
dotnet publish src/N_m3u8DL-RE/N_m3u8DL-RE.csproj -c Release -r win-x64 -o out   # AOT single file
```

Prebuilt `N_m3u8DL-RE.exe` (win-x64, self-contained) is attached to every release.

## Notes

- Unit-test projects from upstream are intentionally not included; only the three runtime projects ship here.
- Queue code lives in `src/N_m3u8DL-RE/Queue/`.
- A live recording holds a slot until Ctrl+C.
- MIT licensed, same as upstream.
