# Contributing to a2n.Hangfire.Dashboard

Thank you for your interest in contributing! This project is open source and welcomes contributions of all kinds — bug reports, feature requests, documentation improvements, and code contributions.

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download) or later
- A code editor (Visual Studio, VS Code, Rider, etc.)
- For PostgreSQL adapter tests: a local PostgreSQL instance (see `tests/a2n.Hangfire.Dashboard.PostgreSql.Tests/appsettings.json`)

### Setting Up the Development Environment

1. Fork and clone the repository:

```bash
git clone https://github.com/<your-username>/a2n.Hangfire.Dashboard.git
cd a2n.Hangfire.Dashboard
```

2. Build the solution:

```bash
dotnet build src/Hangfire\ Dashboard.slnx
```

3. Run the tests:

```bash
dotnet test
```

4. Run the sample app:

```bash
cd samples/SampleApp
dotnet run
```

5. Open `https://localhost:7100/hangfire` in your browser.

Other sample apps:

| Sample | Purpose |
|--------|---------|
| `samples/SampleAppAuth` | Cookie authentication with login page |
| `samples/SampleAppMvc` | ASP.NET Core MVC host integration |
| `samples/SampleAppRazor` | Razor Pages host integration |
| `samples/SampleAppBlazor` | Blazor host integration |
| `samples/SampleAppOrig` | Legacy `Startup`-pattern host |

## How to Contribute

### Reporting Bugs

- Use the [GitHub Issues](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues) page.
- Include steps to reproduce, expected behavior, and actual behavior.
- Include your .NET version and OS.

### Suggesting Features

- Open a [GitHub Issue](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues) with the `enhancement` label.
- Describe the use case and why it would be valuable.

### Submitting Code

1. Create a feature branch from `main`:

```bash
git checkout -b feature/your-feature-name
```

2. Make your changes. Follow the existing code style and conventions.

3. Ensure the project builds without errors:

```bash
dotnet build src/Hangfire\ Dashboard.slnx
```

4. Run the tests:

```bash
dotnet test
```

5. Commit your changes using [Conventional Commits](https://www.conventionalcommits.org/):

```bash
git commit -m "feat: add your feature description"
```

6. Push and open a Pull Request against `main`.

## Code Style

- Follow existing patterns in the codebase.
- Use C# conventions (PascalCase for public members, camelCase with underscore prefix for private fields).
- Blazor components use `.razor` files with `@code` blocks.
- Keep custom CSS minimal — prefer Bootstrap utility classes.
- Use Bootstrap Icons (`bi bi-*`) for all icons.

## Project Structure

| Directory | Purpose |
|-----------|---------|
| `src/a2n.Hangfire.Dashboard/` | Main dashboard project |
| `src/a2n.Hangfire.Dashboard.SqlServer/` | SQL Server storage adapter (analytics + optimized queries) |
| `src/a2n.Hangfire.Dashboard.PostgreSql/` | PostgreSQL storage adapter (analytics + optimized queries) |
| `src/a2n.Hangfire.Console/` | Console integration (drop-in replacement) |
| `src/a2n.Hangfire.Tags/` | Tags integration (drop-in replacement) |
| `tests/` | Unit and integration tests |
| `samples/` | Demo applications for different host types |

## Pull Request Guidelines

- Keep PRs focused — one feature or fix per PR.
- Include a clear description of what changed and why.
- Reference related issues (e.g., "Closes #42").
- Ensure the build and tests pass before requesting review.
- Be responsive to feedback during code review.

## License

By contributing, you agree that your contributions will be licensed under the [LGPL-3.0-or-later License](LICENSE).
