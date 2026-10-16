# Changelog

All notable changes to this project are written down here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Repository scaffold, quality gates and CI.
- Baseline benchmarks of MediatR 12.5.0 and Mediator 3.0.2: send, pipeline, publish, stream and
  container startup, with a sanity mode that proves every scenario reaches its handler.
- Licence audit tool that fails the build on any non-permissive dependency.
