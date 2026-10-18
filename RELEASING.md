# Releasing

Nothing is released by merging. A release starts when a version tag is pushed, and its last step, the push to
NuGet, waits for an approval.

## Once, before the first release

1. **Decide the name.** The package IDs, the namespaces, the diagnostic prefix `CPR`, the help links and the
   logo all carry it; renaming later is a breaking change for everyone who installed it.
2. **Reserve the package ID prefix** on nuget.org for the account that will own the packages, so nobody else
   can publish under it.
3. **Set up trusted publishing** on nuget.org: a policy for this repository, the workflow `release.yml` and
   the environment `nuget`. No API key is stored anywhere; the workflow exchanges its identity for a
   short-lived one.
4. **Create the `nuget` environment** in the repository settings, with yourself as a required reviewer, and set
   the repository variable `NUGET_USER` to the nuget.org account name.
5. **Make the repository public** if it is not yet. The SourceLink in the packages points at it, and CodeQL
   starts running then.

## Each release

1. Move the `Unreleased` section of `CHANGELOG.md` under a heading `## [x.y.z] - yyyy-mm-dd` and merge that.
   The workflow refuses a tag with no matching section, and the section becomes the release notes.
2. Tag the merge commit and push the tag:

   ```bash
   git tag v1.0.0-rc.1
   git push origin v1.0.0-rc.1
   ```

   A version with a suffix, such as `-rc.1`, becomes a pre-release on GitHub and on NuGet.
3. The workflow builds, runs every test suite, packs with the tag's version, installs the packages into a
   new application and runs it, and validates SourceLink, determinism and compiler flags. It then creates
   the GitHub release with the packages attached.
4. Approve the `publish to NuGet` job when you are ready. It pushes both packages and their symbols.

## If something is wrong

A package on NuGet cannot be deleted, only unlisted. Unlist the version, fix forward, and release the next
patch version; never reuse a version number.
