# Contributing to OrionLock

Thanks for taking the time to look at this. OrionLock is a small distributed-lock abstraction with Redis, PostgreSQL, SQL Server, EF Core and in-memory backends (plus Consul, etcd and ZooKeeper backends that are not published yet). The project is small and the bar for contributions is "does it make the package clearer, faster, or safer without expanding the public surface needlessly."

## Before you open a PR

For anything beyond a typo, a docs tweak, or a one-line fix, please open an issue first. Five minutes of alignment up front saves an afternoon of rework later. State:

- The use case you are trying to solve
- What you tried that did not work
- Whether you want to send the patch yourself or are flagging the gap

For typos, docs polish, comment fixes, single-line changes, please skip the issue and send a PR directly. Title it `docs: ...` or `chore: ...` so it is obvious from the queue.

## Local development

```bash
git clone https://github.com/tunahanaliozturk/OrionLock
cd OrionLock
dotnet restore
dotnet build Moongazing.OrionLock.sln -c Release
dotnet test Moongazing.OrionLock.sln -c Release
```

The .NET 10 SDK is required to build: every project targets `net8.0;net9.0;net10.0`. The .NET 8 and 9 runtimes are needed to run the full multi-targeted test matrix. The container-backed suites (Redis, PostgreSQL, SQL Server, EF Core) use Testcontainers and need a running Docker daemon; without one those tests are skipped, not failed.

Branch from `main`. Name the branch after intent: `feat/...`, `fix/...`, `docs/...`, `refactor/...`, `chore/...`, `test/...`.

## Pull request shape

- One conceptual change per PR. Refactors and behaviour changes go in separate PRs even if the diff feels small.
- Conventional Commits style commit subject (`feat:`, `fix:`, `docs:`, etc.).
- New behaviour comes with tests. Bug fixes come with a failing-before, passing-after test.
- Public API additions need XML doc comments. Breaking changes need a CHANGELOG entry.
- No `Co-Authored-By` trailers. The author of the PR is the author of the work.

## Coding style

- `Directory.Build.props` sets `TreatWarningsAsErrors`, `AnalysisLevel` `latest-recommended` and `EnforceCodeStyleInBuild`, so an analyzer or `.editorconfig` warning fails the build. Treat warnings as bugs.
- The published packages carry `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` baselines. New public API goes into `PublicAPI.Unshipped.txt`.
- Match the surrounding code style. The repo does not have a separate STYLE.md; if the existing code does X, do X.
- Names are spelled out. No `mgr`, `svc`, `ctx`. The exceptions are well-known abbreviations (`Id`, `Db`, `Url`, `Json`).
- Comments explain why, not what. The code already says what.

## Tests

- xUnit, with Moq where a fake is needed. Assertions use xUnit's `Assert`.
- Test names read as a statement about the behaviour: `AddOrionLock_ShouldRegister_IDistributedLock_AsSingleton`, `Deadline_ReturnsHandle_WhenFree`.
- Tests that need a real backend use Testcontainers and the `[DockerFact]` / `[DockerTheory]` attributes from `tests/Shared`, which skip the test when Docker is unavailable.
- Coverage is a side effect of writing tests for behaviour, not a target in itself.

## Reporting bugs

Open an issue with the [bug report form](https://github.com/tunahanaliozturk/OrionLock/issues/new?template=bug_report.yml). Include:

- A minimal reproduction (one file, one method, ideally less than 50 lines)
- The actual behaviour vs the expected behaviour
- The runtime (`dotnet --info` output) and the package version

If the bug has security implications, do not open a public issue; follow [SECURITY.md](SECURITY.md).

## Security

Do not file public issues for vulnerabilities. Report them privately through GitHub as described in [SECURITY.md](SECURITY.md).

## Conduct

Be kind. We follow the [Code of Conduct](CODE_OF_CONDUCT.md). Disagreement is fine; rudeness is not.

## License

By submitting a pull request, you agree your contribution is licensed under the repo's [MIT License](LICENSE.txt).
