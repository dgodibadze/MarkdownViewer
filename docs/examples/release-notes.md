# Aurora 2.4 — Release Notes

_Shipped 2026-07-10 · [Full changelog](#) · [Upgrade guide](#upgrading)_

## Highlights

**Aurora 2.4** focuses on startup performance and a friendlier CLI. Cold start
is now **41% faster** on Apple Silicon, and every command finally has `--help`
that fits on one screen.

## What's new

### Performance

- Lazy-load the plugin registry — saves ~180 ms on launch
- Cache parsed configs between runs (`~/.aurora/cache`)
- Parallel asset fingerprinting on machines with 4+ cores

### CLI

```console
$ aurora deploy --env staging --watch
✓ Built 214 assets in 3.2s
✓ Uploaded to staging (eu-west-1)
→ Watching for changes… (Ctrl+C to stop)
```

### Fixed

- Config reloads no longer drop in-flight requests (#1482)
- `aurora doctor` detects orphaned lockfiles (#1477)
- Windows paths with spaces quote correctly in generated scripts (#1465)

## Upgrading

```bash
brew upgrade aurora    # macOS
winget upgrade aurora  # Windows
```

> **Note** — 2.4 drops support for config schema v1. Run `aurora migrate-config`
> once before upgrading production environments.

| Version | Status | Supported until |
|---|---|---|
| 2.4 | **Current** | — |
| 2.3 | Maintenance | 2026-12-01 |
| 2.2 | End of life | 2026-07-01 |
