# Releasing Codex Deck

The repository contains a tag-driven GitHub Actions release workflow.

## Before tagging

1. Update `CHANGELOG.md`.
2. Build locally with `build.ps1`.
3. Run the deterministic diagnostics documented in `CONTRIBUTING.md`.
4. Confirm the working tree is clean and `main` is pushed.

## Publish a release

```powershell
git tag -a v0.1.0 -m "Codex Deck v0.1.0"
git push origin v0.1.0
```

The release workflow will:

- build the Windows executable;
- run cache, title and navigation diagnostics;
- package the executable and public documentation;
- generate a SHA256 checksum;
- create a GitHub Release using the tag name and generated notes.

Do not reuse or move an existing release tag. If a workflow fails, fix the source and create the next patch version.
