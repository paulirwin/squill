# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Squill Is

A declarative, cross-platform, database-independent SQL deployment tool inspired by (and aiming for compatibility with) SSDT/DACPAC, targeting non-SQL-Server databases — PostgreSQL first. Users express schema as `CREATE` statements in `.sql` files; Squill builds an environment-neutral DACPAC from them, then deploys by diffing the DACPAC model against the target database and scripting the changes. See README.md for the full motivation and design.

## Commands

Requires the .NET 10 SDK (all projects target `net10.0`).

- Build: `dotnet build Squill.sln`
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

Everything revolves around a generic, database-agnostic `Model`: a list of `Element`s (typed by string, e.g. `"SqlTable"`, mirroring SSDT element type names for DACPAC compatibility), each holding `Property`s, `Relationship`s (links to other elements or entries), and `Annotation`s. Models are compared with a Merkle-tree approach: every node implements `IHashable` and `HashUtility` computes SHA256 hashes bottom-up, so equal top-level hashes short-circuit the diff. `SchemaCompare` produces a `SchemaComparison` of deltas — currently only `CreateDelta` (top-level object existence); ALTER and DROP throw `NotImplementedException`.

Database specifics are abstracted behind `IDatabaseProvider` / `IDatabase` / `IDatabaseModelBuilder` / `IDatabaseDependencyAnalyzer` (the dependency analyzer handles things like primary keys being dependent on their table, so dependent elements are attached to their parent's delta). A `Workspace` is just the set of source `IFile`s.

There are two strategies for turning a workspace of SQL files into a `Model`:

1. `TemporaryDatabaseModelBuilder` (Squill.Core) — runs the scripts against a temporary database created by the provider, then extracts the model from that database. Requires a live server and ordered scripts.
2. `ParserWorkspaceModelBuilder` (Squill.Provider.Postgres) — parses the SQL text directly with the ANTLR-based parser, no database needed. This is the direction the project is moving.

### PostgreSQL parser (`Squill.PostgresParser`)

ANTLR4-based parser producing a typed syntax tree (the `Syntax/` classes, e.g. `CreateTableStatement`, `ColumnDefinition`). `PostgresVisitor` is a large partial class split across one file per grammar rule (`PostgresVisitor.Createstmt.cs`, etc.) that maps the ANTLR parse tree to the syntax model. `CaseChangingCharStream` handles case-insensitive keywords.

`PostgreSQLLexer.cs` and `PostgreSQLParser.cs` are **generated** from the `.g4` grammars — do not hand-edit them. Regenerate with `RegenerateAntlr.ps1` (needs Java and the ANTLR jar). Parser tests use the Sakila sample schema (BSD-licensed, see `Squill.PostgresParser.Tests/README.md`).

### Postgres provider (`Squill.Provider.Postgres`)

Implements the Core abstractions using Npgsql. The string constants used in the generic model live here: `PostgresElementTypes`, `PostgresPropertyNames`, `PostgresRelationshipNames`, `PostgresAnnotationTypes`. `PostgresDatabaseModelBuilder` extracts a model from a live database; `PostgresDatabaseDependencyAnalyzer` encodes element dependency rules.

### DACPAC (`Squill.Dacpac`)

Early scaffolding for serializing a model to the DACPAC zip format (`Origin.xml`, `DacMetadata.xml`, `model.xml`, `[Content Types].xml`), with the goal of byte-compatible output with SSDT-built DACPACs. Note the XML writer prototypes (`ModelWriter`, `OriginWriter`, etc.) currently live in `Squill.Dacpac.Tests` — this is work in progress.

### Entry point (`Squill`)

The console executable; currently a stub that prints the version.
