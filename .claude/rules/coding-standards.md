# Coding Standards

**Always loaded** — no `paths:` frontmatter. These apply to every file in the codebase, are stable, and survive `/compact`. Layer-specific and language-deep rules live in the path-scoped siblings (`csharp.md`, `domain.md`, `application.md`, `infrastructure.md`, `webapi.md`, `tests.md`, `sql.md`), which load only when a matching file is read and do **not** survive compaction.

## Formatting

`.editorconfig` at the repository root is the authority — 9.5 KB of settings, enforced by the IDE and `dotnet format`. Never restate or override it. The five an agent most often gets wrong:

* **File-scoped namespaces** (`namespace Foo;`), not block-scoped.
* **Allman braces** — opening brace on its own line, always, including single-statement `if` bodies.
* 4 spaces, never tabs.
* **CRLF** line endings, and **no** final newline.
* No `this.` qualification on fields.

Run `dotnet format` if unsure rather than hand-aligning.

## Typing Expectations

* **.NET 8, C# 12.** No C# 13/14 syntax — it will not compile against `net8.0`.
* **Nullable reference types enabled.** Never suppress with `!` without an explanatory comment on the same line.
* **`sealed` by default** on concrete classes. Leave a type open only when something actually inherits it.
* **One type per file**, and the file name matches the type name exactly.
* **`/// <summary>` on every public type, property, and method.** `/// <inheritdoc />` on `Handle`.
* Typed domain IDs across every boundary — never a raw `Guid`. NodaTime, never `DateTime`; see CLAUDE.md.

| Use | For |
|---|---|
| `sealed record` (positional preferred) | Commands, queries, events, contracts, value objects |
| `sealed class` + primary constructor | Handlers, repositories, query implementations, services |
| `record class` | Contracts needing `init`-only properties or complex constructors |

**Dependencies** are constructor-injected and asserted in the **field initializer**, never the constructor body:

```csharp
private readonly IBranchRepository _repository = repository.AssertIsNotNull();
```

**Async all the way.** Never `.Result` or `.Wait()`. Every `async` method takes `CancellationToken` as its last parameter and passes it to every awaitable call; synchronous methods never take one. Use `IClockService` for time, never `SystemClock.Instance`.

## Testing Requirements

* Every new or changed handler, domain method, and validator gets a test in the mirroring `*.Tests` project. Handler coverage is currently ~32% (250 of 778) — treat that as debt to not add to, not as licence to skip.
* **AAA with `// Arrange`, `// Act`, `// Assert` comments.** Never given/when/then.
* Name tests `MethodName_StateUnderTest_ExpectedBehavior`.
* **Static deterministic values only** — no Bogus, no `_faker`, no `Guid.NewGuid()` in an arrangement. A test must produce the same result on every run.
* Never `if (x is null) return;` to skip a test body. Seed the data instead; a missing fixture must fail, not silently pass.
* Assert the commit count: `1` on the happy path, `0` on every failure path.
* Handlers return `Result` — assert `IsFailed` and the error type, not a thrown exception. Exceptions are asserted only on domain invariants.
* Run `dotnet test` on the affected project before reporting work complete.

Assertion library and the full mocking conventions: `rules/tests.md`.

## Logging Conventions

Serilog behind `ILogger<T>`, with Elasticsearch formatting — so the message template *is* the query interface. A template placeholder becomes a searchable field; an interpolated string becomes dead text.

* Inject `ILogger<T>`; name the field `_logger`.
* **Structured templates with `{PascalCase}` placeholders. Never string interpolation.**

```csharp
// Correct — ExportId is a queryable field in Elasticsearch
_logger.LogError(ex, "Error expiring export {ExportId}. Continuing with remaining exports.", export.Id);

// Wrong — the ID is baked into the message text and cannot be queried
_logger.LogError(ex, $"Error expiring export {export.Id}.");
```

There are ~254 interpolated log calls still in the codebase against ~618 structured ones. Don't add to the former; convert one when you're already editing that line.

* The exception is always the **first** argument to `LogError` / `LogCritical`, not folded into the message.
* **Never log PHI or PII** — no patient names, insurance numbers, or free-text medical content. Log IDs and let the reader join.
* Levels: `LogDebug` for developer tracing, `LogInformation` for a completed business action, `LogWarning` for a recoverable anomaly, `LogError` for a failed operation with its exception, `LogCritical` only for data-loss or startup-fatal conditions.
* Never swallow silently — log at `Error` before returning an `InfrastructureError`. Log auth failures and denials.
* `LoggerMessage` source generation is not used in this codebase; don't introduce it as a one-off.
