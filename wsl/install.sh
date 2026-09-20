#!/usr/bin/env bash
# Installs the CodexBar CLI and its HTTP server wrapper inside WSL so the
# Windows Quota Arc app can read quotas in remote mode.
#
# Idempotent: re-running keeps an existing download, config and dashboard
# token. It never calls sudo; the system unit step prints the commands to run.
set -euo pipefail

readonly DEFAULT_VERSION="0.62.0"
readonly RELEASES_URL="https://github.com/steipete/CodexBar/releases/download"

usage() {
  cat <<'EOF'
Usage: wsl/install.sh [--no-service] [--help]

Installs the CodexBar CLI (https://github.com/steipete/CodexBar) for the
current user and prepares the server that Quota Arc reads in remote mode.

Steps:
  1. Download and verify CodexBarCLI into ~/.local/share/codexbar-cli/v<version>/
     and link ~/.local/bin/codexbar to it (skipped when already present).
  2. Install ~/.local/bin/codexbar-serve.
  3. Create ~/.config/codexbar/config.json from wsl/config.example.json (if absent).
  4. Create ~/.config/codexbar/dashboard-token (if absent or empty).
  5. Render ~/.config/codexbar/codexbar.service and print the sudo commands
     that install it as a system unit (skipped with --no-service).

Options:
  --no-service   Do not render the systemd unit.
  -h, --help     Show this help and exit.

Environment:
  CODEXBAR_VERSION   CodexBar release to install (default: 0.62.0).
  CODEXBAR_LIBC      glibc (default) or musl.
EOF
}

log() { printf '==> %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

render_service=1
while (($# > 0)); do
  case "$1" in
    --no-service) render_service=0 ;;
    -h | --help) usage; exit 0 ;;
    *) usage >&2; die "unknown argument: $1" ;;
  esac
  shift
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
version="${CODEXBAR_VERSION:-$DEFAULT_VERSION}"
version="${version#v}"
libc="${CODEXBAR_LIBC:-glibc}"

case "$(uname -m)" in
  x86_64 | amd64) arch="x86_64" ;;
  aarch64 | arm64) arch="aarch64" ;;
  *) die "unsupported architecture: $(uname -m) (supported: x86_64, aarch64)" ;;
esac

case "$libc" in
  glibc) platform="linux-${arch}" ;;
  musl) platform="linux-musl-${arch}" ;;
  *) die "CODEXBAR_LIBC must be 'glibc' or 'musl', got: $libc" ;;
esac

bin_dir="$HOME/.local/bin"
share_dir="$HOME/.local/share/codexbar-cli"
install_dir="$share_dir/v${version}"
config_dir="$HOME/.config/codexbar"

download() {
  local url="$1" out="$2"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL --retry 3 -o "$out" "$url"
  elif command -v wget >/dev/null 2>&1; then
    wget -q -O "$out" "$url"
  else
    die "curl or wget is required to download CodexBar"
  fi
}

install_cli() {
  if [[ -x "$install_dir/CodexBarCLI" ]]; then
    log "CodexBar v${version} already installed in $install_dir"
    return
  fi

  command -v sha256sum >/dev/null 2>&1 || die "sha256sum is required"
  command -v tar >/dev/null 2>&1 || die "tar is required"

  local asset="CodexBarCLI-v${version}-${platform}.tar.gz"
  local base_url="${RELEASES_URL}/v${version}"
  local tmp
  tmp="$(mktemp -d)"
  # shellcheck disable=SC2064 # expand now: tmp is local to this function
  trap "rm -rf -- '$tmp'" EXIT

  log "Downloading $asset"
  download "$base_url/$asset" "$tmp/$asset" || die "download failed: $base_url/$asset"
  download "$base_url/$asset.sha256" "$tmp/$asset.sha256" || die "download failed: $base_url/$asset.sha256"

  local expected
  expected="$(awk 'NR == 1 { print $1 }' "$tmp/$asset.sha256")"
  [[ "$expected" =~ ^[0-9a-fA-F]{64}$ ]] || die "malformed checksum file for $asset"
  log "Verifying checksum"
  (cd "$tmp" && printf '%s  %s\n' "$expected" "$asset" | sha256sum -c -) \
    || die "checksum mismatch for $asset; aborting"

  mkdir -p "$tmp/extract"
  tar -xzf "$tmp/$asset" -C "$tmp/extract"

  # The tarball is expected to be flat; tolerate a single top-level folder.
  local binary src
  binary="$(find "$tmp/extract" -maxdepth 2 -name CodexBarCLI -type f -print -quit)"
  [[ -n "$binary" ]] || die "CodexBarCLI not found in $asset"
  src="$(dirname -- "$binary")"
  [[ -d "$src/CodexBar_CodexBarCore.bundle" ]] || die "CodexBar_CodexBarCore.bundle not found next to CodexBarCLI"
  chmod 755 "$src/CodexBarCLI"
  [[ -e "$src/codexbar" || -L "$src/codexbar" ]] || ln -s CodexBarCLI "$src/codexbar"

  # Stage next to the final location so the last step is an atomic rename.
  mkdir -p "$share_dir"
  rm -rf -- "$install_dir" "$install_dir.partial"
  cp -a -- "$src" "$install_dir.partial"
  mv -- "$install_dir.partial" "$install_dir"

  rm -rf -- "$tmp"
  trap - EXIT
  log "Installed CodexBar v${version} into $install_dir"
}

link_cli() {
  mkdir -p "$bin_dir"
  # Link to the binary inside the versioned folder: the resource bundle must
  # stay next to CodexBarCLI.
  ln -sfn "$install_dir/codexbar" "$bin_dir/codexbar"
  log "Linked $bin_dir/codexbar -> $install_dir/codexbar"
}

install_wrapper() {
  install -m 755 "$script_dir/codexbar-serve" "$bin_dir/codexbar-serve"
  log "Installed $bin_dir/codexbar-serve"
}

install_config() {
  mkdir -p "$config_dir"
  chmod 700 "$config_dir"
  if [[ -e "$config_dir/config.json" ]]; then
    log "Keeping existing $config_dir/config.json"
  else
    install -m 600 "$script_dir/config.example.json" "$config_dir/config.json"
    log "Created $config_dir/config.json"
  fi
}

create_token() {
  local token_file="$config_dir/dashboard-token"
  if [[ -s "$token_file" ]]; then
    log "Keeping existing dashboard token"
    return
  fi
  (
    umask 077
    if command -v openssl >/dev/null 2>&1; then
      openssl rand -hex 32 >"$token_file"
    else
      head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' >"$token_file"
      printf '\n' >>"$token_file"
    fi
  )
  chmod 600 "$token_file"
  log "Created dashboard token in $token_file"
}

service_path() {
  local dirs=("$bin_dir") tool found dir
  for tool in claude codex gemini agy gh opencode; do
    if found="$(command -v "$tool" 2>/dev/null)" && [[ "$found" == /* ]]; then
      dirs+=("$(dirname -- "$found")")
    fi
  done
  dirs+=(/usr/local/bin /usr/bin /bin)

  local result="" seen=":"
  for dir in "${dirs[@]}"; do
    [[ "$seen" == *":$dir:"* ]] && continue
    seen+="$dir:"
    result+="${result:+:}$dir"
  done
  printf '%s' "$result"
}

render_unit() {
  local template="$script_dir/codexbar.service.in"
  local unit="$config_dir/codexbar.service"
  local content user path
  [[ -f "$template" ]] || die "missing template: $template"
  user="$(id -un)"
  path="$(service_path)"
  content="$(<"$template")"
  # Quoted replacements keep '&' and '\' literal (bash 5.2 patsub_replacement).
  content="${content//@USER@/"$user"}"
  content="${content//@HOME@/"$HOME"}"
  content="${content//@PATH@/"$path"}"
  printf '%s\n' "$content" >"$unit"
  chmod 644 "$unit"
  log "Rendered $unit"
  cat <<EOF

Install and start the system unit (WSL cannot reach the systemd user bus under WSLg):

  sudo install -m 644 ~/.config/codexbar/codexbar.service /etc/systemd/system/codexbar.service && sudo systemctl daemon-reload && sudo systemctl enable --now codexbar

EOF
}

install_cli
link_cli
install_wrapper
install_config
create_token
if ((render_service)); then
  render_unit
fi

cli_version="$("$bin_dir/codexbar" --version 2>&1 | head -n 1 || true)"
port="${CODEXBAR_PORT:-8787}"
cat <<EOF
Summary
  Installed version: v${version} (${platform})
  codexbar --version: ${cli_version:-unavailable}
  Dashboard token:   cat ~/.config/codexbar/dashboard-token
  Health check:      curl http://localhost:${port}/health
EOF

case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *) printf '\nNote: %s is not on PATH; add it to your shell profile.\n' "$bin_dir" ;;
esac
