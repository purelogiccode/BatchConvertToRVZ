# Contributing

Thanks for your interest in improving RVZStudio. This page describes how to report issues, submit
changes and follow the project conventions.

## Ways to contribute

- **Report bugs** — open an issue with reproduction steps and log output.
- **Request features** — describe the use case and the expected behavior.
- **Improve documentation** — the `docs/` folder and the root `ReadMe.md` are good starting points.
- **Submit code** — bug fixes, platform fixes and new features are welcome.
- **Star the project** — it helps others discover RVZStudio.

## Reporting issues

Open an issue at <https://github.com/purelogiccode/RVZStudio/issues> and include:

1. **Environment** — operating system, architecture and RVZStudio version (About window).
2. **Steps to reproduce** — the exact sequence that triggers the problem.
3. **Expected vs actual behavior**.
4. **Logs** — the relevant part of the log file (see [Troubleshooting](troubleshooting.md) for
   locations). The on-screen log viewer shows the same messages.
5. **Screenshots** — press `F8` in the application to save one to the `Screenshot` folder.

Please do not attach copyrighted game files. A small corrupt or synthetic sample that reproduces
the problem is fine.

## Development workflow

1. Fork the repository and create a topic branch from `master`.
2. Make your changes, keeping commits focused and atomic.
3. Build and test locally:

   ```bash
   dotnet build RVZStudio.sln -c Release
   dotnet test RVZStudio.sln
   ```

4. Push the branch and open a pull request against `master`.
5. Ensure CI passes on all three operating systems before requesting review.

## Coding conventions

- **Language** — C# with nullable reference types and implicit usings enabled.
- **Analyzers** — Roslynator and Meziantou analyzers run as part of the build. Keep the build at
  **zero warnings**.
- **Async** — services are asynchronous and accept a `CancellationToken`; never block the UI
  thread.
- **UI updates** — marshal to the UI thread with `Dispatcher.UIThread` from services and
  background tasks.
- **Logging** — use Serilog (`Log.Information`, `Log.Warning`, `Log.Error`). `Warning` and above
  are automatically forwarded to the bug-report API, so do not log expected user errors at those
  levels.
- **Comments** — the codebase prefers self-documenting code; add XML documentation for public
  APIs where it clarifies intent.
- **Tests** — add or update tests in `RVZStudio.Tests` for service and model changes. Tests must
  pass on Windows, Linux and macOS.

## Commit conventions

The project uses [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>: <short summary>

<optional body>
```

Common types used in this repository: `feat`, `fix`, `chore`, `refactor`, `docs`, `test`, `ci`.

Examples:

```
feat: add WIA output to the extraction tab
fix: keep the batch running when DolphinTool fails to start
docs: document the release workflow
```

## Pull request checklist

- [ ] The solution builds with zero warnings.
- [ ] `dotnet test` passes.
- [ ] New behavior is covered by tests where practical.
- [ ] Documentation (`ReadMe.md`, `docs/`) is updated when user-facing behavior changes.
- [ ] The commit history is clean and follows the conventions above.

## License

By contributing, you agree that your contributions are licensed under the
[GNU General Public License v3.0](../LICENSE.txt), the same license as the project.

## Related pages

- [Building](building.md) — build, test and publish instructions.
- [Architecture](architecture.md) — how the application is structured.
