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

**Column `DEFAULT`s** are canonicalized by `PostgresDefaultValue` / `MariaDbDefaultValue` so a default parsed from source and the same default read back from the catalog reduce to one token — otherwise every deploy would see a phantom column change. Beyond constant literals, a narrow allowlist of non-constant defaults is modeled (issue #124), and the two engines need opposite treatment: **Postgres preserves the spelling it was given** (`now()` stays `now()`, `CURRENT_TIMESTAMP` stays `CURRENT_TIMESTAMP`, never rewriting one into the other) while normalizing case, whitespace and an explicit `pg_catalog.` prefix — so each spelling maps to its *own* token. **MariaDB/MySQL collapse every synonym** (`CURRENT_TIMESTAMP`, `NOW()`, …) into one stored default but report it differently (MySQL `CURRENT_TIMESTAMP`, MariaDB `current_timestamp()`) — so all of them fold to *one* token. Anything outside the allowlist (an arbitrary call, a serial's `nextval(...)`, a fractional-seconds `CURRENT_TIMESTAMP(3)`) stays unmodeled with an SQ1002 warning rather than risking a round trip it cannot make. Two engine traps worth knowing: MySQL reports `DEFAULT_GENERATED` in `information_schema.COLUMNS.EXTRA` for an ordinary column that merely has a non-constant default, so a generated-column check must match `STORED GENERATED`/`VIRTUAL GENERATED` specifically; and the MariaDB grammar puts `ON UPDATE CURRENT_TIMESTAMP` inside the same `defaultValue` production as the default itself, so its parts must be read separately rather than via the rule's flattened text. Note that grammar rule (`currentTimestamp`) is **looser than the engines**: it admits `LOCALTIME`, `LOCALTIMESTAMP`, `CURDATE` and `CURTIME`, which are *not* current-timestamp synonyms — MariaDB stores `DEFAULT LOCALTIME` as `curtime()` — and which both engines reject outright in `ON UPDATE` position. Canonicalization decisions here must be measured against a live server, never inferred from the grammar, or a default that cannot round-trip gets modeled and re-diffs on every deploy. A signed numeric default (issue #139) is a case in point: Postgres stores the two signs in *different* shapes — `DEFAULT -5` becomes the cast `'-5'::integer`, but `DEFAULT +5` becomes the parenthesized, space-separated `(+ 5)` — so `FromDatabaseText` handles both spellings before the cast strip.

### PostgreSQL parser (`Squill.PostgresParser`)

ANTLR4-based parser producing a typed syntax tree (the `Syntax/` classes, e.g. `CreateTableStatement`, `ColumnDefinition`). `PostgresVisitor` is a large partial class split across one file per grammar rule (`PostgresVisitor.Createstmt.cs`, etc.) that maps the ANTLR parse tree to the syntax model. `CaseChangingCharStream` handles case-insensitive keywords.

`func_expr_common_subexpr` (issue #140) is the non-`func_application` half of `func_expr`, and covers a large slice of everyday SQL in every expression position. Its alternatives split two ways: those whose arguments are a plain comma list (`COALESCE`, `NULLIF`, `GREATEST`, `LEAST`, `XMLCONCAT`) reuse `FunctionApplicationExpression`, while the rest get their own syntax node because their operands are separated by keywords (`ExtractExpression`, `SubstringExpression`, `TrimExpression`, `PositionExpression`, `OverlayExpression`, `NormalizeExpression`, `CollationForExpression`) or take no parentheses at all (`KeywordExpression` for `CURRENT_TIMESTAMP`, `CURRENT_USER`, …; `CastExpression` for `CAST`/`TREAT`, kept distinct from the `::` `TypecastExpression`). Keeping the spelling is the point, not pedantry: Postgres stores `DEFAULT CURRENT_TIMESTAMP` as the keyword and `DEFAULT now()` as the call, never rewriting one into the other, so folding them into one node would make one of the two re-diff on every deploy. `SUBSTRING` is the one alternative that lands in both camps — the keyword form `SUBSTRING(s FROM a FOR b)` becomes a `SubstringExpression`, the comma form `SUBSTRING(s, a, b)` an ordinary call. The XML constructors beyond `XMLCONCAT` (`XMLELEMENT`, `XMLPARSE`, `XMLROOT`, …) each have bespoke syntax and remain unimplemented. Note a parsed construct is not automatically a *modelable* one: `PostgresDefaultValue`'s allowlist still gates which of these may be a column `DEFAULT`, so `CURRENT_TIMESTAMP(3)` (precision, unverified round trip), `LOCALTIME` and `CURRENT_USER` parse fine but stay unmodeled with an SQ1002 warning.

`PostgreSQLLexer.cs` and `PostgreSQLParser.cs` are **generated** from the `.g4` grammars — do not hand-edit them. Regenerate with `RegenerateAntlr.ps1` (needs Java and the ANTLR jar). Parser tests use the Sakila sample schema (BSD-licensed, see `Squill.PostgresParser.Tests/README.md`).

### Postgres provider (`Squill.Provider.Postgres`)

Implements the Core abstractions using Npgsql. The string constants used in the generic model live here: `PostgresElementTypes`, `PostgresPropertyNames`, `PostgresRelationshipNames`, `PostgresAnnotationTypes`. `PostgresDatabaseModelBuilder` extracts a model from a live database; `PostgresDatabaseDependencyAnalyzer` encodes element dependency rules.

### MariaDB/MySQL provider (`Squill.Provider.MariaDb` + `Squill.MariaDbParser`)

A second reference provider (issue #12), covering **both MariaDB and MySQL** with one provider. Mirrors the Postgres provider's structure (`MariaDbDatabaseModelBuilder`, `MariaDbScriptGenerator`, `MariaDbTableDiffAnalyzer`, `MariaDbDatabaseDependencyAnalyzer`, `MariaDbModelFactory`, its own `SqlName`/constants) over MySqlConnector, with an ANTLR-based `Squill.MariaDbParser` (grammars-v4 MariaDB grammar, generated `.cs` checked in like the Postgres parser). MariaDB differences from Postgres: no schema/extension objects (a database *is* the schema), backtick identifiers, `AUTO_INCREMENT` instead of identity, and PK/FK naming (`PRIMARY`, `<table>_ibfk_N`). It also has one element type Postgres has no equivalent for: `SqlEvent`, a `CREATE EVENT` scheduled routine (issue #122). Because both engines resolve a schedule against the wall clock *when the event is created* and store only the resulting absolute timestamps, schedule forms that are not already constant can never round-trip and are rejected at build time — a recurring event with no `STARTS` (the catalog synthesizes one from "now"), and any non-constant `AT`/`STARTS`/`ENDS` such as `CURRENT_TIMESTAMP + INTERVAL 1 DAY`. Two catalog quirks are normalized on extraction: a compound `INTERVAL_VALUE` is stored *with its quotes* (`'2 3'`), and `DISABLE ON SLAVE` reports as `SLAVESIDE_DISABLED` on MariaDB but `REPLICA_SIDE_DISABLED` on MySQL. Integration tests run against both `mariadb:latest` and `mysql:latest` containers.

### DACPAC (`Squill.Dacpac`)

Early scaffolding for serializing a model to the DACPAC zip format (`Origin.xml`, `DacMetadata.xml`, `model.xml`, `[Content Types].xml`), with the goal of byte-compatible output with SSDT-built DACPACs. Optional `predeploy.sql` / `postdeploy.sql` parts carry pre/post-deployment scripts (issue #67), matching how DacFx lays them out: root-level, lowercase, UTF-8 **with BOM**, declared by the unconditional `Default Extension="sql"` (`text/plain`) in `[Content Types].xml`, and — unlike `model.xml` — **not** checksummed in `Origin.xml`. SSDT packages carry no `_rels/.rels`; parts are found by fixed name, so don't add one. Scripts are stored verbatim (never parsed into the model), surfaced as `ModelMetadata.PreDeployScript`/`PostDeployScript`, and composed around the schema diff by `DeploymentScripts.Compose`. They run on every deploy, including one with zero schema deltas. Note DacFx appends a trailing `GO` to each script; we deliberately do not, as `GO` is a T-SQL batch separator and invalid in Postgres/MariaDB. The XML writers/readers (`ModelXmlWriter`, `ModelXmlReader`, `OriginXml`, `ContentTypesXml`, `DacMetadataXml`) live in the `Squill.Dacpac` project.

Also hosts the **provider-dispatch layer**: `ISquillProvider` (a host-facing provider adapter each concrete provider implements — `PostgresSquillProvider`, `MariaDbSquillProvider`), `SquillProviderRegistry` (resolves a provider by name; `MariaDb`/`MySql` → MariaDB, `Postgresql` → Postgres), and `DacpacProviderDispatch` (reads a DACPAC's recorded `ProviderName` and routes deploy/script to the matching provider). The registry is populated by the host so `Squill.Dacpac` stays free of provider references. `SquillProviderName` in a `.squillproj` selects the provider at build time and is recorded in the DACPAC for deploy-time dispatch.

### Entry point (`Squill`)

The console executable. Provides `build`, `deploy`, and `script` verbs (System.CommandLine). `deploy`/`script` dispatch to the right provider via `DacpacProviderDispatch` based on the DACPAC's provider name, so one CLI targets PostgreSQL or MariaDB/MySQL.
