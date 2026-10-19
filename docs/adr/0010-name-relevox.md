# 10. The name: Relevox

Status: accepted
Date: 2026-10-19

## Context

The project was built as Çapar, written `Capar` in package IDs and namespaces, after the Ottoman mounted
courier. The name was always provisional: nothing was released, and no NuGet prefix was reserved, so that it
could still change cheaply.

Two things made it worth changing before the first release:

- The owner means to build a brand on it. That needs a name that reads the same in every language, and one
  that can own a domain and a trademark. `Ç` turns into `C` in every identifier, and outside Turkish
  "capar" carries none of the story.
- Changing it after release costs every user a package swap. Changing it before costs one pull request.

## Decision

The name is **Relevox**: *relevo*, the relay race in Spanish and Portuguese, and *vox*, Latin for voice,
the message carried from runner to runner. The diagnostics move from `CPR` to `RVX`.

Everything follows the name:

- Package IDs: `Relevox` and `Relevox.Contracts`.
- Namespaces: `Relevox`, `Relevox.Pipeline` and the others.
- The generated code: `RelevoxMediator` and `AddRelevox`, and `RelevoxOptions` for the options.
- Rule IDs: `RVX0001` to `RVX0007`, with help links that follow the repository.

The logo is three batons in a relay, each passing in front of the one before, the last in signal yellow.

Candidates were checked on 2026-10-19 against NuGet, npm, PyPI, crates.io, GitHub, the `.com`, `.dev` and
`.io` domains, and a web search for companies of the same name. Relevox was free on every registry and
turned up no company.

## Consequences

- Nothing outside this repository depended on the old name, so nothing breaks. The two real-project
  migrations in `docs/migration-trial.md` were run under the old name; their numbers stand.
- The GitHub repository is renamed to match, and GitHub redirects the old address.
- A formal trademark search, and registering the domain, are for the owner to do before building a brand on
  the name.

## Alternatives considered

- **Keep Çapar.** It has a story, but the `Ç` cannot survive into an identifier, and the name means nothing
  outside Turkish.
- **Relevo.** It sounded right but was taken where it matters. It is a registered trademark that covers
  software, held by a Spanish media company. A developer tool on npm already uses it, as do two companies
  in Germany and France. Every main domain is registered.
- **Estafet, Ekiden, Staffetta, Ulag, Baton.** All free on NuGet. Estafet is also the name of a London
  software consultancy. The others each lost on sound or story, in the owner's judgement.
