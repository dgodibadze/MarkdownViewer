# Aurora 2.4 — Release Notes

_Shipped 2026-07-10 · cold start now **41% faster** on Apple Silicon_

```console
$ aurora deploy --env staging
✓ Built 214 assets in 3.2s
✓ Uploaded to staging (eu-west-1)
```

**Fixed**

- Config reloads no longer drop in-flight requests (#1482)
- `aurora doctor` now detects orphaned lockfiles (#1477)

| Version | Status | Supported until |
|---|---|---|
| 2.4 | **Current** | — |
| 2.3 | Maintenance | 2026-12-01 |
