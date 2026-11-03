# Contributing

This is an early hobby project, so things move around a lot. Issues and ideas are welcome. If you want to send a pull request, it is best to open an issue first so we can agree on the direction.

## Setup

- .NET 8 SDK (the version in global.json)
- `dotnet restore`, `dotnet build -c Release`, `dotnet test -c Release`

Warnings are treated as errors, so a clean build is the bar.

## Pull requests

- keep them small and focused
- add or update tests for what you change
- do not log chunk text anywhere, log ids and short hash pieces instead
- fingerprints get stored, so anything that changes a hash or a signature needs a version bump and a golden test update
- plain comments, say why and not only what

## Commit messages

Short, in the imperative: "Add SimHash band index", "Fix whitespace handling in the normalizer".
