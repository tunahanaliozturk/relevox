# Releasing

Nothing is released by merging. A release starts when a version tag is pushed, and its last step, the push to
NuGet, waits for an approval.

## Once, before the first release

1. **Decide the name.** Done: Relevox, with the diagnostic prefix `RVX` ([ADR 0010](docs/adr/0010-name-relevox.md)).
   The package IDs, the namespaces, the help links and the logo all carry it; renaming after a release is a
   breaking change for everyone who installed it.
2. **Reserve the package ID prefix** on nuget.org for the account that will own the packages, so nobody else
   can publish under it.
3. **Set up trusted publishing** on nuget.org: a policy for this repository, the workflow `release.yml` and
   the environment `nuget`. No API key is stored anywhere; the workflow exchanges its identity for a
   short-lived one.
4. **Create the `nuget` environment** in the repository settings, with yourself as a required reviewer, and set
   the repository variable `NUGET_USER` to the nuget.org account name.
5. **Make the repository public** if it is not yet. The SourceLink in the packages points at it, and CodeQL
   starts running then.
6. **Protect `main`**, which GitHub allows on a private repository only on a paid plan. The rules are in
   [`eng/main-ruleset.json`](eng/main-ruleset.json): changes arrive through a pull request with its
   conversations resolved, every CI job has to pass on an up-to-date branch, history stays linear, and the
   branch can be neither force-pushed nor deleted. No approving review is required, so a sole maintainer can
   still merge. Apply them once:

   ```bash
   gh api -X POST repos/tunahanaliozturk/relevox/rulesets --input eng/main-ruleset.json
   ```

   CodeQL's `analyze` job is among the required checks, since it first ran on 2026-10-19.

## Each release

1. Move the `Unreleased` section of `CHANGELOG.md` under a heading `## [x.y.z] - yyyy-mm-dd` and merge that.
   The workflow refuses a tag with no matching section, and the section becomes the release notes. In the same
   pull request, move the rules in each `AnalyzerReleases.Unshipped.md` to `AnalyzerReleases.Shipped.md` under
   `## Release x.y.z`, and update the README's status line.
2. Tag the merge commit and push the tag:

   ```bash
   git tag v1.0.0-rc.1
   git push origin v1.0.0-rc.1
   ```

   A version with a suffix, such as `-rc.1`, becomes a pre-release on GitHub and on NuGet.
3. The workflow builds, runs the generator, differential and web sample tests, packs with the tag's version,
   installs the packages into a new application and runs it, and validates SourceLink, determinism and compiler
   flags. It then creates the GitHub release with the packages attached. NativeAOT, the licence audit and the
   benchmark sanity run are CI's, on the commit being tagged: tag only a commit whose CI is green.
4. Approve the `publish to NuGet` job when you are ready. It pushes both packages and their symbols.

## If something is wrong

A package on NuGet cannot be deleted, only unlisted. Unlist the version, fix forward, and release the next
patch version; never reuse a version number.
