# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Squill Is

A declarative, cross-platform, database-independent SQL deployment tool inspired by (and aiming for compatibility with) SSDT/DACPAC, targeting non-SQL-Server databases — PostgreSQL first. Users express schema as `CREATE` statements in `.sql` files; Squill builds an environment-neutral DACPAC from them, then deploys by diffing the DACPAC model against the target database and scripting the changes. See README.md for the full motivation and design.

## Commands

Requires the .NET 10 SDK (all projects target `net10.0`).

- Build: `dotnet build Squill.slnx`
- Run all tests: `dotnet test`
- Run one test project: `dotnet test Squill.PostgresParser.Tests`
- Run a single test (xunit v3): `dotnet test Squill.PostgresParser.Tests --filter "FullyQualifiedName~CreateTableTests.TestName"`
- Integration tests (`Squill.IntegrationTests`) require Docker — they spin up a `postgres:latest` container via Testcontainers.

`WarningsAsErrors=nullable` is set in most projects; nullable warnings fail the build.

## Testing philosophy

The goal is thorough **unit and integration test coverage for every feature**. For any feature that touches SQL, two things must both be verified:

1. **Unit tests** that the parser and model builders handle the SQL correctly (e.g. `Squill.PostgresParser.Tests`, `Squill.Provider.Postgres.Tests`) — parsing produces the right syntax tree, and mapping produces the right `Model`/`Element` structure.
2. **Integration tests** (`Squill.IntegrationTests`) that the same SQL actually works against a **real PostgreSQL database** via Testcontainers — that the DDL we parse and generate is valid, executable Postgres, not just something that round-trips through our own code.

Parsing something correctly in isolation is not enough; a feature isn't considered covered until there's an integration test proving it behaves correctly end-to-end against real Postgres. New SQL-facing features should add both kinds of test, ideally test-first (TDD).

## Architecture

Two phases: **build** (read declarative SQL → validate → in-memory model → serialize to DACPAC) and **deploy** (deserialize DACPAC → extract model from target database → diff models → script and run changes). The build is independent of any target database.

### Core model (`Squill.Core`)

Everything revolves around a generic, database-agnostic `Model`: a list of `Element`s (typed by string, e.g. `"SqlTable"`, mirroring SSDT element type names for DACPAC compatibility), each holding `Property`s, `Relationship`s (links to other elements or entries), and `Annotation`s. Models are compared with a Merkle-tree approach: every node implements `IHashable` and `HashUtility` computes SHA256 hashes bottom-up, so equal top-level hashes short-circuit the diff. `SchemaCompare` produces a `SchemaComparison` of deltas — `CreateDelta`, `AlterDelta` and `RebuildTableDelta` (in-place column changes vs. full table rebuild), `RecreateDelta` (indexes, and replaceable objects like procedures/functions/views), `AlterExtensionVersionDelta`, `AddConstraintDelta` (deferred FKs added to break circular table dependencies), and `DropDelta` (gated by the `DropObjectsNotInSource` option). The remaining `NotImplementedException` is narrow: altering *in place* an existing object that is neither a table, an extension, nor a "replaceable" type (e.g. redefining an enum, domain, aggregate, or schema).

Database specifics are abstracted behind `IDatabaseProvider` / `IDatabase` / `IDatabaseModelBuilder` / `IDatabaseDependencyAnalyzer` (the dependency analyzer handles things like primary keys being dependent on their table, so dependent elements are attached to their parent's delta). A `Workspace` is just the set of source `IFile`s.

There are two strategies for turning a workspace of SQL files into a `Model`:

1. `TemporaryDatabaseModelBuilder` (Squill.Core) — runs the scripts against a temporary database created by the provider, then extracts the model from that database. Requires a live server and ordered scripts.
2. `ParserWorkspaceModelBuilder` (Squill.Provider.Postgres) — parses the SQL text directly with the ANTLR-based parser, no database needed. This is the direction the project is moving.

**Column `DEFAULT`s** are canonicalized by `PostgresDefaultValue` / `MariaDbDefaultValue` so a default parsed from source and the same default read back from the catalog reduce to one token — otherwise every deploy would see a phantom column change. The two engines need opposite treatment: **Postgres preserves the spelling it was given**, so each spelling maps to its *own* token, while **MariaDB/MySQL collapse synonyms** into one. A canonical token can also depend on the target engine, which that engine's `DatabaseSchemaProvider` declares. Only a narrow allowlist of non-constant defaults is modeled; anything else stays unmodeled with an SQ1002 warning rather than risking a round trip it cannot make. **Decisions here must be measured against a live server, never inferred from the grammar** (which is looser than the engines) — otherwise a default that cannot round-trip gets modeled and re-diffs on every deploy.

### PostgreSQL parser (`Squill.PostgresParser`)

ANTLR4-based parser producing a typed syntax tree (the `Syntax/` classes, e.g. `CreateTableStatement`, `ColumnDefinition`). `PostgresVisitor` is a large partial class split across one file per grammar rule (`PostgresVisitor.Createstmt.cs`, etc.) that maps the ANTLR parse tree to the syntax model. `CaseChangingCharStream` handles case-insensitive keywords.

`func_expr_common_subexpr` is the non-`func_application` half of `func_expr`. Alternatives whose arguments are a plain comma list reuse `FunctionApplicationExpression`; those whose operands are separated by keywords, or that take no parentheses, get their own syntax node. **Keeping the source spelling is the point**: Postgres stores `DEFAULT CURRENT_TIMESTAMP` as the keyword and `DEFAULT now()` as the call, never rewriting one into the other, so folding them into one node would make one of the two re-diff on every deploy. Note a parsed construct is not automatically a *modelable* one — `PostgresDefaultValue`'s allowlist still gates which may be a column `DEFAULT`, so some constructs parse fine but stay unmodeled with an SQ1002 warning.

A typed literal (`interval '1 day'`) is a `TypedLiteralExpression` whose type name comes from the source rather than `GetText()`, which would flatten `timestamp with time zone` into one word; dropping the prefix silently is worse than throwing, since the predicate would deploy meaning something else. The escape-bearing string constants (`U&'…'`, `E'…'`, `$$…$$`) are carried verbatim as both text and value: Squill only ever reproduces a literal, and decoding one here would risk changing it.

**The grammar is a precedence ladder.** PostgreSQL's `gram.y` is flat and ambiguous, disambiguated by bison `%prec`; ANTLR has none, so grammars-v4 re-expresses it as a ladder where each tier recurses to the tier *below* it on the right. A tier that recurses to the top instead over-captures. Since the `.g4` is never hand-edited, such a defect is worked around in the visitor (`RebalanceRightOperand`, `PostgresVisitor.PrecedenceRebalance.cs`) and reported upstream, so the workaround can be deleted once fixed.

`PostgreSQLLexer.cs` and `PostgreSQLParser.cs` are **generated** from the `.g4` grammars — do not hand-edit them. Regenerate with `RegenerateAntlr.ps1 -PathToAntlrJar <jar>` (needs Java and the ANTLR jar; match the jar to the `Antlr4.Runtime.Standard` version in the `.csproj`). Adding `-Revendor` first re-downloads the `.g4` files *and* `PostgreSQLLexerBase.cs`/`PostgreSQLParserBase.cs` from grammars-v4 master, so picking up an upstream grammar fix is one command. The two `*Base.cs` are vendored verbatim but need a `namespace` (and, for the parser's, `#nullable disable` — it is not nullable-annotated and would trip `TreatWarningsAsErrors`); the script applies both idempotently so nobody has to remember them. Everything under `Squill.PostgresParser` except those four vendored files and the generated output is ours.

**Re-vendoring is not a drop-in.** Two traps in particular. A rule that used to match empty may become genuinely optional at the call site, so accessors that previously always returned a context **now return null** — the compiler will not catch this, and it surfaces as a `NullReferenceException` at parse time. And a new alternative can silently re-route something that already worked. The test suite is the safety net for both, asserting parse-tree shapes and rendered SQL rather than grammar internals. **If a re-vendor seems to require editing a test's expectations, stop**: that means observable behaviour changed, which is a bug in the port, not a test that needs updating.

### Postgres provider (`Squill.Provider.Postgres`)

Implements the Core abstractions using Npgsql. The string constants used in the generic model live here: `PostgresElementTypes`, `PostgresPropertyNames`, `PostgresRelationshipNames`, `PostgresAnnotationTypes`. `PostgresDatabaseModelBuilder` extracts a model from a live database; `PostgresDatabaseDependencyAnalyzer` encodes element dependency rules.

### MariaDB/MySQL provider (`Squill.Provider.MariaDb` + `Squill.MariaDbParser`)

A second reference provider covering **both MariaDB and MySQL** with one provider, mirroring the Postgres provider's structure over MySqlConnector, with an ANTLR-based `Squill.MariaDbParser` (generated `.cs` checked in like the Postgres parser). MariaDB differs from Postgres in having no schema/extension objects (a database *is* the schema), backtick identifiers, `AUTO_INCREMENT` instead of identity, and engine-assigned PK/FK names. It also has `SqlEvent`, which Postgres has no equivalent for. Anything whose stored form is resolved against the wall clock when it is created (an event schedule) can never round-trip and is rejected at build time rather than modeled. Integration tests run against both `mariadb:latest` and `mysql:latest`, and a scenario must pass on both.

### DACPAC (`Squill.Dacpac`)

Serializes a model to the DACPAC zip format (`Origin.xml`, `DacMetadata.xml`, `model.xml`, `[Content Types].xml`), aiming for byte-compatible output with SSDT-built DACPACs — so match DacFx's layout rather than inventing one. A few constraints are easy to get wrong: pre/post-deploy scripts are UTF-8 **with BOM** and, unlike `model.xml`, **not** checksummed in `Origin.xml`; SSDT packages carry no `_rels/.rels`, so don't add one; and we deliberately do not append DacFx's trailing `GO`, which is a T-SQL batch separator and invalid in Postgres/MariaDB. Deploy scripts are stored verbatim, never parsed into the model, and run on every deploy including one with zero schema deltas.

Also hosts the **provider-dispatch layer**: `ISquillProvider` (a host-facing provider adapter each concrete provider implements — `PostgresSquillProvider`, `MariaDbSquillProvider`), `SquillProviderRegistry` (resolves a provider by name; `MariaDb`/`MySql` → MariaDB, `Postgresql` → Postgres), and `DacpacProviderDispatch` (reads a DACPAC's recorded `ProviderName` and routes deploy/script to the matching provider). The registry is populated by the host so `Squill.Dacpac` stays free of provider references. `SquillProviderName` in a `.squillproj` selects the provider at build time and is recorded in the DACPAC for deploy-time dispatch.

Separately, `DatabaseSchemaProvider` (+ `DatabaseSchemaProviderRegistry`) identifies the **target engine and major version**, mirroring SSDT's `Sql160DatabaseSchemaProvider` types, with one reflection-discoverable subclass per supported major. Every build has one (an unspecified `TargetVersion` resolves to that engine's latest supported major), and **it is where engine capabilities belong**: code that varies by engine reads a property off the schema provider rather than branching on a provider-name string, an enum, or a type test. Put a capability on a base shared by exactly the engines it means something to (e.g. `MariaDbFamilyDatabaseSchemaProvider`), not on `DatabaseSchemaProvider`, and make it `abstract` so each engine must state its own answer.

### Entry point (`Squill`)

The console executable. Provides `build`, `deploy`, and `script` verbs (System.CommandLine). `deploy`/`script` dispatch to the right provider via `DacpacProviderDispatch` based on the DACPAC's provider name, so one CLI targets PostgreSQL or MariaDB/MySQL.
