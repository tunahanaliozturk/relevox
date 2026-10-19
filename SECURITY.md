# Security

## Reporting a vulnerability

Open a [private security advisory](https://github.com/tunahanaliozturk/relevox/security/advisories/new)
rather than a public issue. I will acknowledge within a few days and keep you updated.

## What is in scope

Relevox runs inside your process and inside your compiler, so the interesting failures are these:

- **Generated code that calls the wrong handler**, or skips a behaviour that was registered. Authorization
  and validation are commonly written as pipeline behaviours; a dispatch path that bypasses them is a
  security bug, not just a correctness one.
- **A source generator that does something other than generate source.** It must not read files, touch
  the network or emit code that depends on anything outside the compilation.
- **Dependency licence or supply chain problems**, such as a transitive package with terms that would
  stop you shipping.

## What is not

Anything your own handlers and behaviours do with the requests they receive. The library moves a request
to its handler; validating it is your pipeline's job.
