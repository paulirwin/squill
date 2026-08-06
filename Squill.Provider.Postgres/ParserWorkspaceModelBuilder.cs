using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.PostgresParser.Syntax;

namespace Squill.Provider.Postgres;

public class ParserWorkspaceModelBuilder : IWorkspaceModelBuilder
{
    private readonly Workspace _workspace;
    private readonly IPostgresParser _postgresParser;
    private readonly PostgresqlDatabaseSchemaProvider _schemaProvider;

    /// <summary>
    /// Builds a model from PostgreSQL source. The target
    /// <see cref="PostgresqlDatabaseSchemaProvider"/> is what makes the build version-aware:
    /// constructs introduced after the targeted major are reported at build time rather than
    /// failing partway through a deploy (issue #142).
    ///
    /// <para>
    /// It defaults to the latest supported major rather than being required, which is what
    /// declaring no <c>SquillTargetVersion</c> means — an unconstrained project behaves as if
    /// it targets a current server, so nothing is reported as too new. This mirrors
    /// <see cref="Squill.Dacpac.DatabaseSchemaProviderRegistry.Resolve(string, int?)"/>, whose
    /// null case resolves the same way.
    /// </para>
    /// </summary>
    public ParserWorkspaceModelBuilder(
        Workspace workspace,
        IPostgresParser postgresParser,
        PostgresqlDatabaseSchemaProvider? schemaProvider = null)
    {
        _workspace = workspace;
        _postgresParser = postgresParser;
        // Resolved rather than named so adding a new major does not leave a stale default
        // behind here — the registry already knows which is latest.
        _schemaProvider = schemaProvider
            ?? (PostgresqlDatabaseSchemaProvider)DatabaseSchemaProviderRegistry
                .ResolveLatest("Postgresql");
    }

    public async Task<BuildResult> ExtractModelAsync(CancellationToken cancellationToken = default)
    {
        var model = new Model();
        var validator = new SourceValidator();
        var warnings = new List<SqlSourceDiagnostic>();
        var views = new List<PendingView>();

        foreach (var file in _workspace.Files.Where(i => i.Kind == FileKind.Compile))
        {
            await ProcessFile(file, model, validator, warnings, views, cancellationToken);
        }

        // Views are built only once every table is known: expanding a SELECT * needs the
        // referenced table's columns, which may be declared in a later file. This runs
        // before validation so a broken view is reported alongside every other source
        // error rather than on a later rebuild (issue #61).
        AddViews(model, validator, views);

        // Validated after every file so declaration order (within and across files) does
        // not matter, just like it doesn't for the deployed schema. Parse and mapping
        // errors collected above are reported together with the reference errors, so one
        // build surfaces every problem rather than one per rebuild (issue #61).
        validator.ThrowIfInvalid();

        MoveRoutinesToEnd(model);

        return new BuildResult(model, warnings);
    }

    // A view whose element cannot be built until every table in the workspace has been
    // seen, kept with the file and position to report any failure against.
    private sealed record PendingView(IFile File, CreateViewStatement Statement);

    /// <summary>
    /// Builds every view, once every table in the workspace is known — expanding a
    /// SELECT * needs the referenced table's columns, which may be declared in a later file.
    ///
    /// A view that cannot be built is recorded on the validator rather than thrown, so a
    /// build reports every broken view at once alongside the other source errors (issue #61).
    /// </summary>
    private static void AddViews(Model model, SourceValidator validator, List<PendingView> views)
    {
        // Ordinal, and after every other element: the database-extraction builder reads
        // views last, in catalog order, and the Merkle hash is order-sensitive.
        foreach (var view in views
                     .OrderBy(i => SplitSchema(i.Statement.Name).Schema, StringComparer.Ordinal)
                     .ThenBy(i => SplitSchema(i.Statement.Name).Name.UnqualifiedName, StringComparer.Ordinal))
        {
            try
            {
                model.Elements.Add(MakeCreateViewElement(view.Statement, validator));
            }
            catch (Exception ex) when (ex is NotImplementedException or NotSupportedException
                or InvalidOperationException)
            {
                validator.AddError(new SqlSourceException(
                    ex.Message, view.File.Name, view.Statement.Line, view.Statement.Column,
                    SqlSourceException.UnresolvedReference, ex));
            }
        }
    }

    /// <summary>
    /// Parses one file and maps its statements into the model. A syntax error aborts only
    /// this file — it is recorded and the remaining files are still parsed, so a build
    /// reports every broken file at once. A statement that cannot be mapped is likewise
    /// recorded and the rest of the file continues, so multiple unsupported statements are
    /// all reported (issue #61).
    /// </summary>
    private async Task ProcessFile(IFile file,
        Model model,
        SourceValidator validator,
        List<SqlSourceDiagnostic> warnings,
        List<PendingView> views,
        CancellationToken cancellationToken)
    {
        var text = await file.ReadAllTextAsync(cancellationToken);

        Root root;
        try
        {
            root = _postgresParser.Parse(text);
        }
        catch (PostgresParseException ex)
        {
            validator.AddError(new SqlSourceException(
                ex.Message, file.Name, ex.Line, ex.Column, innerException: ex));

            // The file did not parse, so it contributes no statements; carry on with the
            // next file rather than aborting the whole build here.
            return;
        }
        catch (NotImplementedException ex)
        {
            // A construct the grammar accepts but the visitor cannot map throws from inside
            // Parse, before there is a statement to anchor to — so the file is all the position
            // there is. Reporting it as SQ0001 rather than letting it escape is what keeps an
            // unsupported construct a build diagnostic instead of a raw stack trace (#159).
            validator.AddError(new SqlSourceException(ex.Message, file.Name, innerException: ex));

            return;
        }

        foreach (var statement in root.Statements)
        {
            try
            {
                ProcessStatement(
                    statement, model, file, validator, warnings, views, _schemaProvider);
            }
            catch (SqlSourceException ex)
            {
                // Already source-anchored and carrying its own code (SQ0006 for an imperative
                // statement); recorded as-is so the rest of the file is still reported rather
                // than the first one aborting the build.
                validator.AddError(ex);
            }
            catch (Exception ex) when (ex is NotImplementedException or NotSupportedException
                or InvalidOperationException or PostgresParseException)
            {
                // Attach the source file and the statement's position so the host can
                // report the failure as a diagnostic pointing at the offending statement.
                validator.AddError(new SqlSourceException(
                    ex.Message, file.Name, statement.Line, statement.Column, innerException: ex));
            }
        }
    }

    /// <summary>
    /// Moves procedures after every other element, ordered by schema, name and argument
    /// types. The database-extraction builder emits them last in exactly that order (the
    /// catalog has no notion of the order they were declared in), and the Merkle hash is
    /// order-sensitive — so a parsed model must adopt the same order to hash-match one
    /// extracted from a live database. Ordering them last also matches the create order,
    /// since a procedure body may reference any table.
    /// </summary>
    // Functions, procedures then aggregates are moved to the end of the model, in that
    // order — matching the DB-extraction builder, which extracts them in the same order
    // after everything else. Aggregates come last because they reference a state function,
    // so the function must already exist. The Merkle hash is order-sensitive, so the two
    // builders must agree. Within each kind, ordinal ordering matches the database's
    // byte-wise (COLLATE "C") ordering of the same values.
    private static void MoveRoutinesToEnd(Model model)
    {
        MoveRoutineKindToEnd(model, PostgresElementTypes.SqlFunction);
        MoveRoutineKindToEnd(model, PostgresElementTypes.SqlProcedure);
        MoveRoutineKindToEnd(model, PostgresElementTypes.SqlAggregate);

        // Triggers come last of all: one depends on both its table and the function it runs,
        // so it must be created after every table and routine. The DB-extraction builder
        // likewise emits triggers last, in this same order, so the order-sensitive Merkle
        // hash agrees between the two builders.
        MoveTriggersToEnd(model);
    }

    // Triggers are moved to the end, ordered by schema, then table, then trigger name —
    // matching the DB-extraction builder's ordering (COLLATE "C" on the same values) so a
    // parsed model hash-matches an extracted one.
    private static void MoveTriggersToEnd(Model model)
    {
        var triggers = model.Elements
            .Where(i => i.Type == PostgresElementTypes.SqlTrigger)
            .ToList();

        if (triggers.Count == 0)
        {
            return;
        }

        foreach (var trigger in triggers)
        {
            model.Elements.Remove(trigger);
        }

        foreach (var trigger in triggers
                     .OrderBy(i => PostgresModelFactory.GetSchema(i), StringComparer.Ordinal)
                     .ThenBy(i => TriggerTableName(i), StringComparer.Ordinal)
                     .ThenBy(i => i.GetProperty<string>(PostgresPropertyNames.RoutineName), StringComparer.Ordinal))
        {
            model.Elements.Add(trigger);
        }
    }

    // The bare name of the table a trigger fires on, read from its TriggerTable relationship.
    private static string TriggerTableName(Element trigger)
    {
        var reference = trigger.GetRelationship(PostgresRelationshipNames.TriggerTable)
            ?.Entries.OfType<Reference>().FirstOrDefault();

        // The reference name may be schema-qualified (schema.table); the DB builder orders by
        // the bare table name, so take the last segment.
        var name = reference?.Name ?? string.Empty;
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    private static void MoveRoutineKindToEnd(Model model, string elementType)
    {
        var routines = model.Elements
            .Where(i => i.Type == elementType)
            .ToList();

        if (routines.Count == 0)
        {
            return;
        }

        foreach (var routine in routines)
        {
            model.Elements.Remove(routine);
        }

        foreach (var routine in routines
                     .OrderBy(i => PostgresModelFactory.GetSchema(i), StringComparer.Ordinal)
                     .ThenBy(i => i.GetProperty<string>(PostgresPropertyNames.RoutineName), StringComparer.Ordinal)
                     .ThenBy(i => i.GetProperty<string>(PostgresPropertyNames.ArgumentTypes), StringComparer.Ordinal))
        {
            model.Elements.Add(routine);
        }
    }

    private static void ProcessStatement(Statement statement,
        Model model,
        IFile file,
        SourceValidator validator,
        List<SqlSourceDiagnostic> warnings,
        List<PendingView> views,
        PostgresqlDatabaseSchemaProvider schemaProvider)
    {
        if (statement is CreateTableStatement createTableStatement)
        {
            // A typed table (OF a_type) and a partition child (PARTITION OF parent) declare no
            // column set of their own — it belongs to the type or the parent, neither of which
            // the model can express. So the table is not modeled at all, and is not registered
            // with the validator either: a columnless table would make every reference to it
            // look unresolved (issue #143).
            if (createTableStatement.OfType is not null || createTableStatement.PartitionOf is not null)
            {
                AddUnmodeledTableStatementWarning(file, createTableStatement, warnings);
                return;
            }

            // A partitioned parent *does* declare its own columns, so unlike the two forms
            // above it would model and deploy quite happily — as an ordinary, unpartitioned
            // table. Silently deploying different semantics than the source declares is the
            // failure mode #141 called out for typed literals, and a warning is not enough
            // here: partitioning is the whole point of the declaration. Rejected until
            // partitioning is modeled properly (issue #143).
            if (createTableStatement.PartitionBy is not null)
            {
                throw new NotSupportedException(
                    $"PARTITION BY on table '{SplitSchema(createTableStatement.Name).Name.UnqualifiedName}' "
                    + "is not yet supported: Squill cannot model partitioning, and deploying the "
                    + "table unpartitioned would not match what is declared.");
            }

            validator.AddCreateTable(file, createTableStatement);

            // A duplicate table would otherwise contribute a second set of elements for the
            // same object; the build fails on the SQ0003 error, but skipping the elements
            // keeps the half-built model coherent for anything that inspects it.
            if (validator.IsDuplicateTable(createTableStatement))
            {
                return;
            }

            AddUnmodeledTableWarnings(file, createTableStatement, warnings);

            AddTableTargetVersionWarnings(file, createTableStatement, schemaProvider, warnings);

            foreach (var element in MakeCreateTableElements(createTableStatement))
            {
                model.Elements.Add(element);
            }
        }
        else if (statement is CreateIndexStatement createIndexStatement)
        {
            validator.AddCreateIndex(file, createIndexStatement);

            // Reported alongside the model rather than instead of it: the index is still built
            // as declared, because dropping NULLS NOT DISTINCT would deploy the opposite
            // uniqueness semantics from the source's (issue #142).
            var indexTable = SplitSchema(createIndexStatement.OnRelation.Name).Name.UnqualifiedName;

            PostgresTargetVersionChecker.Check(
                file,
                createIndexStatement,
                indexTable,
                schemaProvider,
                warnings);

            // A partial index's predicate is one of the three places an arbitrary expression
            // reaches the model, so it is one of the three places a too-new literal can hide.
            if (createIndexStatement.WhereClause is { } indexPredicate)
            {
                PostgresTargetVersionChecker.CheckExpression(
                    file,
                    indexPredicate,
                    $"The predicate of an index on '{indexTable}'",
                    schemaProvider,
                    warnings);
            }

            var element = MakeCreateIndexElement(createIndexStatement);

            model.Elements.Add(element);
        }
        else if (statement is CreateExtensionStatement createExtensionStatement)
        {
            // CASCADE is carried through and re-emitted on deploy (issue #143) rather than
            // warned about: dropping it would build cleanly and then fail on deploy, since the
            // extension's dependency would be missing. It is deliberately not part of the
            // element's identity — see PostgresPropertyNames.Cascade.
            //
            // FROM is different: it upgrades a pre-9.1 unpackaged module into an extension, so
            // it only means anything against a database that already contains that module. It
            // cannot be reproduced from a declarative model and stays unmodeled.
            if (createExtensionStatement.FromVersion is { } fromVersion)
            {
                warnings.Add(new SqlSourceDiagnostic(
                    $"FROM '{fromVersion}' on extension '{createExtensionStatement.Name.Name}' is not "
                    + "modeled; the extension will be created directly rather than upgraded from an "
                    + "unpackaged module.",
                    file.Name, statement.Line, statement.Column));
            }

            var element = MakeCreateExtensionElement(createExtensionStatement);

            model.Elements.Add(element);
        }
        else if (statement is CreateSchemaStatement createSchemaStatement)
        {
            // The schema is modeled; only its owning role is not, since Squill does not
            // manage roles (issue #143). The role may be the token CURRENT_USER or
            // SESSION_USER when the schema was named explicitly (issue #166); it is reported
            // as written so the warning names exactly what was dropped.
            //
            // "the deploying role" is deliberately the wording for all three cases, because
            // dropping the clause is not equally harmless across them. Measured on
            // postgres:latest under SET ROLE: AUTHORIZATION CURRENT_USER matches what a bare
            // CREATE SCHEMA already does, so dropping it is a true no-op — but SESSION_USER
            // resolves to the *session* role, which differs from the current one under SET
            // ROLE or a SECURITY DEFINER context, so dropping that one really does change the
            // owner. Both are unmodeled either way, as a named role has been since #143.
            if (createSchemaStatement.Authorization is { } role)
            {
                warnings.Add(new SqlSourceDiagnostic(
                    $"AUTHORIZATION {role} on schema '{createSchemaStatement.Name.Name}' is not "
                    + "modeled; the schema will be owned by the deploying role.",
                    file.Name, statement.Line, statement.Column));
            }

            validator.AddSchema(createSchemaStatement.Name.Name);

            // 'public' exists in every database by default and is not a declared
            // object, so a CREATE SCHEMA public is ignored — matching the DB-extraction
            // builder, which never emits a SqlSchema for public. Otherwise the two
            // models would never agree and a redeploy would never converge.
            if (!string.Equals(createSchemaStatement.Name.Name, "public", StringComparison.Ordinal))
            {
                model.Elements.Add(
                    PostgresModelFactory.CreateSchema(SqlName.Object(createSchemaStatement.Name.Name)));
            }
        }
        else if (statement is CreateProcedureStatement createProcedureStatement)
        {
            validator.AddCreateProcedure(file, createProcedureStatement);

            if (validator.IsDuplicateProcedure(createProcedureStatement))
            {
                return;
            }

            model.Elements.Add(MakeCreateProcedureElement(createProcedureStatement));
        }
        else if (statement is CreateViewStatement createViewStatement)
        {
            validator.AddCreateView(file, createViewStatement);

            // Held back until every file has been read: a SELECT * needs the referenced
            // table's columns, and that table may be declared later.
            views.Add(new PendingView(file, createViewStatement));
        }
        else if (statement is CreateEnumTypeStatement createEnumTypeStatement)
        {
            model.Elements.Add(MakeCreateEnumTypeElement(createEnumTypeStatement));
        }
        else if (statement is CreateDomainStatement createDomainStatement)
        {
            model.Elements.Add(MakeCreateDomainElement(createDomainStatement));
        }
        else if (statement is CreateSequenceStatement createSequenceStatement)
        {
            model.Elements.Add(MakeCreateSequenceElement(createSequenceStatement));
        }
        else if (statement is CreateCompositeTypeStatement createCompositeTypeStatement)
        {
            model.Elements.Add(MakeCreateCompositeTypeElement(createCompositeTypeStatement));
        }
        else if (statement is CreateRangeTypeStatement createRangeTypeStatement)
        {
            model.Elements.Add(MakeCreateRangeTypeElement(createRangeTypeStatement));
        }
        else if (statement is CreateCollationStatement createCollationStatement)
        {
            model.Elements.Add(MakeCreateCollationElement(createCollationStatement));
        }
        else if (statement is CreateFunctionStatement createFunctionStatement)
        {
            validator.AddCreateFunction(file, createFunctionStatement);

            if (validator.IsDuplicateFunction(createFunctionStatement))
            {
                return;
            }

            model.Elements.Add(MakeCreateFunctionElement(createFunctionStatement));
        }
        else if (statement is CreateAggregateStatement createAggregateStatement)
        {
            model.Elements.Add(MakeCreateAggregateElement(createAggregateStatement));
        }
        else if (statement is CreateTriggerStatement createTriggerStatement)
        {
            validator.AddCreateTrigger(file, createTriggerStatement);

            model.Elements.Add(MakeCreateTriggerElement(createTriggerStatement));
        }
        else if (statement is ImperativeStatement imperativeStatement)
        {
            // An authored ALTER/DROP/DML is a mistake in the source, not a gap in Squill, so it
            // gets its own error rather than the "not yet implemented" below — which reads as a
            // missing capability and invites the author to wait for it (issue #125).
            throw ImperativeStatementDiagnostic.Exception(
                imperativeStatement.Name,
                ToDiagnosticKind(imperativeStatement.Kind),
                file.Name,
                statement.Line,
                statement.Column);
        }
        else
        {
            throw new NotImplementedException(
                $"Statement type {statement.GetType()} to Element transformation not yet implemented");
        }
    }

    // The parsers deliberately do not reference Squill.Core, so each carries its own copy of
    // this distinction; the provider, which bridges the two, maps one to the other.
    private static ImperativeStatementKind ToDiagnosticKind(ImperativeKind kind) => kind switch
    {
        ImperativeKind.DataChange => ImperativeStatementKind.DataChange,
        ImperativeKind.Query => ImperativeStatementKind.Query,
        _ => ImperativeStatementKind.SchemaChange,
    };

    /// <summary>
    /// Records a warning for every construct in a CREATE TABLE that is recognized but not
    /// carried into the model, so a declared-but-unmodeled construct is visible rather than
    /// silently absent from the DACPAC (issue #61). Currently: column defaults that are
    /// neither constant literals nor allowlisted function defaults (an arbitrary call like
    /// <c>some_fn(1)</c>, or <c>DEFAULT NULL</c>) — see <see cref="PostgresDefaultValue"/>.
    /// CHECK constraints were listed here until issue #120 modeled them, and non-constant
    /// defaults such as <c>now()</c> until issue #124 modeled them.
    /// </summary>
    /// <summary>
    /// The warning for a CREATE TABLE that is not modeled *at all* — the typed-table
    /// (<c>OF a_type</c>) and partition-child (<c>PARTITION OF parent</c>) forms, whose column
    /// set belongs to another object (issue #143). Unlike the per-construct warnings below,
    /// this one stands in for the entire table.
    /// </summary>
    private static void AddUnmodeledTableStatementWarning(IFile file,
        CreateTableStatement createTableStatement,
        List<SqlSourceDiagnostic> warnings)
    {
        var table = SplitSchema(createTableStatement.Name).Name.UnqualifiedName;

        var reason = createTableStatement.PartitionOf is { } parent
            ? $"is declared PARTITION OF '{parent}'"
            : $"is declared OF type '{createTableStatement.OfType}'";

        warnings.Add(new SqlSourceDiagnostic(
            $"Table '{table}' {reason} and is not modeled; it takes its columns from that "
            + "object, which Squill cannot express, so it will not be deployed or compared.",
            file.Name,
            createTableStatement.Line,
            createTableStatement.Column));
    }

    private static void AddUnmodeledTableWarnings(IFile file,
        CreateTableStatement createTableStatement,
        List<SqlSourceDiagnostic> warnings)
    {
        var table = SplitSchema(createTableStatement.Name).Name.UnqualifiedName;

        foreach (var tableConstraint in createTableStatement.Elements.OfType<TableConstraint>())
        {
            var constraint = tableConstraint is NamedTableConstraint named
                ? named.Constraint
                : tableConstraint;

            // PRIMARY KEY / UNIQUE USING INDEX names an existing index to promote. The model
            // cannot bind a constraint to one specific index, so the constraint is dropped.
            var usingIndex = constraint switch
            {
                PrimaryKeyTableConstraint { UsingIndex: { } ix } => ix.Name,
                UniqueTableConstraint { UsingIndex: { } ix } => ix.Name,
                _ => null,
            };

            if (usingIndex is not null)
            {
                warnings.Add(new SqlSourceDiagnostic(
                    $"A USING INDEX constraint on table '{table}' (backed by index "
                    + $"'{usingIndex}') is not modeled; it will not be deployed or compared.",
                    file.Name,
                    constraint.Line ?? createTableStatement.Line,
                    constraint.Column ?? createTableStatement.Column));
            }
        }

        foreach (var columnDefinition in createTableStatement.Elements.OfType<ColumnDefinition>())
        {
            foreach (var columnConstraint in columnDefinition.Constraints)
            {
                var constraint = columnConstraint is NamedColumnConstraint named
                    ? named.Constraint
                    : columnConstraint;

                // Only a non-constant default is unmodeled; a constant literal round-trips
                // fine and must not warn.
                if (constraint is DefaultColumnConstraint defaultConstraint
                    && PostgresDefaultValue.FromExpression(defaultConstraint.Expression) is null)
                {
                    warnings.Add(new SqlSourceDiagnostic(
                        $"DEFAULT on column '{table}.{columnDefinition.Name.Name}' is not a "
                        + "constant literal and is not modeled; it will not be deployed or compared.",
                        file.Name,
                        constraint.Line ?? createTableStatement.Line,
                        constraint.Column ?? createTableStatement.Column));
                }
            }
        }
    }

    /// <summary>
    /// Reports constructs in a table's expressions that the target major does not accept
    /// (issue #191). A table lets an arbitrary expression through in three places — a column
    /// <c>DEFAULT</c>, a <c>CHECK</c> predicate (either column- or table-level), and a generated
    /// column's generation expression — and all three are checked here. The fourth, an index
    /// predicate, is checked where <c>CREATE INDEX</c> is handled.
    ///
    /// <para>
    /// A view body would be a fifth, but this builder does not parse one into expressions yet: it
    /// refuses the <c>SELECT</c> outright. When that changes, the view body has to be added here,
    /// because a definition is stored as written.
    /// </para>
    /// </summary>
    private static void AddTableTargetVersionWarnings(IFile file,
        CreateTableStatement createTableStatement,
        PostgresqlDatabaseSchemaProvider schemaProvider,
        List<SqlSourceDiagnostic> warnings)
    {
        var table = SplitSchema(createTableStatement.Name).Name.UnqualifiedName;

        foreach (var tableConstraint in createTableStatement.Elements.OfType<TableConstraint>())
        {
            var constraint = tableConstraint is NamedTableConstraint named
                ? named.Constraint
                : tableConstraint;

            if (constraint is CheckTableConstraint check)
            {
                PostgresTargetVersionChecker.CheckExpression(
                    file,
                    check.Expression,
                    $"A CHECK constraint on table '{table}'",
                    schemaProvider,
                    warnings);
            }
        }

        foreach (var columnDefinition in createTableStatement.Elements.OfType<ColumnDefinition>())
        {
            var column = $"{table}.{columnDefinition.Name.Name}";

            // Raised here rather than where the type specifier is built, because that helper is
            // shared with domains and composite-type attributes and does not know a column name.
            if (columnDefinition.DataType is BuiltInDataType builtIn && HasNegativeScale(builtIn))
            {
                throw new NotSupportedException(NegativeScaleMessage(column));
            }

            // A separate axis from the version checks around it: a deprecated construct is
            // accepted by every supported major, so no target version is at fault and raising
            // one would not resolve it (issue #190).
            PostgresDeprecationChecker.CheckColumn(file, columnDefinition, column, warnings);

            foreach (var columnConstraint in columnDefinition.Constraints)
            {
                var constraint = columnConstraint is NamedColumnConstraint named
                    ? named.Constraint
                    : columnConstraint;

                switch (constraint)
                {
                    case DefaultColumnConstraint defaultConstraint:
                        PostgresTargetVersionChecker.CheckExpression(
                            file,
                            defaultConstraint.Expression,
                            $"The DEFAULT on column '{column}'",
                            schemaProvider,
                            warnings);
                        break;

                    case CheckColumnConstraint checkConstraint:
                        PostgresTargetVersionChecker.CheckExpression(
                            file,
                            checkConstraint.Expression,
                            $"The CHECK constraint on column '{column}'",
                            schemaProvider,
                            warnings);
                        break;

                    // A generation expression matters more here than a DEFAULT, not less. A
                    // DEFAULT is canonicalized to the value the engine stores on its way into the
                    // model, so a non-decimal literal is already decimal by the time it is
                    // deployed. A generation expression is carried back out verbatim, so a
                    // too-new literal in one really would reach the server as written.
                    case GeneratedColumnConstraint generatedConstraint:
                        PostgresTargetVersionChecker.CheckExpression(
                            file,
                            generatedConstraint.Expression,
                            $"The generation expression for column '{column}'",
                            schemaProvider,
                            warnings);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Validates that everything the source references is defined in the project — like
    /// SSDT, an unresolved reference is a build error reported at the referencing
    /// construct's source position. Own-table checks (constraint columns, FK shape) are
    /// made as statements are added; cross-object checks (referenced tables/columns,
    /// schemas) are deferred to <see cref="ThrowIfInvalid"/> so declaration order, within
    /// and across files, does not matter. Every error is reported, not just the first.
    /// </summary>
    // Postgres tables live in a schema, so they are keyed by a (schema, table) tuple, both
    // lower-cased via TableKey. The shared validation core lives in SourceValidatorBase; this
    // subclass adds Postgres's schema-as-declared-object tracking (issue #37), routine
    // overloading, and the per-schema constraint/index name namespace.
    private sealed class SourceValidator : SourceValidatorBase<(string Schema, string Table)>
    {
        private readonly HashSet<string> _declaredSchemas = new(StringComparer.OrdinalIgnoreCase) { "public" };
        private readonly List<SchemaReference> _schemaReferences = [];

        // Where each constraint/routine was first defined, so a redefinition can name the
        // original's file and line rather than just saying "already defined".
        private readonly Dictionary<(string Schema, string Name), Origin> _constraintOrigins = new();
        private readonly Dictionary<(string Schema, string Name, string Args), Origin> _procedureOrigins = new();

        // NOTE on unique column sets (tracked by the base): PRIMARY KEY, UNIQUE (both column-
        // and table-level) and CREATE UNIQUE INDEX all register their column set here. Any
        // future source of uniqueness must do the same, or a foreign key to those columns
        // would be wrongly rejected: Postgres requires a foreign key to be backed by an exact
        // unique set — unlike InnoDB, a leftmost prefix of a wider index is not enough.

        // A deferred reference to a schema an object is declared in.
        private sealed record SchemaReference(
            string SourceFile,
            int? Line,
            int? Column,
            string Subject,
            string Schema);

        public void AddSchema(string name) => _declaredSchemas.Add(name);

        /// <summary>
        /// Whether this CREATE TABLE redefines a table already declared elsewhere — in which
        /// case its elements are left out of the model. The error itself was recorded by
        /// <see cref="AddCreateTable"/>.
        /// </summary>
        public bool IsDuplicateTable(CreateTableStatement createTableStatement)
            => _duplicateTables.Contains(createTableStatement);

        private readonly HashSet<CreateTableStatement> _duplicateTables = [];

        /// <summary>
        /// Whether this CREATE PROCEDURE redefines one already declared with the same name
        /// and argument types.
        /// </summary>
        public bool IsDuplicateProcedure(CreateProcedureStatement createProcedureStatement)
            => _duplicateProcedures.Contains(createProcedureStatement);

        private readonly HashSet<CreateProcedureStatement> _duplicateProcedures = [];

        public void AddCreateProcedure(IFile file, CreateProcedureStatement createProcedureStatement)
        {
            var (schema, name) = SplitSchema(createProcedureStatement.Name);

            // A routine's identity is its name plus its IN/INOUT argument types, so an
            // overload with a different signature is a distinct object, not a duplicate.
            var argumentTypes = string.Join(',', createProcedureStatement.Parameters
                .Where(i => i.Mode is ParameterMode.In or ParameterMode.InOut or ParameterMode.Variadic)
                .Select(i => NormalizeArgumentType(i.DataType)));

            var procedureKey = (schema.ToLowerInvariant(), name.UnqualifiedName.ToLowerInvariant(), argumentTypes);

            if (_procedureOrigins.TryGetValue(procedureKey, out var existingProcedure))
            {
                AddError(new SqlSourceException(
                    $"Procedure '{Display(schema, name.UnqualifiedName)}({argumentTypes})' is "
                    + $"already defined in {DescribeOrigin(existingProcedure)}.",
                    file.Name, createProcedureStatement.Line, createProcedureStatement.Column,
                    SqlSourceException.DuplicateDefinition));

                _duplicateProcedures.Add(createProcedureStatement);
            }
            else
            {
                _procedureOrigins[procedureKey] =
                    new Origin(file.Name, createProcedureStatement.Line);
            }

            // Schemas are declared objects (issue #37): a procedure in a non-public schema
            // needs that schema's CREATE SCHEMA somewhere in the project, or the deploy
            // would fail — so its absence is a build error.
            if (!string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase))
            {
                _schemaReferences.Add(new SchemaReference(
                    file.Name, createProcedureStatement.Line, createProcedureStatement.Column,
                    $"Procedure '{schema}.{name.UnqualifiedName}'", schema));
            }
        }

        /// <summary>
        /// Whether this CREATE FUNCTION redefines one already declared with the same name
        /// and argument types (issue #81).
        /// </summary>
        public bool IsDuplicateFunction(CreateFunctionStatement createFunctionStatement)
            => _duplicateFunctions.Contains(createFunctionStatement);

        private readonly HashSet<CreateFunctionStatement> _duplicateFunctions = [];

        private readonly Dictionary<(string Schema, string Name, string Args), Origin> _functionOrigins = new();

        public void AddCreateFunction(IFile file, CreateFunctionStatement createFunctionStatement)
        {
            var (schema, name) = SplitSchema(createFunctionStatement.Name);

            var argumentTypes = string.Join(',', createFunctionStatement.Parameters
                .Where(i => i.Mode is ParameterMode.In or ParameterMode.InOut or ParameterMode.Variadic)
                .Select(i => NormalizeArgumentType(i.DataType)));

            var functionKey = (schema.ToLowerInvariant(), name.UnqualifiedName.ToLowerInvariant(), argumentTypes);

            if (_functionOrigins.TryGetValue(functionKey, out var existingFunction))
            {
                AddError(new SqlSourceException(
                    $"Function '{Display(schema, name.UnqualifiedName)}({argumentTypes})' is "
                    + $"already defined in {DescribeOrigin(existingFunction)}.",
                    file.Name, createFunctionStatement.Line, createFunctionStatement.Column,
                    SqlSourceException.DuplicateDefinition));

                _duplicateFunctions.Add(createFunctionStatement);
            }
            else
            {
                _functionOrigins[functionKey] = new Origin(file.Name, createFunctionStatement.Line);
            }

            if (!string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase))
            {
                _schemaReferences.Add(new SchemaReference(
                    file.Name, createFunctionStatement.Line, createFunctionStatement.Column,
                    $"Function '{schema}.{name.UnqualifiedName}'", schema));
            }
        }

        public void AddCreateTrigger(IFile file, CreateTriggerStatement createTriggerStatement)
        {
            var (schema, table) = SplitSchema(createTriggerStatement.Table);

            // Schemas are declared objects (issue #37): a trigger on a table in a non-public
            // schema needs that schema declared, or the deploy would fail.
            if (!string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase))
            {
                _schemaReferences.Add(new SchemaReference(
                    file.Name, createTriggerStatement.Line, createTriggerStatement.Column,
                    $"Trigger '{createTriggerStatement.Name}'", schema));
            }

            // The table the trigger fires on must be declared in the project, so an
            // unresolved one is reported like any other unresolved reference. The function is
            // not validated here: a trigger commonly runs a built-in (tsvector_update_trigger),
            // which is not a declared object, so requiring one would reject valid schemas.
            AddTableReference(new TableReference(
                file.Name, createTriggerStatement.Line, createTriggerStatement.Column,
                $"Trigger '{createTriggerStatement.Name}'",
                TableKey(schema, table.UnqualifiedName),
                Display(schema, table.UnqualifiedName), []));
        }

        public void AddCreateView(IFile file, CreateViewStatement createViewStatement)
        {
            var (schema, name) = SplitSchema(createViewStatement.Name);

            // Schemas are declared objects (issue #37), exactly as for a table or procedure.
            if (!string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase))
            {
                _schemaReferences.Add(new SchemaReference(
                    file.Name, createViewStatement.Line, createViewStatement.Column,
                    $"View '{schema}.{name.UnqualifiedName}'", schema));
            }

            // Every table the view selects from must be declared in the project, so an
            // unresolved one is reported like any other unresolved reference.
            foreach (var sourceTable in createViewStatement.SourceTables)
            {
                var (tableSchema, tableName) = SplitSchema(sourceTable);

                AddTableReference(new TableReference(
                    file.Name, createViewStatement.Line, createViewStatement.Column,
                    $"View '{schema}.{name.UnqualifiedName}'",
                    TableKey(tableSchema, tableName.UnqualifiedName),
                    Display(tableSchema, tableName.UnqualifiedName), []));
            }
        }

        /// <summary>
        /// The columns declared on a table, or null when the table is not declared in the
        /// project. Used to expand a view's <c>SELECT *</c>; an unresolved table is already
        /// reported as an error by <see cref="SourceValidatorBase{TTableKey}.ThrowIfInvalid"/>.
        /// </summary>
        public IReadOnlyList<string>? GetDeclaredColumns(string schema, string table)
            => DeclaredColumnOrder.TryGetValue(TableKey(schema, table), out var columns)
                ? columns
                : null;

        public void AddCreateTable(IFile file, CreateTableStatement createTableStatement)
        {
            var (schema, tableName) = SplitSchema(createTableStatement.Name);
            var table = tableName.UnqualifiedName;

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var columnDefinition in createTableStatement.Elements.OfType<ColumnDefinition>())
            {
                // A column named twice in the same table would silently collapse into one
                // model element; Postgres rejects it outright, so it is a build error.
                if (!columns.Add(columnDefinition.Name.Name))
                {
                    AddError(new SqlSourceException(
                        $"Column '{columnDefinition.Name.Name}' is defined more than once on "
                        + $"table '{table}'.",
                        file.Name, createTableStatement.Line, createTableStatement.Column,
                        SqlSourceException.DuplicateDefinition));
                }
            }

            var key = TableKey(schema, table);

            // Two CREATE TABLEs for the same name: the declared-table map would last-win and
            // the model would carry both elements, which confuses diffing — so it is an error
            // reported at the second definition, naming where the first one is.
            if (TableOrigins.TryGetValue(key, out var existing))
            {
                AddError(new SqlSourceException(
                    $"Table '{Display(schema, table)}' is already defined in "
                    + $"{DescribeOrigin(existing)}.",
                    file.Name, createTableStatement.Line, createTableStatement.Column,
                    SqlSourceException.DuplicateDefinition));

                // Keep the first definition's columns as the authoritative set; the build is
                // failing anyway, and this avoids cascading unresolved-reference errors.
                _duplicateTables.Add(createTableStatement);

                return;
            }

            TableOrigins[key] = new Origin(file.Name, createTableStatement.Line);
            DeclaredTables[key] = columns;

            // The set above is for membership checks and is unordered; a view's SELECT *
            // expands in declaration order, so that order is kept alongside it.
            DeclaredColumnOrder[TableKey(schema, table)] = createTableStatement.Elements
                .OfType<ColumnDefinition>()
                .Select(i => i.Name.Name)
                .ToList();

            // Schemas are declared objects (issue #37): a table in a non-public schema
            // needs that schema's CREATE SCHEMA somewhere in the project or the deploy
            // would fail — so its absence is a build error.
            if (!string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase))
            {
                _schemaReferences.Add(new SchemaReference(
                    file.Name, createTableStatement.Line, createTableStatement.Column,
                    $"Table '{schema}.{table}'", schema));
            }

            foreach (var tableConstraint in createTableStatement.Elements.OfType<TableConstraint>())
            {
                var (constraint, constraintName) = tableConstraint is NamedTableConstraint named
                    ? (named.Constraint, named.Name.Name)
                    : (tableConstraint, (string?)null);

                var line = constraint.Line ?? createTableStatement.Line;
                var column = constraint.Column ?? createTableStatement.Column;

                // A USING INDEX constraint declares no columns of its own and is not modeled
                // (issue #143), so there is nothing to validate — and validating it would
                // reject valid SQL for having an empty column list.
                if (constraint is PrimaryKeyTableConstraint { UsingIndex: not null }
                    or UniqueTableConstraint { UsingIndex: not null })
                {
                    continue;
                }

                // Only an index-backed constraint (PRIMARY KEY, UNIQUE) takes a name in the
                // schema's relation namespace. A FOREIGN KEY or CHECK name is scoped to its
                // table, and Postgres happily accepts the same one on two tables — so
                // checking those would reject valid SQL.
                if (constraintName is not null
                    && constraint is PrimaryKeyTableConstraint or UniqueTableConstraint)
                {
                    CheckDuplicateConstraintName(file, line, column, schema, constraintName);
                }

                if (constraint is PrimaryKeyTableConstraint pk)
                {
                    CheckOwnColumns(file, line, column,
                        $"Primary key on table '{table}'", table, columns,
                        pk.Columns.Select(c => c.Name));

                    AddUniqueColumnSet(schema, table, pk.Columns.Select(c => c.Name), isPrimaryKey: true);
                }
                else if (constraint is UniqueTableConstraint unique)
                {
                    CheckOwnColumns(file, line, column,
                        $"Unique constraint on table '{table}'", table, columns,
                        unique.Columns.Select(c => c.Name));

                    // An unnamed unique constraint takes the derived <table>_<cols>_key name,
                    // which the model predicts. Two of them can collide (UNIQUE (a_b) and
                    // UNIQUE (a, b) both derive <table>_a_b_key), and Postgres would resolve
                    // that by appending a uniquifying suffix the model cannot predict — so
                    // report it as a duplicate rather than deploy a name that won't match.
                    if (constraintName is null)
                    {
                        // Includes the INCLUDE columns, because the server's derived name does
                        // (issue #210) -- the validator and element construction must predict
                        // the same name or these checks would guard one that never ships.
                        var derived = DeriveUniqueConstraintName(
                            table,
                            unique.Columns.Select(c => c.Name),
                            unique.IncludeColumns.Select(c => c.Name));

                        CheckUniqueConstraintNameIsPredictable(
                            file, line, column, table, derived);
                        CheckDuplicateConstraintName(file, line, column, schema, derived);
                    }

                    // Registering the set lets a foreign key legitimately reference these
                    // columns; without it the FK would be wrongly rejected as unbacked.
                    AddUniqueColumnSet(schema, table, unique.Columns.Select(c => c.Name), isPrimaryKey: false);
                }
                else if (constraint is ForeignKeyTableConstraint fk)
                {
                    CheckOwnColumns(file, line, column,
                        $"Foreign key on table '{table}'", table, columns,
                        fk.Columns.Select(c => c.Name));

                    if (fk.ReferencedColumns.Count > 0 && fk.ReferencedColumns.Count != fk.Columns.Count)
                    {
                        AddError(new SqlSourceException(
                            $"Foreign key on table '{table}' has {fk.Columns.Count} referencing "
                            + $"column(s) but {fk.ReferencedColumns.Count} referenced column(s).",
                            file.Name, line, column, SqlSourceException.InvalidConstraint));

                        // The shape is already wrong; a uniqueness complaint on top of it
                        // would only obscure the actual problem.
                        AddForeignKeyReference(file, line, column, table,
                            fk.ReferencedTable, fk.ReferencedColumns.Select(c => c.Name).ToList(),
                            checkUniqueness: false);

                        continue;
                    }

                    AddForeignKeyReference(file, line, column, table,
                        fk.ReferencedTable, fk.ReferencedColumns.Select(c => c.Name).ToList());
                }
            }

            foreach (var columnDefinition in createTableStatement.Elements.OfType<ColumnDefinition>())
            {
                foreach (var columnConstraint in columnDefinition.Constraints)
                {
                    var (constraint, constraintName) = columnConstraint is NamedColumnConstraint named
                        ? (named.Constraint, named.Name)
                        : (columnConstraint, (string?)null);

                    var line = constraint.Line ?? createTableStatement.Line;
                    var column = constraint.Column ?? createTableStatement.Column;

                    // As with table-level constraints, only an index-backed constraint claims
                    // a name in the schema's relation namespace; an inline FOREIGN KEY name
                    // is per-table, and a named DEFAULT is rejected downstream with a message
                    // of its own.
                    if (constraintName is not null
                        && constraint is PrimaryKeyColumnConstraint or UniqueColumnConstraint)
                    {
                        CheckDuplicateConstraintName(file, line, column, schema, constraintName);
                    }

                    if (constraint is PrimaryKeyColumnConstraint)
                    {
                        AddUniqueColumnSet(schema, table,
                            [columnDefinition.Name.Name], isPrimaryKey: true);
                    }
                    else if (constraint is UniqueColumnConstraint)
                    {
                        // As above: an unnamed inline UNIQUE takes the derived name, so a
                        // collision with another derived name must be reported.
                        if (constraintName is null)
                        {
                            var derived = DeriveUniqueConstraintName(
                                table, [columnDefinition.Name.Name]);

                            CheckUniqueConstraintNameIsPredictable(
                                file, line, column, table, derived);
                            CheckDuplicateConstraintName(file, line, column, schema, derived);
                        }

                        AddUniqueColumnSet(schema, table,
                            [columnDefinition.Name.Name], isPrimaryKey: false);
                    }
                    else if (constraint is ForeignKeyColumnConstraint fk)
                    {
                        AddForeignKeyReference(file, line, column,
                            table,
                            fk.ReferencedTable,
                            fk.ReferencedColumn is { } referencedColumn
                                ? new[] { referencedColumn.Name }
                                : Array.Empty<string>());
                    }
                }
            }
        }

        public void AddCreateIndex(IFile file, CreateIndexStatement createIndexStatement)
        {
            var (schema, tableName) = SplitSchema(createIndexStatement.OnRelation.Name);

            var subject = createIndexStatement.Name is { } name
                ? $"Index '{name.Name}'"
                : "Index";

            // Only plain column keys are resolved; an expression key names no single column of
            // its own, so the columns it reads are not checked here.
            var columns = createIndexStatement.Elements
                .Select(e => e.Expression)
                .OfType<ColumnReferenceExpression>()
                .Select(c => c.Identifier.Name)
                .ToList();

            // INCLUDE columns must exist on the table just as key columns do, so they are
            // resolved too — but separately, since they take no part in the key (issue #160).
            var referencedColumns = columns
                .Concat(createIndexStatement.IncludeElements
                    .Select(e => e.Expression)
                    .OfType<ColumnReferenceExpression>()
                    .Select(c => c.Identifier.Name))
                .ToList();

            AddTableReference(new TableReference(
                file.Name, createIndexStatement.Line, createIndexStatement.Column,
                subject,
                TableKey(schema, tableName.UnqualifiedName),
                Display(schema, tableName.UnqualifiedName), referencedColumns));

            // An index shares the constraint namespace within a schema, so a name reused by
            // another index or a constraint is a duplicate definition.
            if (createIndexStatement.Name is { } indexName)
            {
                CheckDuplicateConstraintName(file, createIndexStatement.Line,
                    createIndexStatement.Column, schema, indexName.Name);
            }

            // A UNIQUE index backs a foreign key exactly as a unique constraint does, but
            // only when every key is a plain column (a partial or expression index does not
            // satisfy Postgres's requirement).
            if (createIndexStatement.Unique
                && createIndexStatement.WhereClause is null
                && columns.Count == createIndexStatement.Elements.Count)
            {
                AddUniqueColumnSet(schema, tableName.UnqualifiedName, columns, isPrimaryKey: false);
            }
        }

        /// <summary>
        /// Resolves Postgres's schema-as-declared-object references (issue #37) first — a table,
        /// routine, or trigger in a non-public schema needs that schema's CREATE SCHEMA in the
        /// project — then defers to the base for the shared table/column and foreign-key
        /// backing-index resolution and the all-errors-at-once throw.
        /// </summary>
        public override void ThrowIfInvalid()
        {
            foreach (var reference in _schemaReferences)
            {
                if (!_declaredSchemas.Contains(reference.Schema))
                {
                    AddError(new SqlSourceException(
                        $"{reference.Subject} is in schema '{reference.Schema}', "
                        + "which is not defined in the project.",
                        reference.SourceFile, reference.Line, reference.Column,
                        SqlSourceException.UnresolvedReference));
                }
            }

            base.ThrowIfInvalid();
        }

        // A table's name as it should read in a message: bare in the public schema (matching
        // how it was almost certainly written), schema-qualified anywhere else.
        private static string Display(string schema, string name)
            => string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{schema}.{name}";

        private void AddForeignKeyReference(IFile file,
            int? line,
            int? column,
            string referencingTable,
            QualifiedName referencedTable,
            IReadOnlyList<string> referencedColumns,
            bool checkUniqueness = true)
        {
            var (referencedSchema, referencedName) = SplitSchema(referencedTable);
            var referencedKey = TableKey(referencedSchema, referencedName.UnqualifiedName);
            var referencedDisplay = Display(referencedSchema, referencedName.UnqualifiedName);

            AddTableReference(new TableReference(
                file.Name, line, column,
                $"Foreign key on table '{referencingTable}'",
                referencedKey, referencedDisplay, referencedColumns));

            // Deferred: the referenced table may be declared later, or in another file, so
            // its primary key / unique constraints are not all known yet.
            if (checkUniqueness)
            {
                AddForeignKeyCheck(new ForeignKeyUniquenessCheck(
                    file.Name, line, column,
                    $"Foreign key on table '{referencingTable}'",
                    referencedKey, referencedDisplay, referencedColumns));
            }
        }

        /// <summary>
        /// Records a set of columns made unique by a primary key, unique constraint, or
        /// unique index, so a foreign key referencing exactly that set can be validated.
        /// Overload that computes the (schema, table) key before delegating to the base.
        /// </summary>
        private void AddUniqueColumnSet(string schema,
            string table,
            IEnumerable<string> columns,
            bool isPrimaryKey)
            => AddUniqueColumnSet(TableKey(schema, table), columns, isPrimaryKey);

        /// <summary>
        /// Reports a constraint or index name already used in the same schema. A primary key
        /// or unique constraint is backed by an index, and indexes share the per-schema
        /// relation namespace — so two tables in one schema cannot both have a
        /// <c>CONSTRAINT pk_x PRIMARY KEY</c>, and the deploy would fail with
        /// "relation already exists".
        /// </summary>
        // PostgreSQL truncates a generated constraint name to 63 bytes, shortening the table
        // and column components from the middle while preserving the "_key" suffix. That
        // algorithm is not reproduced here, so a name that would be truncated cannot be
        // predicted — and an unpredictable name would never hash-match the one read back from
        // the database, silently re-diffing on every deploy. Require an explicit name instead.
        private void CheckUniqueConstraintNameIsPredictable(IFile file,
            int? line,
            int? column,
            string table,
            string derivedName)
        {
            // The limit is NAMEDATALEN - 1 = 63 bytes, not characters. The same fact is
            // declared as a capability on PostgresqlDatabaseSchemaProvider
            // (MaxIdentifierLength / MeasureIdentifier, issue #163); it is repeated here
            // because this builder is not given a schema provider, and threading one in is a
            // public API change out of scope for that issue. Both must move together.
            if (System.Text.Encoding.UTF8.GetByteCount(derivedName) <= 63)
            {
                return;
            }

            AddError(new SqlSourceException(
                $"The generated name for the unique constraint on table '{table}' "
                + $"('{derivedName}') exceeds PostgreSQL's 63-byte identifier limit and would "
                + "be truncated to a name Squill cannot predict. Name the constraint "
                + "explicitly with CONSTRAINT <name> UNIQUE (...).",
                file.Name, line, column, SqlSourceException.InvalidConstraint));
        }

        private void CheckDuplicateConstraintName(IFile file,
            int? line,
            int? column,
            string schema,
            string name)
        {
            var key = (schema.ToLowerInvariant(), name.ToLowerInvariant());

            if (_constraintOrigins.TryGetValue(key, out var existing))
            {
                AddError(new SqlSourceException(
                    $"Constraint or index '{name}' is already defined in "
                    + $"{DescribeOrigin(existing)}.",
                    file.Name, line, column, SqlSourceException.DuplicateDefinition));

                return;
            }

            _constraintOrigins[key] = new Origin(file.Name, line);
        }

        // Postgres folds unquoted identifiers to lowercase while the parser preserves
        // source casing, so declared-object lookups compare case-insensitively — this can
        // miss a quoted-identifier case mismatch, but never produces a false error.
        private static (string Schema, string Table) TableKey(string schema, string table)
            => (schema.ToLowerInvariant(), table.ToLowerInvariant());
    }

    // A foreign key gathered while walking a CREATE TABLE, before it becomes an
    // element (its name may be explicit or derived from the Postgres convention).
    private sealed record ForeignKeySpec(
        string? ExplicitName,
        IReadOnlyList<string> Columns,
        QualifiedName ReferencedTable,
        IReadOnlyList<string> ReferencedColumns,
        ReferentialAction OnDelete,
        ReferentialAction OnUpdate,
        bool IsDeferrable = false,
        bool IsInitiallyDeferred = false);

    // A UNIQUE constraint gathered while walking a CREATE TABLE, before it becomes an
    // element (its name may be explicit or derived from the Postgres convention).
    // IncludeColumns and StorageParameters carry the INCLUDE (...) and WITH (...) clauses a
    // UNIQUE constraint shares with the index backing it (issue #210). IncludeColumns also
    // feeds the derived name, which folds them in -- see DeriveUniqueConstraintName.
    private sealed record UniqueConstraintSpec(
        string? ExplicitName,
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> IncludeColumns,
        string? StorageParameters);

    // A CHECK constraint gathered from a CREATE TABLE. Column is the column an inline CHECK
    // was written on (null for a table-level one), which is what Postgres folds into the
    // name it derives for an unnamed constraint.
    private sealed record CheckConstraintSpec(
        string? ExplicitName, string? Column, string Expression);

    // Postgres names an unnamed CHECK constraint <table>_<column>_check for one written
    // inline on a column, and <table>_check for a table-level one. Predicting the name lets
    // a parsed model hash-match one extracted from the database.
    private static string DeriveCheckConstraintName(string table, string? column)
        => column is null ? $"{table}_check" : $"{table}_{column}_check";

    // The INCLUDE / WITH clauses gathered from a table-level PRIMARY KEY while walking a
    // CREATE TABLE (issue #210). A holder rather than out-parameters because the collection
    // walk already threads several accumulators through, and a PK contributes at most one set.
    private sealed class IndexBackedConstraintOptions
    {
        public List<string> IncludeColumns { get; } = [];

        public string? StorageParameters { get; set; }
    }

    // The WITH (...) storage parameters of an index-backed constraint, rendered into the same
    // canonical string an index uses (issue #210), or null when none were declared -- so an
    // ordinary constraint carries no property and hashes as it did before.
    private static string? RenderStorageParametersOrNull(ICollection<IndexWithOption> options)
        => options.Count > 0 ? RenderStorageParameters(options) : null;

    // USING INDEX TABLESPACE on a constraint is rejected rather than modeled, matching what
    // CREATE INDEX already does (issue #160): measured there, an index in pg_default records
    // reltablespace = 0 exactly as one with no clause does, so naming the default is a genuine
    // no-op, while any other tablespace is a real placement decision the model cannot carry and
    // would silently lose. Converging both spellings on one answer is the point of issue #210.
    private static void RejectNonDefaultConstraintTablespace(
        IIndexBackedTableConstraint constraint, string table)
    {
        if (constraint.TableSpace is { } tablespace
            && !string.Equals(tablespace.Name, "pg_default", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"A constraint on table '{table}' declares USING INDEX TABLESPACE "
                + $"'{tablespace.Name}', which Squill does not model. Only the default "
                + "tablespace (pg_default) is supported.");
        }
    }

    // The name Postgres gives an unnamed unique constraint: <table>_<col>_..._key. Shared by
    // the validator (to detect a collision between two derived names) and by element
    // construction, so the predicted name and the validated name cannot drift apart.
    //
    // Postgres resolves a name that is already taken by appending an increasing integer
    // (<table>_<col>_key1, …) against the relation namespace it shares with indexes. Two
    // colliding declarations in the source are caught by CheckDuplicateConstraintName, and a
    // name too long to predict by CheckUniqueConstraintNameIsPredictable; a collision with an
    // object that exists only in the target database remains unpredictable from source alone,
    // so naming the constraint explicitly is the reliable option.
    //
    // The INCLUDE columns take part in the name too: measured, `UNIQUE (a, b) INCLUDE (c)` is
    // named <table>_a_b_c_key, not <table>_a_b_key (issue #210). Passing key columns alone
    // would predict a name the server never uses, so the constraint would re-diff forever.
    private static string DeriveUniqueConstraintName(
        string table, IEnumerable<string> columns, IEnumerable<string>? includeColumns = null)
        => $"{table}_{string.Join('_', columns.Concat(includeColumns ?? []))}_key";

    private static IEnumerable<Element> MakeCreateTableElements(CreateTableStatement createTableStatement)
    {
        var (schema, tableName) = SplitSchema(createTableStatement.Name);

        // The table element is built through the factory so it carries the same schema
        // relationship the DB builder emits (an implicit "public" when the CREATE TABLE
        // is not schema-qualified), letting a parsed model hash-match an extracted one.
        var tableElement = PostgresModelFactory.CreateTable(tableName, schema);

        var primaryKeyColumns = new List<PostgresModelFactory.IndexedColumn>();
        var foreignKeys = new List<ForeignKeySpec>();
        var uniqueConstraints = new List<UniqueConstraintSpec>();
        var checkConstraints = new List<CheckConstraintSpec>();

        // The INCLUDE and WITH clauses of a table-level PRIMARY KEY (issue #210). Only a
        // table-level PK can carry them -- the inline column spelling has no such clause -- so
        // they are filled in by CollectTableLevelConstraints alone.
        var primaryKeyOptions = new IndexBackedConstraintOptions();

        var inlinePkName = AddTableColumnsRelationship(
            tableElement, tableName, createTableStatement, primaryKeyColumns, foreignKeys,
            uniqueConstraints, checkConstraints);

        var tableLevelPkName = CollectTableLevelConstraints(
            createTableStatement, tableName, primaryKeyColumns, foreignKeys, uniqueConstraints,
            checkConstraints, primaryKeyOptions);

        // A named PK can be written inline on its column (CONSTRAINT pk_x PRIMARY KEY) or as
        // a table-level clause; at most one applies, so either source is the explicit name.
        var explicitPkName = inlinePkName ?? tableLevelPkName;

        yield return tableElement;

        // Postgres names an unnamed primary key <table>_pkey. The PK is emitted as its
        // own model element (not a table annotation) so both builders agree and so it is
        // visible to schema comparison and scripting.
        if (primaryKeyColumns.Count > 0)
        {
            var pkName = tableName.Sibling(explicitPkName ?? $"{tableName.UnqualifiedName}_pkey");

            yield return PostgresModelFactory.CreatePrimaryKey(
                pkName, tableName, primaryKeyColumns, schema,
                primaryKeyOptions.IncludeColumns.Select(c => tableName.Child(c)),
                primaryKeyOptions.StorageParameters);
        }

        // Postgres names an unnamed unique constraint <table>_<col>_..._key. Predicting it
        // here lets a parsed model hash-match one extracted from the database. Emitted after
        // the PK and before the FKs, matching the DB builder's per-table element order.
        foreach (var unique in uniqueConstraints)
        {
            var uniqueName = tableName.Sibling(unique.ExplicitName
                ?? DeriveUniqueConstraintName(
                    tableName.UnqualifiedName, unique.Columns, unique.IncludeColumns));

            var columns = unique.Columns
                .Select(c => new PostgresModelFactory.IndexedColumn(tableName.Child(c)));

            yield return PostgresModelFactory.CreateUniqueConstraint(
                uniqueName, tableName, columns, schema,
                unique.IncludeColumns.Select(c => tableName.Child(c)),
                unique.StorageParameters);
        }

        // CHECK constraints follow the unique constraints and precede the FKs, matching the
        // DB builder's per-table element order.
        foreach (var check in checkConstraints)
        {
            var checkName = tableName.Sibling(check.ExplicitName
                ?? DeriveCheckConstraintName(tableName.UnqualifiedName, check.Column));

            yield return PostgresModelFactory.CreateCheckConstraint(
                checkName, tableName, check.Expression, schema);
        }

        foreach (var foreignKey in foreignKeys)
        {
            yield return MakeForeignKeyElement(tableName, foreignKey, schema);
        }
    }

    private static Element MakeForeignKeyElement(
        SqlName tableName, ForeignKeySpec spec, string schema)
    {
        var referencedTable = NormalizeReferencedTable(spec.ReferencedTable);

        // Postgres derives an unnamed FK constraint's name as <table>_<firstcolumn>_fkey.
        // Predicting it here lets a parsed model hash-match one extracted from the DB.
        var fkName = spec.ExplicitName is { } explicitName
            ? tableName.Sibling(explicitName)
            : tableName.Sibling($"{tableName.UnqualifiedName}_{spec.Columns[0]}_fkey");

        var columns = spec.Columns.Select(tableName.Child);
        var foreignColumns = spec.ReferencedColumns.Select(referencedTable.Child);

        return PostgresModelFactory.CreateForeignKey(
            fkName,
            tableName,
            columns,
            referencedTable,
            foreignColumns,
            spec.OnDelete,
            spec.OnUpdate,
            spec.IsDeferrable,
            spec.IsInitiallyDeferred,
            schema);
    }

    // Walks the table-level constraints, appending table-level PK columns, unique specs and
    // FK specs. Returns the explicit name of a table-level PRIMARY KEY constraint if named.
    private static string? CollectTableLevelConstraints(CreateTableStatement createTableStatement,
        SqlName tableName,
        List<PostgresModelFactory.IndexedColumn> primaryKeyColumns,
        List<ForeignKeySpec> foreignKeys,
        List<UniqueConstraintSpec> uniqueConstraints,
        List<CheckConstraintSpec> checkConstraints,
        IndexBackedConstraintOptions primaryKeyOptions)
    {
        string? explicitPkName = null;

        foreach (var tableConstraint in createTableStatement.Elements.OfType<TableConstraint>())
        {
            var (constraint, explicitName) = tableConstraint is NamedTableConstraint named
                ? (named.Constraint, named.Name.Name)
                : (tableConstraint, (string?)null);

            // Not modeled: the constraint's columns live on the index it names, which the
            // model has no way to bind to. Warned about as SQ1002 (issue #143).
            if (constraint is PrimaryKeyTableConstraint { UsingIndex: not null }
                or UniqueTableConstraint { UsingIndex: not null })
            {
                continue;
            }

            if (constraint is PrimaryKeyTableConstraint pk)
            {
                foreach (var column in pk.Columns)
                {
                    primaryKeyColumns.Add(new PostgresModelFactory.IndexedColumn(tableName.Child(column.Name)));
                }

                RejectNonDefaultConstraintTablespace(pk, table: tableName.UnqualifiedName);

                primaryKeyOptions.IncludeColumns.AddRange(pk.IncludeColumns.Select(c => c.Name));
                primaryKeyOptions.StorageParameters = RenderStorageParametersOrNull(pk.WithOptions);

                explicitPkName = explicitName;
            }
            else if (constraint is UniqueTableConstraint unique)
            {
                RejectNonDefaultConstraintTablespace(unique, table: tableName.UnqualifiedName);

                uniqueConstraints.Add(new UniqueConstraintSpec(
                    explicitName,
                    unique.Columns.Select(c => c.Name).ToList(),
                    unique.IncludeColumns.Select(c => c.Name).ToList(),
                    RenderStorageParametersOrNull(unique.WithOptions)));
            }
            else if (constraint is ForeignKeyTableConstraint fk)
            {
                foreignKeys.Add(new ForeignKeySpec(
                    explicitName,
                    fk.Columns.Select(c => c.Name).ToList(),
                    fk.ReferencedTable,
                    fk.ReferencedColumns.Select(c => c.Name).ToList(),
                    fk.OnDelete ?? ReferentialAction.NoAction,
                    fk.OnUpdate ?? ReferentialAction.NoAction,
                    // The visitor has already collapsed INITIALLY DEFERRED ⇒ DEFERRABLE, so
                    // unlike the inline path there is nothing left to resolve here (issue #160).
                    fk.IsDeferrable,
                    fk.IsInitiallyDeferred));
            }
            else if (constraint is CheckTableConstraint check)
            {
                // A table-level CHECK has no column of its own; its predicate may span any
                // columns of the table (issue #120).
                checkConstraints.Add(new CheckConstraintSpec(
                    explicitName, Column: null, ExpressionSqlRenderer.Render(check.Expression)));
            }
        }

        return explicitPkName;
    }

    // Returns the explicit name of a single-column inline PRIMARY KEY constraint
    // (CONSTRAINT pk_x PRIMARY KEY on a column), or null if the PK is unnamed or table-level.
    private static string? AddTableColumnsRelationship(Element sqlTableElement,
        SqlName tableName,
        CreateTableStatement createTableStatement,
        List<PostgresModelFactory.IndexedColumn> primaryKeyColumns,
        List<ForeignKeySpec> foreignKeys,
        List<UniqueConstraintSpec> uniqueConstraints,
        List<CheckConstraintSpec> checkConstraints)
    {
        string? inlinePkName = null;

        var columns = new Relationship(PostgresRelationshipNames.Columns);
        sqlTableElement.Relationships.Add(columns);

        foreach (var columnDefinition in createTableStatement.Elements.OfType<ColumnDefinition>())
        {
            var columnName = tableName.Child(columnDefinition.Name.Name);

            var element = new Element(PostgresElementTypes.SqlSimpleColumn)
            {
                Name = columnName,
            };

            // Nullability can be implied by several constraints (NOT NULL, PRIMARY KEY,
            // IDENTITY); collect these here and emit the properties once, after the loop,
            // in a fixed order (IsNullable then identity) so the model matches the
            // DB-extraction builder's property order — the Merkle hash is order-sensitive.
            bool? isNullable = null;
            IdentityColumnConstraint? identityConstraint = null;
            string? defaultValue = null;
            string? generatedExpression = null;
            string? collation = null;

            // An inline constraint attribute (DEFERRABLE / INITIALLY DEFERRED) is written after
            // the constraint it qualifies but arrives as a sibling node, so it is collected here
            // and applied to the column's foreign key once the whole list has been walked.
            bool? isDeferrable = null;
            bool? isInitiallyDeferred = null;
            ForeignKeySpec? inlineForeignKey = null;

            // SERIAL/SMALLSERIAL/BIGSERIAL are shorthand for a sequence-backed integer
            // column, not real types. They are lowered to the modern equivalent — the
            // underlying integer type (handled by CanonicalName) plus GENERATED BY DEFAULT
            // AS IDENTITY — so the deployed column round-trips: a literal `serial` would
            // deploy as integer + a nextval default, which extraction drops, leaving the
            // column to re-diff on every deploy (issue #121).
            if (IsSerialType(columnDefinition.DataType))
            {
                identityConstraint = new IdentityColumnConstraint(
                    columnDefinition.DataType.TypeName, always: false,
                    null, null, null, null, null, null);

                // A serial column is implicitly NOT NULL.
                isNullable = false;
            }

            foreach (var columnConstraint in columnDefinition.Constraints)
            {
                // A CONSTRAINT <name> wrapper carries an explicit name; unwrap it but
                // remember the name for constraints (like FKs) that model it.
                var (constraint, explicitName) = columnConstraint is NamedColumnConstraint named
                    ? (named.Constraint, named.Name)
                    : (columnConstraint, (string?)null);

                if (constraint is NullableColumnConstraint nullableColumnConstraint)
                {
                    isNullable = nullableColumnConstraint.Nullable;
                }
                else if (constraint is PrimaryKeyColumnConstraint)
                {
                    primaryKeyColumns.Add(new PostgresModelFactory.IndexedColumn(columnName));

                    // A CONSTRAINT <name> PRIMARY KEY on the column names the PK; carry it
                    // so it survives scripting rather than being replaced by <table>_pkey.
                    if (explicitName != null)
                    {
                        inlinePkName = explicitName;
                    }

                    // PKs are not nullable
                    isNullable = false;
                }
                else if (constraint is UniqueColumnConstraint)
                {
                    // An inline UNIQUE is a single-column unique constraint; a
                    // CONSTRAINT <name> UNIQUE wrapper names it. The inline spelling has no
                    // INCLUDE or WITH clause -- those belong to the table-level form -- so it
                    // contributes neither (issue #210).
                    uniqueConstraints.Add(new UniqueConstraintSpec(
                        explicitName, [columnDefinition.Name.Name], [], null));
                }
                else if (constraint is DefaultColumnConstraint defaultConstraint)
                {
                    // Postgres accepts CONSTRAINT <name> DEFAULT <expr> but does not persist
                    // the name (a column default is not a named constraint as in SQL Server),
                    // so it could never survive a round-trip from the database. Reject it
                    // with a clear message rather than model a name that would vanish.
                    if (explicitName != null)
                    {
                        throw new NotSupportedException(
                            $"Column '{columnName}' has a named DEFAULT constraint "
                            + $"('{explicitName}'), but PostgreSQL does not store a name for a "
                            + "column default; remove the constraint name.");
                    }

                    // Only constant literals are modeled (int/numeric/bool/string); a
                    // function default (now(), a serial's nextval, …) or DEFAULT NULL is
                    // left off the model. See PostgresDefaultValue.
                    defaultValue = PostgresDefaultValue.FromExpression(defaultConstraint.Expression);
                }
                else if (constraint is IdentityColumnConstraint identity)
                {
                    identityConstraint = identity;

                    // Postgres identity columns are implicitly NOT NULL.
                    isNullable = false;
                }
                else if (constraint is ForeignKeyColumnConstraint fk)
                {
                    // Held rather than added, because a DEFERRABLE / INITIALLY DEFERRED written
                    // after this clause is a sibling constraint node not yet seen (#159).
                    inlineForeignKey = new ForeignKeySpec(
                        explicitName,
                        new[] { columnDefinition.Name.Name },
                        fk.ReferencedTable,
                        fk.ReferencedColumn is { } refCol ? new[] { refCol.Name } : Array.Empty<string>(),
                        fk.OnDelete ?? ReferentialAction.NoAction,
                        fk.OnUpdate ?? ReferentialAction.NoAction);
                }
                else if (constraint is CollateColumnConstraint collate)
                {
                    // A collation name is case-sensitive; the unqualified name is stored, since
                    // that is what pg_collation reports on the way back.
                    collation = collate.Collation.Segments[^1].Name;
                }
                else if (constraint is ConstraintAttributeColumnConstraint attribute)
                {
                    isDeferrable = attribute.Deferrable ?? isDeferrable;
                    isInitiallyDeferred = attribute.InitiallyDeferred ?? isInitiallyDeferred;
                }
                else if (constraint is CheckColumnConstraint check)
                {
                    // An inline CHECK belongs to the column it is written on, which is what
                    // Postgres folds into the name it derives for an unnamed one (#120).
                    checkConstraints.Add(new CheckConstraintSpec(
                        explicitName,
                        columnDefinition.Name.Name,
                        ExpressionSqlRenderer.Render(check.Expression)));
                }
                else if (constraint is GeneratedColumnConstraint generated)
                {
                    // A generated (computed) column (issue #120). The expression is rendered
                    // back to SQL text for scripting; it does not take part in comparison,
                    // since PostgreSQL rewrites it on the way into the catalog.
                    generatedExpression = ExpressionSqlRenderer.Render(generated.Expression);
                }
                else
                {
                    throw new NotImplementedException(
                        $"Column constraint type {constraint.GetType()} to property mapping not implemented");
                }
            }

            // The inline foreign key is added now that the whole constraint list has been
            // walked, so a DEFERRABLE / INITIALLY DEFERRED written after it is picked up (#159).
            if (inlineForeignKey is not null)
            {
                foreignKeys.Add(inlineForeignKey with
                {
                    // INITIALLY DEFERRED implies DEFERRABLE: PostgreSQL rejects the pairing with
                    // NOT DEFERRABLE, so a source that writes only the INITIALLY clause is
                    // deferrable too — and the catalog reports it that way.
                    IsDeferrable = isDeferrable ?? isInitiallyDeferred ?? false,
                    IsInitiallyDeferred = isInitiallyDeferred ?? false,
                });
            }
            else if (isDeferrable is not null || isInitiallyDeferred is not null)
            {
                // Postgres accepts a constraint attribute only on a constraint that can be
                // deferred; on a column with no such constraint it is a syntax error. Rejecting
                // it here beats silently dropping something the source declared.
                throw new NotSupportedException(
                    $"Column '{columnName}' declares a DEFERRABLE or INITIALLY clause, but "
                    + "PostgreSQL allows one only on a foreign key, UNIQUE or PRIMARY KEY "
                    + "constraint written on the same column.");
            }

            // Only a NOT NULL column stores IsNullable (=false). Nullable is the default,
            // so an explicit `NULL` records no property — matching the DB-extraction
            // builder, which likewise omits the property for nullable columns. Storing
            // IsNullable=true would make a parsed model diverge from the extracted one and
            // break the hash-based comparison.
            if (isNullable is false)
            {
                element.Properties.Add(new Property(PostgresPropertyNames.IsNullable, false));
            }

            if (identityConstraint is not null)
            {
                element.Properties.Add(new Property(PostgresPropertyNames.IsIdentity, true));
                element.Properties.Add(new Property(PostgresPropertyNames.IdentityGeneration,
                    identityConstraint.Always ? "Always" : "ByDefault"));

                AddIdentitySequenceOptionProperties(element, identityConstraint, columnDefinition.DataType);
            }

            // Emitted after identity so parsed and DB-extracted models add the property in
            // the same order (the Merkle hash is order-sensitive).
            if (defaultValue != null)
            {
                element.Properties.Add(new Property(PostgresPropertyNames.DefaultValue, defaultValue));
            }

            if (generatedExpression != null)
            {
                PostgresModelFactory.AddGeneratedColumnProperties(element, generatedExpression);
            }

            // Emitted last, matching the DB-extraction builder's property order. An explicit
            // COLLATE "default" names the collation every collatable column already has, so it
            // records nothing — pg_attribute reports it identically to a column with no COLLATE
            // at all, and storing it would re-diff forever (measured, #159).
            if (collation is not null && !string.Equals(collation, "default", StringComparison.Ordinal))
            {
                element.Properties.Add(new Property(PostgresPropertyNames.Collation, collation));
            }

            element.Relationships.Add(new Relationship(PostgresRelationshipNames.TypeSpecifier)
            {
                BuildTypeSpecifier(columnDefinition.DataType)
            });

            columns.Add(element);
        }

        return inlinePkName;
    }

    // Whether a column's declared type is one of the serial shorthands, which imply a
    // sequence-backed (identity) integer column rather than naming a real type.
    private static bool IsSerialType(DataType dataType)
        => dataType is BuiltInDataType { Type: PostgresBuiltInDataType.SmallSerial
            or PostgresBuiltInDataType.Serial
            or PostgresBuiltInDataType.BigSerial };

    // Builds the SqlTypeSpecifier element for a column's data type. The type reference
    // name is the canonical PostgreSQL type name (matching what the DB builder reads back
    // via format_type()/udt_name), and any length/precision/scale modifiers are attached
    // as properties so a parsed model hash-matches one extracted from a real database.
    /// <summary>
    /// Whether a numeric/decimal type declares a negative scale, e.g. <c>numeric(4, -2)</c>. The
    /// sign parses as a <see cref="UnaryExpression"/> wrapping the literal rather than as part of
    /// it, so this is a structural test rather than a value comparison.
    /// </summary>
    private static bool HasNegativeScale(BuiltInDataType dataType) =>
        dataType.Type == PostgresBuiltInDataType.Decimal
        && dataType.Modifiers.Count == 2
        && dataType.Modifiers[1] is UnaryExpression
            { Operator: PostgresBuiltInUnaryOperator.Negate };

    /// <summary>
    /// Explains why a negative scale is rejected rather than modeled (issue #191). Measured on
    /// PostgreSQL 16: <c>numeric(4,-2)</c> reads back out of
    /// <c>information_schema.columns.numeric_scale</c> as <c>2046</c>, an unsigned reading of the
    /// typmod, and that view is what <c>PostgresDatabaseModelBuilder</c> reads. Modeling the
    /// declared <c>-2</c> would therefore never compare equal to what is extracted back, and the
    /// column would be redeployed on every deploy.
    ///
    /// <para>
    /// Rejected rather than warned-and-dropped because the scale is not droppable: deploying
    /// <c>numeric(4)</c> in its place would silently store different numbers than the source
    /// asked for, which is the failure #141 called out for typed literals.
    /// </para>
    /// </summary>
    private static string NegativeScaleMessage(string? column) =>
        (column is null
            ? "A negative scale on numeric/decimal is not supported"
            : $"The negative scale on column '{column}' is not supported")
        + ": PostgreSQL reports it back as an unsigned value (a scale of -2 reads as 2046), so it "
        + "cannot be compared against the database and the column would be redeployed on every "
        + "deploy.";

    private static Element BuildTypeSpecifier(DataType dataType)
    {
        // An array type declares as its element type's name with `[]` appended (the
        // PostgreSQL array notation). PostgreSQL "ignores any supplied array size limits"
        // and "does not enforce the declared number of dimensions" — the size/dimensions
        // are "simply documentation" (see the arrays docs) — so the model carries no
        // size, and format_type() renders the same "<element>[]" on the DB side (#76).
        if (dataType is ArrayDataType arrayDataType)
        {
            return MakeTypeSpecifierElement(CanonicalTypeName(arrayDataType.ElementType) + "[]");
        }

        if (dataType is BuiltInDataType builtInDataType)
        {
            var typeSpec = MakeTypeSpecifierElement(builtInDataType.Type.CanonicalName());

            // A bare `bit` is fixed-length bit(1): Postgres stores it that way and
            // information_schema reports character_maximum_length = 1, so the parser
            // side must emit Length = 1 to hash-match the DB builder. A bare `bit
            // varying`, by contrast, is unbounded and reports NULL length, so it gets
            // no property — the same treatment as a bare varchar (issue #97).
            if (builtInDataType.Type == PostgresBuiltInDataType.Bit
                && builtInDataType.Modifiers.Count == 0)
            {
                typeSpec.Properties.Add(new Property(PostgresPropertyNames.Length, 1));
                return typeSpec;
            }

            // A type with no modifiers gets no length/precision properties. For a
            // bare varchar this mirrors the DB builder, where an unbounded
            // character varying reports character_maximum_length = NULL — so both
            // sides agree and the model hashes match (issue #6).
            if (builtInDataType.Modifiers.Count == 1)
            {
                if (builtInDataType.Type is PostgresBuiltInDataType.Varchar
                    or PostgresBuiltInDataType.Char
                    or PostgresBuiltInDataType.Bit
                    or PostgresBuiltInDataType.BitVarying)
                {
                    if (builtInDataType.Modifiers[0] is not LiteralExpression { Value: long length })
                    {
                        throw new InvalidOperationException(
                            "Unexpected length modifier for varchar, character, or bit type");
                    }

                    // Store as int to match the DB-extraction builder and the script
                    // generator, which both use int for the Length property. For bit types
                    // this is the bit-string length; format_type() reports it the same way.
                    typeSpec.Properties.Add(new Property(PostgresPropertyNames.Length, (int)length));
                }
                else
                {
                    throw new NotImplementedException(
                        $"Modifiers for built-in data type {builtInDataType.Type} not yet implemented");
                }
            }
            else if (builtInDataType.Modifiers.Count > 1)
            {
                if (builtInDataType.Type == PostgresBuiltInDataType.Decimal)
                {
                    if (builtInDataType.Modifiers.Count != 2)
                    {
                        throw new InvalidOperationException("Expected only 2 modifiers for numeric/decimal type");
                    }

                    // A negative scale — numeric(4, -2), rounding to hundreds — is valid
                    // PostgreSQL from 15 onward, and parses here as a Negate over the literal
                    // rather than as a negative literal. It is rejected rather than modeled
                    // because it cannot make the round trip (issue #191): measured on 16,
                    // information_schema.columns.numeric_scale reports a -2 scale as 2046, an
                    // unsigned reading of the typmod, and that view is what the database model
                    // builder reads. Modeling -2 would therefore compare unequal to the 2046
                    // extracted back and re-diff the column on every deploy.
                    //
                    // Rejected rather than warned-and-dropped because the scale is not
                    // droppable: deploying numeric(4) instead would silently store different
                    // numbers than the source asked for.
                    if (HasNegativeScale(builtInDataType))
                    {
                        throw new NotSupportedException(NegativeScaleMessage(null));
                    }

                    if (builtInDataType.Modifiers[0] is not LiteralExpression { Value: long precision }
                        || builtInDataType.Modifiers[1] is not LiteralExpression { Value: long scale })
                    {
                        throw new InvalidOperationException(
                            "Either precision or scale modifier for numeric/decimal type was not an integer");
                    }

                    typeSpec.Properties.Add(new Property(PostgresPropertyNames.Precision, precision));
                    typeSpec.Properties.Add(new Property(PostgresPropertyNames.Scale, scale));
                }
                else
                {
                    throw new NotImplementedException($"More than 1 modifier not yet implemented for built-in type {builtInDataType.Type}");
                }
            }

            return typeSpec;
        }

        if (dataType is UnresolvedDataType unresolvedDataType)
        {
            // A custom type (e.g. pgvector's `vector`) is not a built-in. Its type
            // name is carried verbatim so it hash-matches the DB builder, which reads
            // the same name from pg_type (udt_name). A single integer modifier — the
            // dimension in vector(3) — is stored as Length, mirroring how the DB
            // builder reports it from atttypmod.
            var typeSpec = MakeTypeSpecifierElement(unresolvedDataType.TypeName);

            if (unresolvedDataType.Modifiers.Count == 1)
            {
                if (unresolvedDataType.Modifiers[0] is not LiteralExpression { Value: long dimension })
                {
                    throw new NotImplementedException(
                        $"Non-integer modifier for custom type {unresolvedDataType.TypeName} not yet implemented");
                }

                typeSpec.Properties.Add(new Property(PostgresPropertyNames.Length, (int)dimension));
            }
            else if (unresolvedDataType.Modifiers.Count > 1)
            {
                throw new NotImplementedException(
                    $"More than one modifier not yet implemented for custom type {unresolvedDataType.TypeName}");
            }

            return typeSpec;
        }

        throw new NotImplementedException(
            $"Data meta-type {dataType.GetType()} to relationship mapping not implemented");
    }

    // The canonical PostgreSQL name for a data type, used as an array's element-type name.
    private static string CanonicalTypeName(DataType dataType) => dataType switch
    {
        BuiltInDataType builtIn => builtIn.Type.CanonicalName(),
        UnresolvedDataType unresolved => unresolved.TypeName,
        ArrayDataType array => CanonicalTypeName(array.ElementType) + "[]",
        _ => throw new NotImplementedException(
            $"Canonical name for data meta-type {dataType.GetType()} not implemented"),
    };

    // A SqlTypeSpecifier element wrapping a single Type reference by canonical name.
    private static Element MakeTypeSpecifierElement(string typeName) =>
        new(PostgresElementTypes.SqlTypeSpecifier)
        {
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.Type)
                {
                    new Reference(typeName)
                    {
                        ExternalSource = "BuiltIns",
                    }
                }
            }
        };

    // Emits the identity sequence-option properties (issue #13) in a fixed order —
    // StartValue, Increment, MinValue, MaxValue, CacheSize, IsCycling — omitting any
    // option equal to the Postgres default for the column's type and sequence direction,
    // so the model hash-matches a DB extraction (which reports defaults filled in).
    private static void AddIdentitySequenceOptionProperties(
        Element element, IdentityColumnConstraint identity, DataType dataType)
    {
        var canonicalType = dataType is BuiltInDataType builtIn
            ? builtIn.Type.CanonicalName()
            : "integer";

        var increment = identity.Increment ?? PostgresIdentitySequenceDefaults.Increment;
        var (defaultStart, defaultMin, defaultMax) =
            PostgresIdentitySequenceDefaults.For(canonicalType, increment);

        if (identity.StartValue is { } startValue && startValue != defaultStart)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.StartValue, startValue));
        }

        if (increment != PostgresIdentitySequenceDefaults.Increment)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.Increment, increment));
        }

        if (identity.MinValue is { } minValue && minValue != defaultMin)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.MinValue, minValue));
        }

        if (identity.MaxValue is { } maxValue && maxValue != defaultMax)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.MaxValue, maxValue));
        }

        if (identity.CacheSize is { } cacheSize && cacheSize != PostgresIdentitySequenceDefaults.CacheSize)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.CacheSize, cacheSize));
        }

        if (identity.Cycle is { } cycle && cycle != PostgresIdentitySequenceDefaults.IsCycling)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsCycling, cycle));
        }
    }

    private static Element MakeCreateIndexElement(CreateIndexStatement createIndexStatement)
    {
        if (createIndexStatement.Name is null)
        {
            // TODO.PI: determine anonymous index naming convention for Postgres
            throw new NotImplementedException("Unnamed CREATE INDEX statements are not yet supported");
        }

        if (createIndexStatement.OnRelation.Only || createIndexStatement.OnRelation.Star)
        {
            throw new NotImplementedException("ONLY and descendant table syntax on CREATE INDEX are not yet supported");
        }

        // The table an index is ON may be schema-qualified (ON staging.film); split off the
        // schema so the table reference, column references, and index name are all bare —
        // matching the DB-extraction builder — with the schema carried separately.
        var (schema, tableName) = SplitSchema(createIndexStatement.OnRelation.Name);

        var indexName = SqlName.Object(createIndexStatement.Name.Name);

        // btree is Postgres's implicit access method when USING is omitted. Defaulting to
        // it here (rather than leaving the method null) matches the DB builder, which reads
        // "btree" from pg_am — so a parsed index and one extracted from the database agree.
        var indexMethod = createIndexStatement.UsingMethod?.Name ?? "btree";

        // Only btree carries per-column ASC/DESC and NULLS ordering; other access methods
        // (e.g. hnsw) reject those options and the DB builder omits them. So the implicit
        // ASC / NULLS LAST defaults are only filled in for a btree index, keeping both
        // builders' models identical.
        var isBtree = string.Equals(indexMethod, "btree", StringComparison.OrdinalIgnoreCase);

        var columns = new List<PostgresModelFactory.IndexedColumn>();

        foreach (var indexElement in createIndexStatement.Elements)
        {
            // A key that is not a plain column reference is an expression index — e.g.
            // CREATE INDEX ix ON people (lower(name)). Both the bare-call and parenthesized
            // spellings reduce to the same expression here, matching PostgreSQL, which stores
            // one canonical form for both (issue #160).
            var columnReference = indexElement.Expression as ColumnReferenceExpression;

            var keyExpression = columnReference is null
                ? ExpressionSqlRenderer.Render(indexElement.Expression)
                : null;

            // When a btree index does not spell out a direction / null-order, Postgres
            // applies ASC (IsAscending = true) and NULLS LAST (NullsFirst = false); the DB
            // builder records those defaults, so the parser fills them in too.
            bool? isAscending = indexElement.Direction is IndexElementDirection direction
                ? direction == IndexElementDirection.Asc
                : isBtree ? true : null;

            bool? nullsFirst = indexElement.NullOrder is IndexElementNullOrder nullOrder
                ? nullOrder == IndexElementNullOrder.NullsFirst
                : isBtree ? false : null;

            columns.Add(new PostgresModelFactory.IndexedColumn(
                // An expression key names no column, so the index's own name stands in to give
                // the spec a stable identity.
                columnReference is not null
                    ? tableName.Child(columnReference.Identifier.Name)
                    : indexName,
                isAscending,
                nullsFirst,
                // An opclass or collation may be written schema-qualified, but only the bare
                // name is stored: pg_opclass and pg_collation report it that way, so keeping
                // the qualifier would make a qualified source re-diff against its own database.
                UnqualifiedNameOf(indexElement.OperatorClass),
                UnqualifiedNameOf(indexElement.Collation),
                keyExpression));
        }

        // INCLUDE (...) covering columns (issue #160). PostgreSQL rejects an ordering or
        // operator class on one, so only the column name is carried.
        var includedColumns = new List<SqlName>();

        foreach (var includeElement in createIndexStatement.IncludeElements)
        {
            if (includeElement.Expression is not ColumnReferenceExpression includeColumn)
            {
                throw new NotImplementedException(
                    $"INCLUDE element expression type {includeElement.Expression.GetType()} to element mapping not implemented");
            }

            includedColumns.Add(tableName.Child(includeColumn.Identifier.Name));
        }

        // A WHERE clause makes this a partial (filtered) index; render its predicate
        // back to SQL text so it can be carried in the model and re-emitted on publish.
        var filterPredicate = createIndexStatement.WhereClause is { } whereClause
            ? ExpressionSqlRenderer.Render(whereClause)
            : null;

        // WITH (...) storage parameters (e.g. HNSW's m / ef_construction). Rendered to a
        // canonical string so parsed and DB-extracted models hash-match.
        var storageParameters = createIndexStatement.WithOptions.Count > 0
            ? RenderStorageParameters(createIndexStatement.WithOptions)
            : null;

        // A TABLESPACE is accepted but not modeled, and only pg_default may be named.
        // Measured: an index in pg_default stores reltablespace = 0, exactly as one with no
        // TABLESPACE clause does, and pg_get_indexdef omits the clause entirely — so the
        // default spelling is a genuine no-op that can be dropped without losing anything. Any
        // other tablespace is a real placement decision that would be silently lost, so it is
        // rejected rather than ignored (issue #160).
        if (createIndexStatement.TableSpace is { } tablespace
            && !string.Equals(tablespace.Name, "pg_default", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Index '{createIndexStatement.Name.Name}' declares TABLESPACE "
                + $"'{tablespace.Name}', which Squill does not model. Only the default "
                + "tablespace (pg_default) is supported.");
        }

        // NOTE: CONCURRENTLY and IF NOT EXISTS affect how the index gets created, not the desired schema state
        return PostgresModelFactory.CreateIndex(
            indexName,
            tableName,
            createIndexStatement.Unique,
            indexMethod,
            columns,
            filterPredicate,
            storageParameters,
            schema,
            includedColumns,
            createIndexStatement.NullsNotDistinct);
    }

    /// <summary>
    /// The bare name of an optionally schema-qualified <c>any_name</c> — an index operator class
    /// or collation. The catalog reports both unqualified, so the qualifier is dropped to keep a
    /// qualified source and its own database from disagreeing (issue #160).
    /// </summary>
    private static string? UnqualifiedNameOf(QualifiedName? name)
        => name?.Segments[^1].Name;

    private static Element MakeCreateExtensionElement(CreateExtensionStatement createExtensionStatement)
    {
        // Extensions are database-level, standalone objects identified by name.
        // The declared version (if any) is carried through; SCHEMA is not yet modeled.
        var extensionName = SqlName.Object(createExtensionStatement.Name.Name);

        return PostgresModelFactory.CreateExtension(
            extensionName, createExtensionStatement.Version, createExtensionStatement.Cascade);
    }

    // An enum type (issue #75) is a standalone declared object; its labels are carried in
    // declaration order. The name is split into schema + bare name the same way a table is,
    // so an unqualified type lands in "public" and hash-matches an extracted model.
    private static Element MakeCreateEnumTypeElement(CreateEnumTypeStatement createEnumTypeStatement)
    {
        var (schema, name) = SplitSchema(createEnumTypeStatement.Name);

        return PostgresModelFactory.CreateEnumType(name, schema, createEnumTypeStatement.Labels);
    }

    // A composite type (issue #122). Its attributes are modeled as SqlSimpleColumn elements —
    // the same shape a table's columns take — so the existing type-specifier machinery carries
    // each attribute's type, modifiers included, and both model builders agree by construction.
    private static Element MakeCreateCompositeTypeElement(
        CreateCompositeTypeStatement createCompositeTypeStatement)
    {
        var (schema, name) = SplitSchema(createCompositeTypeStatement.Name);

        var attributes = new List<Element>();

        foreach (var attribute in createCompositeTypeStatement.Attributes)
        {
            var element = new Element(PostgresElementTypes.SqlSimpleColumn)
            {
                Name = name.Child(attribute.Name.Name),
                Relationships =
                {
                    new Relationship(PostgresRelationshipNames.TypeSpecifier)
                    {
                        BuildTypeSpecifier(attribute.DataType),
                    },
                },
            };

            attributes.Add(element);
        }

        return PostgresModelFactory.CreateCompositeType(name, schema, attributes);
    }

    // A range type (issue #122). The subtype is normalized to its canonical name so a declared
    // `float8` matches the `double precision` the catalog reports -- which matters here in
    // particular because PostgreSQL's own CREATE TYPE ... AS RANGE documentation writes
    // SUBTYPE = float8. The alias itself is resolved by the parser (issue #197), so the ordinary
    // canonical-name rules are all this needs.
    private static Element MakeCreateRangeTypeElement(
        CreateRangeTypeStatement createRangeTypeStatement)
    {
        var (schema, name) = SplitSchema(createRangeTypeStatement.Name);

        return PostgresModelFactory.CreateRangeType(
            name,
            schema,
            CanonicalTypeName(createRangeTypeStatement.Subtype),
            createRangeTypeStatement.SubtypeOperatorClass,
            createRangeTypeStatement.Collation);
    }

    // A collation (issue #159). The declared items are resolved into the facets pg_collation
    // stores, because that is all the catalog keeps: LOCALE fans out to LC_COLLATE / LC_CTYPE
    // for libc and stays as the locale for icu. A collation declared FROM another is rejected
    // rather than modeled — resolving it needs the copied collation's own locale, which for a
    // stock one like "POSIX" lives in the target database and not in the source, so the model
    // could not be built without a live server (which is exactly what this builder avoids).
    private static Element MakeCreateCollationElement(CreateCollationStatement statement)
    {
        var (schema, name) = SplitSchema(statement.Name);

        if (statement.CopiedFrom is { } copiedFrom)
        {
            throw new NotSupportedException(
                $"Collation '{name.UnqualifiedName}' is declared FROM \"{copiedFrom}\", which "
                + "Squill cannot model: PostgreSQL stores the copied locale rather than the "
                + "reference, so the declaration cannot be reproduced without resolving it "
                + "against a live server. Declare the locale directly, e.g. "
                + "(LOCALE = 'POSIX', PROVIDER = libc).");
        }

        // libc is the provider PostgreSQL assumes when none is declared.
        var provider = statement.Provider ?? "libc";

        // For libc, LOCALE sets both LC_COLLATE and LC_CTYPE; an explicit pair overrides it.
        // For icu, the locale is stored as-is and the lc_* facets stay empty.
        var isIcu = string.Equals(provider, "icu", StringComparison.Ordinal);

        return PostgresModelFactory.CreateCollation(
            name,
            schema,
            provider,
            locale: isIcu ? statement.Locale : null,
            lcCollate: isIcu ? null : statement.LcCollate ?? statement.Locale,
            lcCtype: isIcu ? null : statement.LcCtype ?? statement.Locale,
            isDeterministic: statement.Deterministic ?? true);
    }

    // A standalone sequence (issue #122). The declared options are handed to the factory
    // exactly as written; it decides which are worth storing by comparing each against the
    // PostgreSQL default for the sequence's type and direction.
    private static Element MakeCreateSequenceElement(CreateSequenceStatement createSequenceStatement)
    {
        var (schema, name) = SplitSchema(createSequenceStatement.Name);

        return PostgresModelFactory.CreateSequence(
            name,
            schema,
            SequenceTypeName(createSequenceStatement.DataType),
            createSequenceStatement.StartValue,
            createSequenceStatement.Increment,
            createSequenceStatement.MinValue,
            createSequenceStatement.MaxValue,
            createSequenceStatement.CacheSize,
            createSequenceStatement.IsCycling);
    }

    // The canonical spelling of a sequence's AS type, so `int4` and `integer` produce the same
    // model and both match what the catalog reports. The parser has already rejected anything
    // that is not one of the three integer types.
    private static string? SequenceTypeName(DataType? dataType) => dataType switch
    {
        null => null,
        BuiltInDataType { Type: PostgresBuiltInDataType.SmallInt } => "smallint",
        BuiltInDataType { Type: PostgresBuiltInDataType.Integer } => "integer",
        BuiltInDataType { Type: PostgresBuiltInDataType.BigInt } => "bigint",
        _ => throw new NotImplementedException(
            $"A sequence may only be declared AS smallint, integer or bigint, not "
            + $"'{dataType.TypeName}'"),
    };

    // In a domain CHECK, VALUE is the keyword for the value being checked, so it is rendered
    // bare rather than double-quoted.
    private static readonly HashSet<string> DomainCheckBareIdentifiers = new(StringComparer.Ordinal)
    {
        "VALUE",
    };

    // A domain (issue #75) is a standalone declared object: a base type plus an optional
    // CHECK. The base type reuses the column type-specifier shape; the CHECK expression is
    // rendered to canonical text. Any other constraint (NOT NULL, etc.) is not modeled yet.
    private static Element MakeCreateDomainElement(CreateDomainStatement createDomainStatement)
    {
        var (schema, name) = SplitSchema(createDomainStatement.Name);

        var typeSpecifier = BuildTypeSpecifier(createDomainStatement.DataType);

        string? checkExpression = null;

        foreach (var domainConstraint in createDomainStatement.Constraints)
        {
            var constraint = domainConstraint is NamedColumnConstraint named
                ? named.Constraint
                : domainConstraint;

            if (constraint is CheckColumnConstraint check)
            {
                if (checkExpression is not null)
                {
                    throw new NotImplementedException(
                        $"Domain '{name.UnqualifiedName}' has more than one CHECK constraint, "
                        + "which is not yet supported.");
                }

                // VALUE is the domain keyword for the value being checked; it must be
                // rendered bare, not double-quoted (a quoted "VALUE" is read as a column).
                checkExpression = ExpressionSqlRenderer.Render(
                    check.Expression, DomainCheckBareIdentifiers);
            }
            else
            {
                // NOT NULL and other domain constraints are not modeled yet (they still parse).
                throw new NotImplementedException(
                    $"Domain constraint type {constraint.GetType()} is not yet supported; "
                    + "only CHECK is modeled.");
            }
        }

        return PostgresModelFactory.CreateDomain(name, schema, typeSpecifier, checkExpression);
    }

    // Renders index storage parameters (the WITH clause) to a canonical
    // "name=value, name=value" string, preserving declaration order. This is the same
    // shape the DB builder produces from pg_class.reloptions, so a parsed index and one
    // extracted from the database hash-match.
    private static string RenderStorageParameters(IEnumerable<IndexWithOption> options)
        => string.Join(", ", options.Select(o => o.Value is null ? o.Name : $"{o.Name}={o.Value}"));

    private static SqlName ToSqlName(QualifiedName qualifiedName)
        => SqlName.Object(qualifiedName.Segments.Select(i => i.Name).ToArray());

    // Normalizes a foreign key's referenced-table name to the convention both builders
    // share: bare when it resolves to the public schema (matching an unqualified source
    // reference and the DB builder's bare public names), schema-qualified otherwise (so a
    // cross-schema FK round-trips). e.g. `public.book` -> book; `audit.log` -> audit.log.
    private static SqlName NormalizeReferencedTable(QualifiedName qualifiedName)
    {
        var (schema, name) = SplitSchema(qualifiedName);

        return schema == "public" ? name : SqlName.Object(schema, name.UnqualifiedName);
    }

    // Splits a (possibly schema-qualified) table name into its schema and its bare,
    // schema-less name, mirroring how the DB builder stores tables: the element Name is
    // the bare table name (e.g. "film") and the schema is carried as a separate Schema
    // relationship. A CREATE TABLE with no schema qualifier defaults to "public", which
    // is where Postgres puts it — so both builders agree.
    /// <summary>
    /// PostgreSQL's internal type-name aliases mapped to the canonical spelling
    /// format_type() reports. Taken from pg_catalog, where these are the base types whose
    /// typname differs from their formatted name.
    ///
    /// The 1-byte internal <c>"char"</c> type is deliberately absent: the grammar already
    /// resolves a written <c>char</c> to <c>character</c>, and mapping it here would turn
    /// <c>char(3)</c> into the wrong type.
    /// </summary>
    private static readonly Dictionary<string, string> PostgresTypeAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["bool"] = "boolean",
            ["bpchar"] = "character",
            ["float4"] = "real",
            ["float8"] = "double precision",
            ["int2"] = "smallint",
            ["int4"] = "integer",
            ["int8"] = "bigint",
            ["time"] = "time without time zone",
            ["timestamp"] = "timestamp without time zone",
            ["timestamptz"] = "timestamp with time zone",
            ["timetz"] = "time with time zone",
            ["varbit"] = "bit varying",
            ["varchar"] = "character varying",
        };

    /// <summary>
    /// Builds a view element, resolving the names of the columns it exposes.
    ///
    /// An explicit column list names them outright. Otherwise each select-list entry
    /// contributes one name: an alias, a plain column's own name, or — for a <c>*</c> — the
    /// columns of the table it expands over, which is why this runs only after every table
    /// in the workspace is known. An entry that names nothing (an unaliased expression) and
    /// a <c>*</c> that cannot be resolved to a single table are build errors rather than
    /// guesses, since guessing wrong would silently model the wrong shape.
    /// </summary>
    private static Element MakeCreateViewElement(CreateViewStatement statement, SourceValidator validator)
    {
        var (schema, name) = SplitSchema(statement.Name);

        if (statement.Body is not { } definition)
        {
            throw new InvalidOperationException("A view must declare a query");
        }

        var columnNames = statement.ColumnNames.Count > 0
            ? statement.ColumnNames.Select(i => i.Name).ToList()
            : DeriveViewColumnNames(statement, schema, validator);

        if (columnNames.Count == 0)
        {
            throw new InvalidOperationException(
                $"View '{schema}.{name.UnqualifiedName}' exposes no columns");
        }

        return PostgresModelFactory.CreateView(
            SqlName.Object(schema, name.UnqualifiedName), schema, columnNames, definition);
    }

    private static List<string> DeriveViewColumnNames(
        CreateViewStatement statement,
        string viewSchema,
        SourceValidator validator)
    {
        var names = new List<string>();

        foreach (var column in statement.SelectColumns)
        {
            if (column.IsWildcard)
            {
                names.AddRange(ExpandWildcard(statement, viewSchema, column.Qualifier, validator));

                continue;
            }

            if (column.DerivedName is not { } derivedName)
            {
                throw new NotSupportedException(
                    "A view's select list may only contain columns and aliased expressions; "
                    + "give the expression an alias (e.g. SELECT qty * 2 AS doubled) so the "
                    + "column has a name.");
            }

            names.Add(derivedName);
        }

        return names;
    }

    private static IEnumerable<string> ExpandWildcard(
        CreateViewStatement statement,
        string viewSchema,
        string? qualifier,
        SourceValidator validator)
    {
        // An unqualified * over several tables is ambiguous — which table's columns come
        // first, and whether same-named columns collide, depends on the join. Rather than
        // guess, ask the author to name the columns.
        if (qualifier is null && statement.SourceTables.Count != 1)
        {
            throw new NotSupportedException(
                "A view's SELECT * cannot be expanded over more than one table; "
                + "list the columns explicitly instead.");
        }

        var table = qualifier is null
            ? statement.SourceTables[0]
            : ResolveQualifier(statement, qualifier);

        var (tableSchema, tableName) = SplitSchema(table);

        // A view in a non-public schema still selects from tables that may live anywhere;
        // an unqualified table name resolves against public, as PostgreSQL's default
        // search_path does.
        var columns = validator.GetDeclaredColumns(tableSchema, tableName.UnqualifiedName);

        if (columns is null)
        {
            // The unresolved table is already reported by the validator; this keeps the
            // view from being modeled with a wrong (empty) column list.
            throw new NotSupportedException(
                $"View cannot expand SELECT * because table "
                + $"'{tableSchema}.{tableName.UnqualifiedName}' is not defined in the project.");
        }

        return columns;
    }

    private static QualifiedName ResolveQualifier(CreateViewStatement statement, string qualifier)
    {
        // The qualifier on `t.*` is either a source table's own name or an alias for one.
        // Only the former can be resolved without modeling the FROM clause's aliases, which
        // is why an alias that does not match a table name is rejected.
        foreach (var sourceTable in statement.SourceTables)
        {
            if (string.Equals(SplitSchema(sourceTable).Name.UnqualifiedName, qualifier,
                    StringComparison.OrdinalIgnoreCase))
            {
                return sourceTable;
            }
        }

        throw new NotSupportedException(
            $"A view's SELECT {qualifier}.* refers to '{qualifier}', which is not one of the "
            + "tables it selects from; list the columns explicitly instead.");
    }

    private static Element MakeCreateProcedureElement(CreateProcedureStatement statement)
    {
        var (schema, name) = SplitSchema(statement.Name);

        if (statement.Language is not { } language)
        {
            throw new InvalidOperationException("A procedure must declare a LANGUAGE");
        }

        if (statement.Body is not { } body)
        {
            throw new InvalidOperationException("A procedure must declare a body");
        }

        // The parameter type is stored normalized (modifiers discarded) rather than as
        // written, because that is all PostgreSQL retains for a routine parameter — a
        // `varchar(10)` parameter is not length-checked, and the catalog reports it as
        // plain `character varying`. Storing the declared form would mean a parsed model
        // could never hash-match an extracted one.
        // A parameter DEFAULT is not modeled: PostgreSQL rewrites the expression when it
        // stores it (a bare 'x' comes back as 'x'::text), so a parsed model could not
        // hash-match an extracted one. Rejecting it keeps the gap explicit rather than
        // silently deploying a procedure that loses its defaults.
        if (statement.Parameters.FirstOrDefault(i => i.DefaultExpression is not null) is { } withDefault)
        {
            throw new NotImplementedException(
                $"A DEFAULT on procedure parameter '{withDefault.Name?.Name ?? "(unnamed)"}' "
                + "is not yet supported");
        }

        var parameters = statement.Parameters
            .Select(parameter => new PostgresModelFactory.ProcedureParameter(
                RenderParameterMode(parameter.Mode),
                parameter.Name?.Name,
                NormalizeArgumentType(parameter.DataType)))
            .ToList();

        // Only IN and INOUT parameters form a procedure's identity, matching
        // pg_proc.proargtypes — which is what the DB-extraction builder reads back.
        var argumentTypes = string.Join(',', statement.Parameters
            .Where(i => i.Mode is ParameterMode.In or ParameterMode.InOut or ParameterMode.Variadic)
            .Select(i => NormalizeArgumentType(i.DataType)));

        return PostgresModelFactory.CreateProcedure(
            schema,
            name.UnqualifiedName,
            argumentTypes,
            language,
            body,
            parameters,
            statement.SecurityDefiner);
    }

    private static Element MakeCreateFunctionElement(CreateFunctionStatement statement)
    {
        var (schema, name) = SplitSchema(statement.Name);

        if (statement.Language is not { } language)
        {
            throw new InvalidOperationException("A function must declare a LANGUAGE");
        }

        if (statement.Body is not { } body)
        {
            throw new InvalidOperationException("A function must declare a body");
        }

        if (statement.ReturnType is not { } returnType)
        {
            throw new InvalidOperationException("A function must declare a RETURNS type");
        }

        // Parameter DEFAULTs are not modeled for the same reason as on a procedure: Postgres
        // rewrites the expression when it stores it, so a parsed model could not hash-match
        // an extracted one.
        if (statement.Parameters.FirstOrDefault(i => i.DefaultExpression is not null) is { } withDefault)
        {
            throw new NotImplementedException(
                $"A DEFAULT on function parameter '{withDefault.Name?.Name ?? "(unnamed)"}' "
                + "is not yet supported");
        }

        var parameters = statement.Parameters
            .Select(parameter => new PostgresModelFactory.ProcedureParameter(
                RenderParameterMode(parameter.Mode),
                parameter.Name?.Name,
                NormalizeArgumentType(parameter.DataType)))
            .ToList();

        var argumentTypes = string.Join(',', statement.Parameters
            .Where(i => i.Mode is ParameterMode.In or ParameterMode.InOut or ParameterMode.Variadic)
            .Select(i => NormalizeArgumentType(i.DataType)));

        // The return type is normalized the same way parameter types are, so it matches
        // format_type(prorettype) the DB builder reads back.
        var normalizedReturnType = NormalizeArgumentType(returnType);

        return PostgresModelFactory.CreateFunction(
            schema,
            name.UnqualifiedName,
            argumentTypes,
            normalizedReturnType,
            statement.ReturnsSet,
            language,
            body,
            parameters,
            statement.Volatility is { } volatility ? RenderVolatility(volatility) : null,
            statement.Strict ?? false,
            statement.SecurityDefiner);
    }

    private static string RenderVolatility(FunctionVolatility volatility) => volatility switch
    {
        FunctionVolatility.Immutable => "IMMUTABLE",
        FunctionVolatility.Stable => "STABLE",
        FunctionVolatility.Volatile => "VOLATILE",
        _ => throw new NotImplementedException($"Volatility {volatility} is not supported"),
    };

    private static Element MakeCreateAggregateElement(CreateAggregateStatement statement)
    {
        var (schema, name) = SplitSchema(statement.Name);

        if (statement.StateFunction is not { } stateFunction)
        {
            throw new InvalidOperationException("An aggregate must declare an SFUNC");
        }

        if (statement.StateType is not { } stateType)
        {
            throw new InvalidOperationException("An aggregate must declare an STYPE");
        }

        // An aggregate parameter is always plain IN — there are no OUT/INOUT aggregate inputs
        // — so the whole parameter list is the signature.
        var parameters = statement.Parameters
            .Select(parameter => new PostgresModelFactory.ProcedureParameter(
                RenderParameterMode(parameter.Mode),
                parameter.Name?.Name,
                NormalizeArgumentType(parameter.DataType)))
            .ToList();

        var argumentTypes = string.Join(',', statement.Parameters
            .Select(i => NormalizeArgumentType(i.DataType)));

        // The SFUNC is stored schema-qualified so it matches what pg_proc reports back; a
        // bare name is assumed to live in the same schema as the aggregate (Postgres resolves
        // it against the search path, but the extracted form is always qualified).
        var qualifiedStateFunction = stateFunction.Contains('.')
            ? stateFunction
            : $"{schema}.{stateFunction}";

        // The STYPE is normalized the same way an argument type is, so it matches
        // format_type(aggtranstype) the DB builder reads back.
        var normalizedStateType = NormalizeArgumentType(stateType);

        return PostgresModelFactory.CreateAggregate(
            schema,
            name.UnqualifiedName,
            argumentTypes,
            qualifiedStateFunction,
            normalizedStateType,
            parameters);
    }

    private static Element MakeCreateTriggerElement(CreateTriggerStatement statement)
    {
        var (schema, table) = SplitSchema(statement.Table);

        if (statement.FunctionName is not { } functionName)
        {
            throw new InvalidOperationException("A trigger must declare a function to execute");
        }

        // The function is stored the way it was written: bare when unqualified, schema.name
        // when qualified. A bare name is NOT force-qualified with the table's schema — the
        // function is commonly a built-in (tsvector_update_trigger) that lives in pg_catalog,
        // not public, and would be unresolvable if qualified with the wrong schema. Storing it
        // bare lets PostgreSQL resolve it through the search path, and the DB-extraction
        // builder likewise strips a public/pg_catalog prefix so both sides store the same bare
        // name and hash-match.
        var (functionSchema, functionBareName) = SplitSchema(functionName);

        var storedFunction = functionName.Segments.Count > 1
            ? $"{functionSchema}.{functionBareName.UnqualifiedName}"
            : functionBareName.UnqualifiedName;

        return PostgresModelFactory.CreateTrigger(
            schema,
            statement.Name,
            table,
            RenderTiming(statement.Timing),
            RenderEvents(statement.Events),
            RenderLevel(statement.Level),
            storedFunction,
            string.Join(", ", statement.FunctionArguments));
    }

    private static string RenderTiming(TriggerTiming timing) => timing switch
    {
        TriggerTiming.Before => "BEFORE",
        TriggerTiming.After => "AFTER",
        TriggerTiming.InsteadOf => "INSTEAD OF",
        _ => throw new NotImplementedException($"Trigger timing {timing} is not supported"),
    };

    // Renders the OR'd events in a fixed order (INSERT, DELETE, UPDATE, TRUNCATE) so the model
    // is canonical regardless of how they were written; pg_get_triggerdef reports them in this
    // same order, so a parsed model hash-matches an extracted one.
    private static string RenderEvents(TriggerEvents events)
    {
        var parts = new List<string>();

        if (events.HasFlag(TriggerEvents.Insert))
        {
            parts.Add("INSERT");
        }

        if (events.HasFlag(TriggerEvents.Delete))
        {
            parts.Add("DELETE");
        }

        if (events.HasFlag(TriggerEvents.Update))
        {
            parts.Add("UPDATE");
        }

        if (events.HasFlag(TriggerEvents.Truncate))
        {
            parts.Add("TRUNCATE");
        }

        if (parts.Count == 0)
        {
            throw new InvalidOperationException("A trigger must fire on at least one event");
        }

        return string.Join(" OR ", parts);
    }

    private static string RenderLevel(TriggerLevel level) => level switch
    {
        TriggerLevel.Row => "ROW",
        TriggerLevel.Statement => "STATEMENT",
        _ => throw new NotImplementedException($"Trigger level {level} is not supported"),
    };

    private static string RenderParameterMode(ParameterMode mode) => mode switch
    {
        ParameterMode.In => "IN",
        ParameterMode.Out => "OUT",
        ParameterMode.InOut => "INOUT",
        ParameterMode.Variadic => "VARIADIC",
        _ => throw new NotImplementedException($"Parameter mode {mode} is not supported"),
    };

    /// <summary>
    /// Renders a parameter's type the way PostgreSQL records it in a routine's argument
    /// signature: the canonical type name with all modifiers discarded, so `varchar(10)`
    /// becomes `character varying` and `numeric(5,2)` becomes `numeric`. This must match
    /// format_type() exactly, since the DB-extraction builder reads the signature back
    /// through it and the two models are compared by hash.
    /// </summary>
    private static string NormalizeArgumentType(DataType dataType)
    {
        if (dataType is ArrayDataType arrayDataType)
        {
            return $"{NormalizeArgumentType(arrayDataType.ElementType)}[]";
        }

        if (dataType is not BuiltInDataType builtIn)
        {
            var typeName = dataType.TypeName.ToLowerInvariant();

            // PostgreSQL's internal type aliases are not recognized as built-ins by the
            // grammar, but format_type always reports the canonical spelling — so map them
            // here or a signature written with an alias would never hash-match.
            // See https://www.postgresql.org/docs/current/datatype.html
            return PostgresTypeAliases.TryGetValue(typeName, out var canonical) ? canonical : typeName;
        }

        // A bare `timestamp`/`time` is spelled out in full in an argument signature, unlike
        // in a column type specifier where the short form is what the catalog reports.
        return builtIn.Type switch
        {
            PostgresBuiltInDataType.Timestamp => "timestamp without time zone",
            PostgresBuiltInDataType.Time => "time without time zone",
            _ => builtIn.Type.CanonicalName(),
        };
    }

    private static (string Schema, SqlName Name) SplitSchema(QualifiedName qualifiedName)
    {
        var segments = qualifiedName.Segments.Select(i => i.Name).ToArray();

        return segments.Length switch
        {
            1 => ("public", SqlName.Object(segments[0])),
            2 => (segments[0], SqlName.Object(segments[1])),
            // A 3-part name (catalog.schema.object) would silently drop the catalog; reject
            // it rather than deploy to a different place than written.
            _ => throw new NotImplementedException(
                $"Catalog-qualified names ({string.Join('.', segments)}) are not supported; "
                + "use schema.object."),
        };
    }
}