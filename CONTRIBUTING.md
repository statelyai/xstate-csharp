# Contributing

## Build and test

Requires the .NET 10 SDK (pinned in `global.json`).

```bash
dotnet build XState.slnx
dotnet test XState.slnx
```

CI runs the same on every PR and push to `main`. The W3C SCXML suite must match
[CONFORMANCE.md](CONFORMANCE.md) exactly: an unexpected pass fails the run, same as an
unexpected failure. Update the `KnownFailing` list and CONFORMANCE.md together.

## Commit messages

Use [Conventional Commits](https://www.conventionalcommits.org/). Release Please reads
them to pick the next version and write the changelog:

| Prefix | Effect |
|---|---|
| `fix:` | patch bump, listed under Bug Fixes |
| `feat:` | minor bump, listed under Features |
| `feat!:` or `BREAKING CHANGE:` footer | major bump (while pre-1.0: minor) |
| `chore:`, `docs:`, `ci:`, `test:`, `refactor:` | no release, not in changelog |

Squash-merge PRs and make the squash title the conventional commit.

## Releasing

Releases are automated. Do not edit `<Version>` in `Directory.Build.props` or
`CHANGELOG.md` by hand.

1. Merge PRs to `main` as usual.
2. Release Please keeps a PR open titled `chore(main): release <version>`. It bumps
   `<Version>` and updates `CHANGELOG.md`. Review the changelog there.
3. Merge that PR. This creates a draft GitHub release and runs `release.yml`, which builds,
   tests, runs the W3C gate, packs, and pushes `XState` and `XState.Scxml` to nuget.org.
4. On success the workflow publishes the release, which creates the `v<version>` tag.
   On failure nothing is public: fix on `main`, then re-run the failed workflow.

### Versioning

Pre-1.0: `feat:` bumps minor, `fix:` bumps patch. Versions are alpha prereleases
(`0.1.0-alpha`, ...). To go stable, remove `prerelease`, `prerelease-type`, and
`versioning` from `release-please-config.json`.

### Publishing credentials

NuGet uses Trusted Publishing (OIDC): nuget.org holds a policy for
`statelyai/xstate-csharp` + `release.yml`, packages `XState*`, owner `davidkpiano`.
No API key secret exists. `NuGet/login@v1` exchanges the workflow's OIDC token for a
short-lived key. Manage the policy at https://www.nuget.org/account/trustedpublishing.

Optional: a `RELEASE_PLEASE_TOKEN` repo secret (fine-grained PAT with contents and
pull-requests write) makes the release PR open as a user so CI runs on it. Without it the
PR still works; `release.yml` runs the full suite before publishing regardless.

### Manual release

Pushing a `v<Version>` tag matching `Directory.Build.props` also runs `release.yml`.
Use only if the automation is broken.
