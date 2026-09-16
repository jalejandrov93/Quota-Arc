# WSL remote mode

Quota Arc for Windows normally reads each provider's credentials from the
Windows side. If your coding assistants live inside WSL instead, remote mode
lets the notch read their quotas from [CodexBar](https://github.com/steipete/CodexBar)
running in WSL.

## Overview

CodexBar's CLI does the provider work inside WSL and serves the results over
HTTP on the loopback interface. WSL forwards loopback traffic to Windows, so
Quota Arc asks `http://127.0.0.1:8787` for usage and draws the rings as usual.

> **Use `127.0.0.1`, not `localhost`.** Windows resolves `localhost` to the IPv6
> address `::1` first, and `codexbar serve` listens on IPv4 only. A request to
> `http://127.0.0.1:8787` does not fall back: it hangs until it times out. On a
> machine with mirrored networking, `http://127.0.0.1:8787/health` answered in
> about 100 ms while `http://localhost:8787/health` failed after 8 seconds.
> Quota Arc's default URL is `http://127.0.0.1:8787` for this reason.

```
+------------------------------+          +------------------------------+
| WSL                          |          | Windows                      |
|                              |   HTTP   |                              |
|  codexbar serve              | -------> |  Quota Arc notch             |
|  127.0.0.1:8787              | 127.0.0.1|  QUOTAARC_CODEXBAR_URL or    |
|  (systemd unit: codexbar)    |   :8787  |  HKCU\Software\QuotaArc      |
+------------------------------+          +------------------------------+
```

Quota Arc only asks for the providers enabled in CodexBar's `config.json`.
`GET /usage?provider=all` probes around 69 providers and takes more than a
minute, so remote mode never uses it.

## Quick start

The shortest path from a fresh setup to rings on the notch. Each step is
covered in detail further down.

1. **Install CodexBar in WSL.** From this repository, inside WSL:

   ```sh
   ./wsl/install.sh
   ```

   It downloads a checksummed CodexBar release into `~/.local/bin` and never
   calls `sudo` itself. It prints the `sudo` commands that install the systemd
   unit; run them to start CodexBar automatically with the distro.

2. **Or start CodexBar by hand**, without the unit:

   ```sh
   ./wsl/install.sh --no-service
   mkdir -p ~/.local/state/codexbar
   nohup ~/.local/bin/codexbar-serve > ~/.local/state/codexbar/serve.log 2>&1 &
   ```

   A server started this way stops when WSL shuts down, so rings go stale after
   a reboot. Use the systemd unit to keep it running.

3. **Check it from Windows**, in PowerShell:

   ```powershell
   curl.exe http://127.0.0.1:8787/health
   ```

   It answers `{"status":"ok",...}`. Always use `127.0.0.1`: `localhost` hangs.

4. **Turn on remote mode** and start Quota Arc from a new session:

   ```powershell
   setx QUOTAARC_CODEXBAR_URL http://127.0.0.1:8787
   ```

5. **Optional: show Agent Hub activity.** If you run
   [agent-hub](https://github.com/jalejandrov93/agent-hub) in WSL, start its
   dashboard service there and check it from Windows. Quota Arc picks it up with
   no configuration. See [Agent Hub cell](#agent-hub-cell).

   ```sh
   systemctl --user enable --now agent-hub-dashboard
   ```

   ```powershell
   curl.exe http://127.0.0.1:7777/api/state
   ```

## Requirements

- Windows 10 or 11 with WSL 2 and a glibc-based distro (Ubuntu, Debian, ...).
  Alpine and other musl distros work with `CODEXBAR_LIBC=musl`.
- systemd enabled in the distro (`/etc/wsl.conf` contains `[boot]` and `systemd=true`).
- WSL localhost forwarding, which is on by default (NAT mode with
  `localhostForwarding`, or mirrored networking).
- `curl` or `wget`, `tar` and `sha256sum` in WSL. `openssl` is optional.
- The coding assistants you want to track, installed and signed in inside WSL.
- Quota Arc for Windows built from this repository. See [`windows/README.md`](../windows/README.md).

## 1. Install the server in WSL

From the repository root, inside WSL:

```sh
./wsl/install.sh
```

The script is safe to re-run. It:

- downloads the CodexBar CLI release for your architecture, verifies its
  SHA-256 checksum, installs it into `~/.local/share/codexbar-cli/v<version>/`
  and links `~/.local/bin/codexbar`;
- installs the `~/.local/bin/codexbar-serve` wrapper;
- creates `~/.config/codexbar/config.json` from [`wsl/config.example.json`](../wsl/config.example.json) if it does not exist;
- creates the dashboard token `~/.config/codexbar/dashboard-token` (mode 600) if it does not exist;
- renders `~/.config/codexbar/codexbar.service` and prints the commands to install it.

Install and start the system unit:

```sh
sudo install -m 644 ~/.config/codexbar/codexbar.service /etc/systemd/system/codexbar.service && sudo systemctl daemon-reload && sudo systemctl enable --now codexbar
```

Pass `--no-service` to skip the unit and run `codexbar-serve` by hand instead.

Verify from WSL:

```sh
curl http://127.0.0.1:8787/health
```

Verify from Windows PowerShell (`curl.exe`, not the `curl` alias):

```powershell
curl.exe http://127.0.0.1:8787/health
```

## 2. Sign in to providers inside WSL

CodexBar reuses the sessions the tools already hold. Sign in once inside WSL,
as the same user the service runs as.

| Provider id | Source in `config.json` | Sign in | Notes |
|---|---|---|---|
| `claude` | `oauth` | `claude` (Claude Code login) | On Linux use `"source": "oauth"`; `auto` times out. |
| `codex` | `auto` | `codex login` | A 401 means `~/.codex/auth.json` is stale; run `codex login` again. |
| `antigravity` | `auto` | Sign in to Antigravity (`agy`) | Google no longer supports Gemini CLI OAuth, so use `antigravity` instead of `gemini`. |
| `opencodego` | `auto` | `opencode` sign-in | Reads OpenCode's local database. The OpenCode web source is macOS-only. |
| `copilot` | `api` | `gh auth login` | Needs `COPILOT_API_TOKEN`; `codexbar-serve` takes it from `gh auth token` when unset. |

After signing in to a provider, restart the service so it picks up the new
session:

```sh
sudo systemctl restart codexbar
```

## 3. Turn on remote mode in Quota Arc

Remote mode is on when either of these is set. Restart Quota Arc after
changing them; the settings are read at launch.

**Environment variable** (turns remote mode on and sets the URL):

```powershell
setx QUOTAARC_CODEXBAR_URL http://127.0.0.1:8787
```

`setx` applies to processes started afterwards, so sign out or start Quota Arc
from a new session.

**Registry** under `HKCU\Software\QuotaArc`. Values are strings (`REG_SZ`):

| Value | Meaning | Default |
|---|---|---|
| `codexbarEnabled` | `1` turns remote mode on | off |
| `codexbarUrl` | CodexBar server URL | `http://127.0.0.1:8787` |
| `codexbarProviders` | Comma-separated provider ids to show | `claude,codex,antigravity,opencodego,copilot` |

```powershell
$key = "HKCU:\Software\QuotaArc"
if (-not (Test-Path $key)) { New-Item -Path $key | Out-Null }
New-ItemProperty -Path $key -Name codexbarEnabled -Value "1" -PropertyType String -Force | Out-Null
Set-ItemProperty -Path $key -Name codexbarUrl -Value "http://127.0.0.1:8787"
Set-ItemProperty -Path $key -Name codexbarProviders -Value "claude,codex,antigravity,opencodego,copilot"
```

To turn remote mode off, remove the environment variable
(`[Environment]::SetEnvironmentVariable("QUOTAARC_CODEXBAR_URL", $null, "User")`)
and set `codexbarEnabled` to `0`.

Individual rings can be hidden or shown again from the app's Settings window.

## 4. CodexBar web dashboard and token

CodexBar also serves a dashboard at `http://127.0.0.1:8787/`. The page itself
always loads, but its data requires the bearer token from
`CODEXBAR_DASHBOARD_TOKEN`; without a configured token the data request answers
401. `codexbar-serve` reads the token from `~/.config/codexbar/dashboard-token`.

Show the token in WSL and paste it into the dashboard's prompt in the browser:

```sh
cat ~/.config/codexbar/dashboard-token
```

To rotate it:

```sh
rm ~/.config/codexbar/dashboard-token
./wsl/install.sh --no-service
sudo systemctl restart codexbar
```

Quota Arc itself does not use the token; it reads `/usage` on loopback.

## Choosing providers

Edit `~/.config/codexbar/config.json` in WSL: set `"enabled": false` or add a
provider entry, then run `sudo systemctl restart codexbar`. Keep
`codexbarProviders` in Quota Arc matching the enabled ids, so the notch does
not ask for providers the server does not serve.

## Updating CodexBar

```sh
CODEXBAR_VERSION=<version> ./wsl/install.sh
sudo systemctl restart codexbar
```

Each version installs into its own folder and `~/.local/bin/codexbar` is
re-linked, so the previous version stays available until you delete its folder
under `~/.local/share/codexbar-cli/`.

## Agent Hub cell

When an [agent-hub](https://github.com/jalejandrov93/agent-hub) dashboard is
reachable at `http://127.0.0.1:7777`, the Windows notch adds an **Agent Hub**
cell next to the quota rings, in local mode and in remote mode alike. It reads
`/api/state` and `/api/config`, and follows `/events` so it updates as jobs
start and finish.

- **Jobs.** The cell shows how many coding-agent jobs are running and queued.
  Agent Hub states no concurrency ceiling, so this is a count, never a ring —
  drawing a fraction would require inventing a limit.
- **Blocks.** Any open circuit breaker, or a pair a human put on hold, shows as a
  block on the cell. An override that only resets a breaker does not.
- **Health.** An agent that is degraded or unavailable changes the cell status.
- **Fidelity.** Every reading is marked derived: these are Agent Hub's own
  measurements of other tools, not a vendor's usage API.

When the dashboard is not running, the cell says so calmly instead of reporting
an error. As with CodexBar, use `127.0.0.1`; `localhost` hangs.

## Troubleshooting

- **Nothing answers.** Run `curl http://127.0.0.1:8787/health` in WSL, then
  `curl.exe http://127.0.0.1:8787/health` in PowerShell. If WSL answers and
  Windows does not, check WSL localhost forwarding.
- **Windows hangs on `localhost` but answers on `127.0.0.1`.** Windows resolves
  `localhost` to `::1` first and CodexBar listens on IPv4 only. Point
  `QUOTAARC_CODEXBAR_URL` or `codexbarUrl` at `http://127.0.0.1:8787`. The same
  applies to any other WSL server Quota Arc reads, such as Agent Hub on port 7777.
- **Service logs.** `systemctl status codexbar` and `journalctl -u codexbar -e`.
- **`systemctl --user` fails with "Failed to connect to bus".** Under WSLg,
  `/run/user/<uid>` is shadowed by a WSLg tmpfs, so the user bus is unreachable.
  Use the system unit with `User=` that `install.sh` renders.
- **A ring is missing.** It may be hidden in Settings, not listed in
  `codexbarProviders`, or disabled in `config.json`.
- **A ring says it needs sign-in (NeedsAuth).** CodexBar reported an auth error
  for that provider. Sign in again inside WSL (see step 2) and restart the
  service. For Codex, run `codex login`.
- **Rings go stale after a while.** WSL can shut the VM down when it is idle.
  Opening any WSL shell starts the distro again, and systemd starts `codexbar`
  with it. On Windows 11, `vmIdleTimeout` under `[wsl2]` in `%UserProfile%\.wslconfig`
  controls the idle timeout.
- **Port 8787 is taken.** Choose another port for the service and point Quota
  Arc at it:

  ```sh
  sudo systemctl edit codexbar
  # [Service]
  # Environment=CODEXBAR_PORT=8788
  sudo systemctl restart codexbar
  ```

  ```powershell
  Set-ItemProperty -Path "HKCU:\Software\QuotaArc" -Name codexbarUrl -Value "http://127.0.0.1:8788"
  ```

  If `QUOTAARC_CODEXBAR_URL` is set, update it too; it takes precedence over `codexbarUrl`.

## Security notes

- `codexbar serve` binds to `127.0.0.1` only. Windows reaches it through WSL
  localhost forwarding; it is not exposed on the network.
- `/usage` returns account data. On loopback it is not token-gated, so any
  local process on the machine can read it.
- The dashboard token lives in `~/.config/codexbar/dashboard-token` with mode
  600, and `codexbar-serve` passes it through the environment, not argv, so it
  does not show up in `ps`.
- Do not bind to `0.0.0.0` or another non-loopback host unless you understand
  CodexBar's threat model: transport is plain HTTP, so the token and account
  data cross the network in cleartext, and a non-loopback host requires both a
  token and `--allow-plain-http`. Use a TLS-terminating reverse proxy for
  anything beyond a trusted network segment.

## Credits and licenses

- [CodexBar](https://github.com/steipete/CodexBar): MIT License, © Peter Steinberger.
  `wsl/install.sh` downloads its release binaries; they are not redistributed in this repository.
- [Quota Arc](https://github.com/vaiibhavkale/Quota-Arc): MIT License, © Vaibhav Kale.
- [Codenotch](https://github.com/vinzdg/codenotch): MIT License.
