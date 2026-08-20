using Squill.Core;
using Squill.MariaDbParser;
using Squill.MariaDbParser.Syntax;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Extracts a <see cref="Model"/> from declarative MariaDB SQL source by parsing it with the
/// ANTLR-based parser (no live database needed). Elements are built through
/// <see cref="MariaDbModelFactory"/> and named to match MariaDB's own auto-generated
/// constraint names (a <c>PRIMARY</c> key, <c>&lt;table&gt;_ibfk_N</c> foreign keys) so a
/// parsed model hash-matches one extracted from a live database.
///
/// The target <see cref="MariaDbFamilyDatabaseSchemaProvider"/> is required rather than
/// defaulted: a handful of constructs — currently the time-function column <c>DEFAULT</c>s
/// (issue #147) — canonicalize differently on each engine, and silently assuming the wrong one
/// would produce a model that re-diffs against its own database on every deploy. The provider
/// declares the relevant capabilities, so this builder never tests for a particular engine.
/// </summary>
public class ParserWorkspaceModelBuilder : IWorkspaceModelBuilder
{
    private readonly Workspace _workspace;
    private readonly IMariaDbParser _parser;
    private readonly MariaDbFamilyDatabaseSchemaProvider _schemaProvider;

    public ParserWorkspaceModelBuilder(
        Workspace workspace,
        IMariaDbParser parser,
        MariaDbFamilyDatabaseSchemaProvider schemaProvider)
    {
        _workspace = workspace;
        _parser = parser;
        _schemaProvider = schemaProvider;
    }

    public async Task<BuildResult> ExtractModelAsync(CancellationToken cancellationToken = default)
    {
        var model = new Model();
        var validator = new SourceValidator(_schemaProvider);
        var warnings = new List<SqlSourceDiagnostic>();
        var views = new List<PendingView>();

        foreach (var file in _workspace.Files.Where(i => i.Kind == FileKind.Compile))
        {
            await ProcessFile(file, model, validator, warnings, views, cancellationToken);
        }

        SortTablesByName(model);

        // Views are added after the tables are sorted (so they are not dragged around as a
        // table's dependents) and only once every table is known: expanding a SELECT * needs
        // the referenced table's columns, which may be declared in a later file. This runs
        // before validation so a broken view is reported alongside every other source error
        // rather than on a later rebuild (issue #61).
        AddViews(model, validator, views, warnings, _schemaProvider);

        // Validated after every file so declaration order (within and across files) does
        // not matter, just like it doesn't for the deployed schema. Parse and mapping errors
        // collected above are reported alongside, so one build surfaces every problem
        // rather than one per rebuild (issue #61).
        validator.ThrowIfInvalid();

        MoveRoutinesToEnd(model);
        MoveTriggersToEnd(model);
        MoveEventsToEnd(model);

        return new BuildResult(model, warnings);
    }

    // A view whose element cannot be built until every table in the workspace has been
    // seen, kept with the file and position to report any failure against.
    private sealed record PendingView(IFile File, CreateViewStatement Statement);

    /// <summary>
    /// Adds every view after the tables, ordered by name. The database-extraction builder
    /// reads views in that order (information_schema has no notion of declaration order) and
    /// the Merkle hash is order-sensitive, so a parsed model must adopt the same order.
    /// </summary>
    private static void AddViews(Model model,
        SourceValidator validator,
        List<PendingView> views,
        List<SqlSourceDiagnostic> warnings,
        MariaDbFamilyDatabaseSchemaProvider schemaProvider)
    {
        // Ordinal, to match the database's byte-wise ordering of the same names.
        foreach (var view in views.OrderBy(i => i.Statement.Name.Name, StringComparer.Ordinal))
        {
            try
            {
                model.Elements.Add(
                    MakeCreateViewElement(view.Statement, validator, schemaProvider));

                AddUnmodeledViewOptionWarnings(view.File, view.Statement, warnings, schemaProvider);
            }
            catch (Exception ex) when (ex is NotImplementedException or NotSupportedException
                or InvalidOperationException)
            {
                // Recorded rather than thrown, so a build reports every broken view at once
                // alongside the other source errors (issue #61).
                validator.AddError(new SqlSourceException(
                    ex.Message, view.File.Name, view.Statement.Line, view.Statement.Column,
                    SqlSourceException.UnresolvedReference, ex));
            }
        }
    }

    /// <summary>
    /// Moves routines — procedures and functions — after every other element, ordered by
    /// name. The database-extraction builder emits them last in that order (information_schema
    /// has no notion of the order they were declared in, and reads both routine kinds together
    /// ordered by name) and the Merkle hash is order-sensitive, so a parsed model must adopt
    /// the same order to hash-match an extracted one.
    ///
    /// This also has to run after <see cref="SortTablesByName"/>, which groups each table
    /// with the elements that follow it — a routine left in place would be treated as one of
    /// the preceding table's dependents and dragged around by that table's name.
    ///
    /// Ordering routines last matches the create order too, since a routine body may
    /// reference any table.
    /// </summary>
    private static void MoveRoutinesToEnd(Model model)
    {
        var routines = model.Elements
            .Where(i => i.Type is MariaDbElementTypes.SqlProcedure or MariaDbElementTypes.SqlFunction)
            .ToList();

        if (routines.Count == 0)
        {
            return;
        }

        foreach (var routine in routines)
        {
            model.Elements.Remove(routine);
        }

        // Ordinal, to match the database's byte-wise ordering of the same names.
        foreach (var routine in routines.OrderBy(i => i.Name as string, StringComparer.Ordinal))
        {
            model.Elements.Add(routine);
        }
    }

    /// <summary>
    /// Moves triggers after every other element (including routines), ordered by their bare
    /// trigger name. The database-extraction builder emits them last in that order
    /// (information_schema orders by TRIGGER_NAME and has no notion of declaration order) and
    /// the Merkle hash is order-sensitive, so a parsed model must adopt the same order to
    /// hash-match an extracted one.
    ///
    /// This runs after <see cref="MoveRoutinesToEnd"/> so a trigger lands after the routines,
    /// matching the extraction order (tables, views, routines, then triggers). It orders by
    /// the trigger's own name — held in <see cref="MariaDbPropertyNames.RoutineName"/> — not
    /// the element Name, which folds in the table (table.trigger) and would sort differently.
    /// </summary>
    /// <summary>
    /// Moves every scheduled event to the end of the model, ordered by name — the order
    /// information_schema.EVENTS is read in, which has no notion of declaration order. Runs
    /// after <see cref="MoveTriggersToEnd"/> so events land last, matching the extraction
    /// order (tables, views, routines, triggers, then events). The Merkle hash is
    /// order-sensitive, so the two builders must agree.
    ///
    /// Unlike a trigger, an event's element Name is its own bare name (it is bound to no
    /// table), so it is ordered on that directly.
    /// </summary>
    private static void MoveEventsToEnd(Model model)
    {
        var events = model.Elements
            .Where(i => i.Type == MariaDbElementTypes.SqlEvent)
            .ToList();

        if (events.Count == 0)
        {
            return;
        }

        foreach (var element in events)
        {
            model.Elements.Remove(element);
        }

        // Ordinal, to match the database's byte-wise ordering of the same names.
        foreach (var element in events.OrderBy(i => (string)i.Name!, StringComparer.Ordinal))
        {
            model.Elements.Add(element);
        }
    }

    private static void MoveTriggersToEnd(Model model)
    {
        var triggers = model.Elements
            .Where(i => i.Type == MariaDbElementTypes.SqlTrigger)
            .ToList();

        if (triggers.Count == 0)
        {
            return;
        }

        foreach (var trigger in triggers)
        {
            model.Elements.Remove(trigger);
        }

        // Ordinal, to match the database's byte-wise ordering of the same names.
        foreach (var trigger in triggers.OrderBy(
                     i => i.GetProperty<string>(MariaDbPropertyNames.RoutineName), StringComparer.Ordinal))
        {
            model.Elements.Add(trigger);
        }
    }

    /// <summary>
    /// Orders tables by name, keeping each table's dependents (primary key, indexes,
    /// foreign keys) immediately after it.
    ///
    /// The database-extraction builder reads tables with ORDER BY TABLE_NAME, because
    /// information_schema has no notion of the order they were declared in. The Merkle hash
    /// is order-sensitive, so a parsed model has to adopt the same order or it would only
    /// hash-match when the source happened to be written alphabetically.
    /// </summary>
    private static void SortTablesByName(Model model)
    {
        // Group each table with the dependents that follow it, so a group moves as a unit.
        var groups = new List<(string Name, List<Element> Elements)>();

        foreach (var element in model.Elements)
        {
            if (element.Type == MariaDbElementTypes.SqlTable)
            {
                groups.Add((element.Name as string ?? string.Empty, [element]));
            }
            else if (groups.Count > 0)
            {
                groups[^1].Elements.Add(element);
            }
            else
            {
                // An element before any table has nothing to attach to; leave it in place
                // by giving it its own group that sorts first.
                groups.Add((string.Empty, [element]));
            }
        }

        // Ordinal, to match the database's byte-wise ordering of the same names.
        var ordered = groups
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .SelectMany(i => i.Elements)
            .ToList();

        model.Elements.Clear();

        foreach (var element in ordered)
        {
            model.Elements.Add(element);
        }
    }

    /// <summary>
    /// Parses one file and maps its statements into the model. A syntax error aborts only
    /// this file — it is recorded and the remaining files are still parsed, so a build
    /// reports every broken file at once. A statement that cannot be mapped is likewise
    /// recorded and the rest of the file continues (issue #61).
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
            root = _parser.Parse(text);
        }
        catch (MariaDbParseException ex)
        {
            validator.AddError(new SqlSourceException(
                ex.Message, file.Name, ex.Line, ex.Column, innerException: ex));

            // The file did not parse, so it contributes no statements; carry on with the
            // next file rather than aborting the whole build here.
            return;
        }

        foreach (var statement in root.Statements)
        {
            try
            {
                switch (statement)
                {
                    case CreateTableStatement createTable:
                        // A temporary table models and deploys as an ordinary permanent one,
                        // which is not what the source declares (issue #204). It belongs to the
                        // connection that created it and is dropped when that connection closes,
                        // so it can never be part of a schema a deploy converges on: the next
                        // extraction would not find it, and every deploy would recreate it.
                        // Rejected before the validator registers it, so a reference to it reads
                        // as unresolved rather than silently binding to a table that will not
                        // exist. Postgres rejects the same declaration for the same reason.
                        if (createTable.IsTemporary)
                        {
                            throw new NotSupportedException(
                                $"TEMPORARY on table '{createTable.Name.Name}' is not supported: "
                                + "a temporary table is not part of a declared schema, and deploying "
                                + "it as an ordinary permanent table would not match what is declared.");
                        }

                        validator.AddCreateTable(file, createTable);

                        if (validator.IsDuplicateTable(createTable))
                        {
                            break;
                        }

                        AddUnmodeledTableWarnings(file, createTable, warnings, _schemaProvider);
                        AddUnmodeledTableOptionWarnings(file, createTable, warnings);

                        // Reported alongside the unmodeled-construct warnings, not in place of
                        // them: a construct can be both too new for the target and unmodeled,
                        // and the two say different things about what will happen (issue #142).
                        MariaDbTargetVersionChecker.Check(
                            file, createTable, _schemaProvider, warnings);

                        // Independent of the version check above rather than folded into it: a
                        // deprecated construct is accepted by every version in the supported
                        // window, so it is not a target-version problem and raising the target
                        // would not resolve it (issue #190).
                        MariaDbDeprecationChecker.Check(
                            file, createTable, _schemaProvider, warnings);

                        foreach (var element in MakeCreateTableElements(createTable, _schemaProvider))
                        {
                            model.Elements.Add(element);
                        }
                        break;

                    case CreateIndexStatement createIndex:
                        validator.AddCreateIndex(file, createIndex);

                        model.Elements.Add(MakeCreateIndexElement(createIndex));
                        break;

                    case CreateProcedureStatement createProcedure:
                        validator.AddCreateProcedure(file, createProcedure);

                        if (validator.IsDuplicateProcedure(createProcedure))
                        {
                            break;
                        }

                        model.Elements.Add(MakeCreateProcedureElement(createProcedure));
                        break;

                    case CreateViewStatement createView:
                        validator.AddCreateView(file, createView);

                        // Held back until every file has been read: a SELECT * needs the
                        // referenced table's columns, and that table may be declared later.
                        views.Add(new PendingView(file, createView));
                        break;

                    case CreateFunctionStatement createFunction:
                        validator.AddCreateFunction(file, createFunction);

                        if (validator.IsDuplicateFunction(createFunction))
                        {
                            break;
                        }

                        model.Elements.Add(MakeCreateFunctionElement(createFunction));
                        break;

                    case CreateTriggerStatement createTrigger:
                        validator.AddCreateTrigger(file, createTrigger);

                        if (validator.IsDuplicateTrigger(createTrigger))
                        {
                            break;
                        }

                        model.Elements.Add(MakeCreateTriggerElement(createTrigger));
                        break;

                    case CreateEventStatement createEvent:
                        validator.AddCreateEvent(file, createEvent);

                        if (validator.IsDuplicateEvent(createEvent))
                        {
                            break;
                        }

                        model.Elements.Add(MakeCreateEventElement(createEvent));
                        break;

                    // An authored ALTER/DROP/DML is a mistake in the source, not a gap in
                    // Squill, so it is an error with its own code rather than the warning
                    // below — which let the build succeed while the statement was silently
                    // discarded, leaving the author believing it had been applied (issue #125).
                    case ImperativeStatement imperative:
                        validator.AddError(ImperativeStatementDiagnostic.Exception(
                            imperative.Name,
                            ToDiagnosticKind(imperative.Kind),
                            file.Name,
                            statement.Line,
                            statement.Column));
                        break;

                    // Recognized but not modeled (a CREATE TABLE ... AS SELECT, …). Not fatal
                    // — the rest of the project still builds — but the construct will not
                    // reach the DACPAC, so say so rather than dropping it silently.
                    case UnmodeledStatement unmodeled:
                        warnings.Add(new SqlSourceDiagnostic(
                            $"{unmodeled.Description} is not modeled by Squill and will not be "
                            + "deployed or compared.",
                            file.Name, statement.Line, statement.Column));
                        break;
                }
            }
            catch (Exception ex) when (ex is NotImplementedException or NotSupportedException
                or InvalidOperationException)
            {
                // Attach the source file and the statement's position so the host can
                // report the failure as a diagnostic pointing at the offending statement.
                validator.AddError(new SqlSourceException(
                    ex.Message, file.Name, statement.Line, statement.Column, innerException: ex));
            }
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
    /// Records a warning for every table option that is recognized but not carried into the
    /// model (issue #207), so the loss is visible rather than the option silently vanishing.
    ///
    /// <para>
    /// Two different reasons land here. Most options (ROW_FORMAT, KEY_BLOCK_SIZE, …) genuinely
    /// persist, but the catalog reports a value for a table that never declared one, so a
    /// declared default cannot be told apart from an absent clause. AUTO_INCREMENT is not a
    /// schema facet at all: it is a live counter that moves as rows are inserted, so modeling it
    /// would re-diff against any table that has ever been written to. Both are warned rather than
    /// rejected, because unlike a Postgres access method none of them changes what the table
    /// <em>is</em>: a table that ignores its ROW_FORMAT still holds the same rows under the same
    /// constraints.
    /// </para>
    /// </summary>
    private static void AddUnmodeledTableOptionWarnings(IFile file,
        CreateTableStatement createTable,
        List<SqlSourceDiagnostic> warnings)
    {
        var table = createTable.Name.Name;

        foreach (var option in createTable.Options)
        {
            // A CHARSET is only unmodeled on its own. Written alongside an explicit COLLATE it is
            // redundant rather than lost, since the collation it would have resolved to is stated
            // outright and is what the catalog reports back.
            if (option.Name == "CHARSET"
                && createTable.Options.Any(o => o.Name == "COLLATE" && o.Value is not null))
            {
                continue;
            }

            if (ModeledTableOptions.Contains(option.Name) && option.Value is not null)
            {
                continue;
            }

            var reason = option.Name switch
            {
                // Said outright rather than as "not modeled", because raising it in a later
                // release would not help: the value the catalog reports is the table's current
                // counter, not the seed it was declared with.
                "AUTO_INCREMENT" =>
                    "is a live counter rather than a schema facet, and is not modeled",

                // A bare CHARSET resolves to its charset's default collation, which differs
                // between the engines, so the build cannot know which one this table will get.
                "CHARSET" =>
                    "resolves to a collation that differs between MariaDB and MySQL, and is not "
                    + "modeled; declare COLLATE to model it",

                _ => "is not modeled",
            };

            warnings.Add(new SqlSourceDiagnostic(
                $"Table option {option.Name} on table '{table}' {reason}; it will not be "
                + "deployed or compared.",
                file.Name,
                option.Line ?? createTable.Line,
                option.Column ?? createTable.Column));
        }
    }

    /// <summary>
    /// Records a warning for every construct in a CREATE TABLE that is recognized but not
    /// carried into the model (issue #61): CHECK/COMMENT/COLLATE and other ignored
    /// constraints, and column defaults that are not constant literals (<c>CURRENT_TIMESTAMP</c>,
    /// <c>NOW()</c>, <c>DEFAULT NULL</c>) — see <see cref="MariaDbDefaultValue"/>.
    /// </summary>
    private static void AddUnmodeledTableWarnings(IFile file,
        CreateTableStatement createTable,
        List<SqlSourceDiagnostic> warnings,
        MariaDbFamilyDatabaseSchemaProvider schemaProvider)
    {
        var table = createTable.Name.Name;

        foreach (var tableConstraint in createTable.Elements.OfType<TableConstraint>())
        {
            var constraint = tableConstraint is NamedTableConstraint named
                ? named.Constraint
                : tableConstraint;

            if (constraint is IgnoredTableConstraint)
            {
                warnings.Add(new SqlSourceDiagnostic(
                    $"A constraint on table '{table}' (FULLTEXT, SPATIAL, …) is not "
                    + "modeled and will not be deployed or compared.",
                    file.Name,
                    constraint.Line ?? createTable.Line,
                    constraint.Column ?? createTable.Column));
            }
        }

        foreach (var columnDefinition in createTable.Elements.OfType<ColumnDefinition>())
        {
            foreach (var columnConstraint in columnDefinition.Constraints)
            {
                var constraint = columnConstraint is NamedColumnConstraint named
                    ? named.Constraint
                    : columnConstraint;

                var line = constraint.Line ?? createTable.Line;
                var column = constraint.Column ?? createTable.Column;

                if (constraint is UnmodeledColumnConstraint unmodeled)
                {
                    // Named rather than listed: the old wording offered "(COMMENT, COLLATE, …)"
                    // for every attribute alike, pointing the reader at clauses they had not
                    // written (issue #216).
                    warnings.Add(new SqlSourceDiagnostic(
                        $"{unmodeled.Keyword} on column '{table}.{columnDefinition.Name.Name}' "
                        + "is not reported by information_schema on either engine, so it is not "
                        + "modeled and will not be deployed or compared.",
                        file.Name, line, column));
                }
                else if (constraint is IgnoredColumnConstraint)
                {
                    warnings.Add(new SqlSourceDiagnostic(
                        $"A constraint on column '{table}.{columnDefinition.Name.Name}' "
                        + "is not modeled and will not be deployed or compared.",
                        file.Name, line, column));
                }
                else if (constraint is DefaultColumnConstraint defaultConstraint)
                {
                    if (MariaDbDefaultValue.FromSourceToken(defaultConstraint.Token, schemaProvider) is null)
                    {
                        // A date/time function default that this engine does not accept — but
                        // another in the family does — is called out as such, since the same
                        // source builds cleanly for the other one (issue #147). Saying only
                        // "not a constant literal" would send the reader looking for the wrong
                        // problem.
                        var reason = !schemaProvider.SupportsDateAndTimeFunctionDefaults
                            && MariaDbDefaultValue.IsDateOrTimeFunction(defaultConstraint.Token)
                            ? $"is a date/time function default, which {schemaProvider.ProviderName} "
                              + "does not accept, and is not modeled"
                            : "is not a constant literal and is not modeled";

                        warnings.Add(new SqlSourceDiagnostic(
                            $"DEFAULT on column '{table}.{columnDefinition.Name.Name}' {reason}; "
                            + "it will not be deployed or compared.",
                            file.Name, line, column));
                    }

                    // An ON UPDATE clause the provider cannot model — one of the other time
                    // functions the grammar admits, which both engines reject in this position
                    // anyway — is reported rather than silently dropped. A fractional-seconds
                    // CURRENT_TIMESTAMP(n) is modeled as of issue #144 and does not warn.
                    if (defaultConstraint.OnUpdateToken is { } onUpdate
                        && MariaDbDefaultValue.CanonicalOnUpdate(onUpdate, schemaProvider) is null)
                    {
                        warnings.Add(new SqlSourceDiagnostic(
                            $"ON UPDATE on column '{table}.{columnDefinition.Name.Name}' is not a "
                            + "CURRENT_TIMESTAMP and is not modeled; it will not be "
                            + "deployed or compared.",
                            file.Name, line, column));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Validates that everything the source references is defined in the project — like
    /// SSDT, an unresolved reference is a build error reported at the referencing
    /// construct's source position. Own-table checks (constraint columns, FK shape) are
    /// made as statements are added; cross-object checks (referenced tables/columns) are
    /// deferred to <see cref="ThrowIfInvalid"/> so declaration order, within and across
    /// files, does not matter. Every error is reported, not just the first. MariaDB has
    /// no schema objects (a database is the schema), so tables are keyed by bare name.
    /// </summary>
    // A MariaDB database *is* the schema namespace — there are no schema objects — so tables
    // (and routines and triggers) are keyed by their bare name, case-insensitively. The shared
    // validation core lives in SourceValidatorBase; this subclass adds MariaDB's routine/
    // trigger/index tracking and the registration methods that read MariaDB syntax.
    private sealed class SourceValidator : SourceValidatorBase<string>
    {
        private readonly MariaDbFamilyDatabaseSchemaProvider _schemaProvider;

        public SourceValidator(MariaDbFamilyDatabaseSchemaProvider schemaProvider)
            : base(StringComparer.OrdinalIgnoreCase)
        {
            _schemaProvider = schemaProvider;
        }

        /// <summary>
        /// Reports an identifier the target engine would reject as too long (issue #163).
        /// Both engines cap at 64 characters and fail with <c>ERROR 1059</c>, which surfaces
        /// mid-deploy after part of the script has already run — so it is caught here instead,
        /// anchored at the statement that declares it.
        ///
        /// <paramref name="description"/> says what the identifier is, because a derived name
        /// (an unnamed foreign key's <c>&lt;table&gt;_ibfk_&lt;n&gt;</c>) does not appear in the
        /// source text and would otherwise be unattributable.
        /// </summary>
        public void CheckIdentifierLength(
            IFile file, int? line, int? column, string description, string identifier)
        {
            var limit = _schemaProvider.MaxIdentifierLength;

            if (_schemaProvider.MeasureIdentifier(identifier) <= limit)
            {
                return;
            }

            // "characters" is stated literally rather than read from the provider because this
            // validator only ever serves the MariaDB family, whose unit is characters. A
            // Postgres equivalent would have to say "bytes" — see MeasureIdentifier.
            AddError(new SqlSourceException(
                $"{description} '{identifier}' is too long: "
                + $"{_schemaProvider.ProviderName} limits an identifier to {limit} characters.",
                file.Name, line, column, SqlSourceException.IdentifierTooLong));
        }

        // Where each routine/trigger was first defined, so a redefinition can name the original.
        private readonly Dictionary<string, Origin> _procedureOrigins = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Origin> _functionOrigins = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Origin> _triggerOrigins = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Origin> _eventOrigins = new(StringComparer.OrdinalIgnoreCase);

        // An index name only has to be unique within its table in MariaDB, unlike Postgres
        // where constraints and indexes share a per-schema namespace.
        private readonly Dictionary<(string Table, string Name), Origin> _indexOrigins = new();

        // Keyed by name alone: a CHECK constraint name is database-scoped in both engines.
        private readonly Dictionary<string, Origin> _checkConstraintOrigins = new();

        private readonly HashSet<CreateTableStatement> _duplicateTables = [];
        private readonly HashSet<CreateProcedureStatement> _duplicateProcedures = [];
        private readonly HashSet<CreateFunctionStatement> _duplicateFunctions = [];
        private readonly HashSet<CreateTriggerStatement> _duplicateTriggers = [];
        private readonly HashSet<CreateEventStatement> _duplicateEvents = [];

        // The base tracks unique column sets and the FK backing-index check; the rationale for
        // recording only unique sets is MariaDB/MySQL-specific and lives at each call site
        // (AddCreateTable / AddCreateIndex): this provider serves both engines and enforces the
        // stricter MySQL rule (a unique key on exactly the referenced columns), so a DACPAC that
        // builds is deployable on either — MariaDB's looser leftmost-prefix form is rejected.

        public bool IsDuplicateTable(CreateTableStatement createTable)
            => _duplicateTables.Contains(createTable);

        public bool IsDuplicateProcedure(CreateProcedureStatement createProcedure)
            => _duplicateProcedures.Contains(createProcedure);

        public void AddCreateProcedure(IFile file, CreateProcedureStatement createProcedure)
        {
            // MariaDB does not allow routine overloading — a name identifies one procedure
            // within the database, regardless of parameters.
            var name = createProcedure.Name.Name;

            CheckIdentifierLength(file, createProcedure.Line, createProcedure.Column,
                "Procedure", name);

            CheckRoutineParameterNames(file, createProcedure.Line, createProcedure.Column,
                $"procedure '{name}'", createProcedure.Parameters);

            if (_procedureOrigins.TryGetValue(name, out var existing))
            {
                AddError(new SqlSourceException(
                    $"Procedure '{name}' is already defined in {DescribeOrigin(existing)}.",
                    file.Name, createProcedure.Line, createProcedure.Column,
                    SqlSourceException.DuplicateDefinition));

                _duplicateProcedures.Add(createProcedure);

                return;
            }

            _procedureOrigins[name] = new Origin(file.Name, createProcedure.Line);
        }

        public bool IsDuplicateFunction(CreateFunctionStatement createFunction)
            => _duplicateFunctions.Contains(createFunction);

        public void AddCreateFunction(IFile file, CreateFunctionStatement createFunction)
        {
            // Like a procedure, a function name identifies one function within the database,
            // regardless of parameters — neither engine allows routine overloading.
            var name = createFunction.Name.Name;

            CheckIdentifierLength(file, createFunction.Line, createFunction.Column,
                "Function", name);

            CheckRoutineParameterNames(file, createFunction.Line, createFunction.Column,
                $"function '{name}'", createFunction.Parameters);

            if (_functionOrigins.TryGetValue(name, out var existing))
            {
                AddError(new SqlSourceException(
                    $"Function '{name}' is already defined in {DescribeOrigin(existing)}.",
                    file.Name, createFunction.Line, createFunction.Column,
                    SqlSourceException.DuplicateDefinition));

                _duplicateFunctions.Add(createFunction);

                return;
            }

            _functionOrigins[name] = new Origin(file.Name, createFunction.Line);
        }

        public bool IsDuplicateTrigger(CreateTriggerStatement createTrigger)
            => _duplicateTriggers.Contains(createTrigger);

        public void AddCreateTrigger(IFile file, CreateTriggerStatement createTrigger)
        {
            // A trigger name is unique within the database (schema), regardless of the table
            // it fires on — the same as a routine.
            var name = createTrigger.Name.Name;

            CheckIdentifierLength(file, createTrigger.Line, createTrigger.Column,
                "Trigger", name);

            if (_triggerOrigins.TryGetValue(name, out var existing))
            {
                AddError(new SqlSourceException(
                    $"Trigger '{name}' is already defined in {DescribeOrigin(existing)}.",
                    file.Name, createTrigger.Line, createTrigger.Column,
                    SqlSourceException.DuplicateDefinition));

                _duplicateTriggers.Add(createTrigger);

                return;
            }

            _triggerOrigins[name] = new Origin(file.Name, createTrigger.Line);

            // The table the trigger fires on must be declared in the project, so an unresolved
            // one is reported like any other unresolved reference.
            var triggerTable = createTrigger.Table.Name;
            AddTableReference(new TableReference(
                file.Name, createTrigger.Line, createTrigger.Column,
                $"Trigger '{name}'",
                triggerTable, triggerTable, []));
        }

        public bool IsDuplicateEvent(CreateEventStatement createEvent)
            => _duplicateEvents.Contains(createEvent);

        public void AddCreateEvent(IFile file, CreateEventStatement createEvent)
        {
            // An event name is unique within the database (schema), the same as a routine or
            // a trigger. Unlike a trigger it references no table, so there is no reference to
            // resolve — an event body may query anything, and bodies are not parsed.
            var name = createEvent.Name.Name;

            CheckIdentifierLength(file, createEvent.Line, createEvent.Column,
                "Event", name);

            if (_eventOrigins.TryGetValue(name, out var existing))
            {
                AddError(new SqlSourceException(
                    $"Event '{name}' is already defined in {DescribeOrigin(existing)}.",
                    file.Name, createEvent.Line, createEvent.Column,
                    SqlSourceException.DuplicateDefinition));

                _duplicateEvents.Add(createEvent);

                return;
            }

            _eventOrigins[name] = new Origin(file.Name, createEvent.Line);
        }

        /// <summary>
        /// Checks the names MariaDB derives for the table's unnamed foreign keys
        /// (<c>&lt;table&gt;_ibfk_&lt;n&gt;</c>). A derived name can exceed the limit while every
        /// identifier the user wrote is within it, so it has to be checked as the name that
        /// will actually be deployed rather than as source text.
        ///
        /// <para>
        /// The ordinal must be counted exactly the way <see cref="MakeCreateTableElements"/>
        /// counts it, or the name reported here is not the name that deploys: column-level
        /// foreign keys are collected by <c>AddColumns</c> <em>before</em> the table-level ones,
        /// and both share one counter. Enumerating only the table-level constraints (as this
        /// check first did) both skipped column-level keys entirely and mis-numbered the rest.
        /// </para>
        ///
        /// <para>
        /// The name derived here is a <em>prediction</em> that the script generator then
        /// enforces: it emits an explicit <c>CONSTRAINT &lt;name&gt; FOREIGN KEY …</c> clause, so
        /// the engine never auto-names the constraint and the two sides agree by construction.
        /// That matters as of MariaDB 12.1, which changed how it names an <em>anonymous</em>
        /// foreign key — from <c>&lt;table&gt;_ibfk_&lt;n&gt;</c> to a bare ordinal (<c>1</c>,
        /// <c>2</c>) — as part of MDEV-28933. Squill does not reach that path, and both engines
        /// were measured round-tripping an unnamed foreign key as
        /// <c>&lt;table&gt;_ibfk_1</c>; but a database Squill did not create could already hold
        /// a foreign key named <c>1</c>.
        /// </para>
        /// </summary>
        private void CheckDerivedForeignKeyNames(
            IFile file, CreateTableStatement createTable, string table)
        {
            // Column-level foreign keys first, matching AddColumns' contribution order. A
            // constraint wrapped in NamedColumnConstraint carries an explicit name and so takes
            // no ordinal — the same rule the table-level loop below applies.
            var unnamed = createTable.Elements
                .OfType<ColumnDefinition>()
                .SelectMany(c => c.Constraints)
                .Where(c => c is not NamedColumnConstraint)
                .OfType<ForeignKeyColumnConstraint>()
                .Select(c => (Line: c.Line ?? createTable.Line, Column: c.Column ?? createTable.Column))
                .ToList();

            // Then the table-level ones, skipping any that carry an explicit CONSTRAINT name —
            // those are checked as written and take no ordinal.
            foreach (var tableConstraint in createTable.Elements.OfType<TableConstraint>())
            {
                if (tableConstraint is NamedTableConstraint)
                {
                    continue;
                }

                if (tableConstraint is ForeignKeyTableConstraint fk)
                {
                    unnamed.Add((fk.Line ?? createTable.Line, fk.Column ?? createTable.Column));
                }
            }

            for (var ordinal = 1; ordinal <= unnamed.Count; ordinal++)
            {
                var (line, column) = unnamed[ordinal - 1];

                CheckIdentifierLength(file, line, column,
                    $"Generated name for an unnamed foreign key on table '{table}'",
                    $"{table}_ibfk_{ordinal}");
            }
        }

        public void AddCreateTable(IFile file, CreateTableStatement createTable)
        {
            var table = createTable.Name.Name;

            CheckIdentifierLength(file, createTable.Line, createTable.Column, "Table", table);

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var columnDefinition in createTable.Elements.OfType<ColumnDefinition>())
            {
                CheckIdentifierLength(file, createTable.Line, createTable.Column,
                    $"Column on table '{table}'", columnDefinition.Name.Name);

                // A column named twice would silently collapse into one model element;
                // MariaDB rejects it outright, so it is a build error.
                if (!columns.Add(columnDefinition.Name.Name))
                {
                    AddError(new SqlSourceException(
                        $"Column '{columnDefinition.Name.Name}' is defined more than once on "
                        + $"table '{table}'.",
                        file.Name, createTable.Line, createTable.Column,
                        SqlSourceException.DuplicateDefinition));
                }
            }

            // Two CREATE TABLEs for the same name would last-win in the declared-table map
            // and put both element sets in the model, which confuses diffing — so it is an
            // error reported at the second definition, naming where the first one is.
            if (TableOrigins.TryGetValue(table, out var existingTable))
            {
                AddError(new SqlSourceException(
                    $"Table '{table}' is already defined in {DescribeOrigin(existingTable)}.",
                    file.Name, createTable.Line, createTable.Column,
                    SqlSourceException.DuplicateDefinition));

                _duplicateTables.Add(createTable);

                return;
            }

            TableOrigins[table] = new Origin(file.Name, createTable.Line);
            DeclaredTables[table] = columns;

            // The set above is for membership checks and is unordered; a view's SELECT *
            // expands in declaration order, so that order is kept alongside it.
            DeclaredColumnOrder[table] = createTable.Elements
                .OfType<ColumnDefinition>()
                .Select(i => i.Name.Name)
                .ToList();

            CheckDerivedForeignKeyNames(file, createTable, table);

            foreach (var tableConstraint in createTable.Elements.OfType<TableConstraint>())
            {
                var (constraint, constraintName) = tableConstraint is NamedTableConstraint named
                    ? (named.Constraint, named.Name)
                    : (tableConstraint, (string?)null);

                var line = constraint.Line ?? createTable.Line;
                var column = constraint.Column ?? createTable.Column;

                // Covers every named table constraint in one place. The unnamed forms are
                // handled per-case below, because each derives its name differently: an
                // unnamed unique index takes its first column's name (already checked as a
                // column), and an unnamed foreign key needs the _ibfk_N ordinal.
                if (constraintName is not null)
                {
                    CheckIdentifierLength(file, line, column,
                        $"Constraint on table '{table}'", constraintName);
                }

                switch (constraint)
                {
                    case PrimaryKeyTableConstraint pk:
                        // Neither engine accepts a functional key in a primary key: MySQL
                        // rejects it with ERROR 3756 ("The primary key cannot be a functional
                        // index") and MariaDB, which has no functional indexes, with a syntax
                        // error. The grammar admits one anyway, because a PK reuses the same
                        // indexColumnNames every other index form uses (issue #209).
                        if (pk.Columns.Any(c => c.KeyExpression is not null))
                        {
                            AddError(new SqlSourceException(
                                $"Primary key on table '{table}' uses an expression key. "
                                + "A primary key must name columns; neither MariaDB nor MySQL "
                                + "accepts a functional index as a primary key.",
                                file.Name, line, column,
                                SqlSourceException.InvalidConstraint));
                        }

                        CheckOwnColumns(file, line, column,
                            $"Primary key on table '{table}'", table, columns,
                            KeyColumnNames(pk.Columns));

                        AddUniqueColumnSet(table, KeyColumnNames(pk.Columns), isPrimaryKey: true);
                        break;

                    case UniqueKeyTableConstraint unique:
                        // An unnamed unique index is named after its first column, which an
                        // expression key does not have (issue #209). MySQL derives the name
                        // from the functional key itself, which cannot be predicted here, so
                        // an explicit name is required rather than guessed.
                        if ((constraintName ?? unique.IndexName) is null
                            && unique.Columns is [{ KeyExpression: not null }, ..])
                        {
                            AddError(new SqlSourceException(
                                $"Unique constraint on table '{table}' starts with an expression "
                                + "key and has no name. Name it explicitly: the name MySQL "
                                + "derives for a functional key cannot be predicted at build "
                                + "time.",
                                file.Name, line, column,
                                SqlSourceException.InvalidConstraint));
                        }

                        CheckOwnColumns(file, line, column,
                            $"Unique constraint on table '{table}'", table, columns,
                            KeyColumnNames(unique.Columns));

                        AddUniqueColumnSet(table, KeyColumnNames(unique.Columns), isPrimaryKey: false);

                        // An inline UNIQUE KEY shares the table's index-name namespace with a
                        // standalone CREATE INDEX, so it has to be registered here too.
                        CheckDuplicateIndexName(file, line, column, table,
                            constraintName ?? unique.IndexName);

                        // The `UNIQUE KEY <name>` spelling names the index outside the
                        // CONSTRAINT slot the shared check above covers.
                        if (constraintName is null && unique.IndexName is { } uniqueIndexName)
                        {
                            CheckIdentifierLength(file, line, column,
                                $"Index on table '{table}'", uniqueIndexName);
                        }
                        break;

                    case IndexTableConstraint index:
                        CheckOwnColumns(file, line, column,
                            $"Index on table '{table}'", table, columns,
                            KeyColumnNames(index.Columns));

                        // A plain KEY/INDEX is deliberately not recorded as a unique set:
                        // MariaDB would accept it as a foreign key's backing index, but MySQL
                        // would not, and the check enforces the stricter of the two.
                        CheckDuplicateIndexName(file, line, column, table,
                            constraintName ?? index.IndexName);

                        if (constraintName is null && index.IndexName is { } indexName)
                        {
                            CheckIdentifierLength(file, line, column,
                                $"Index on table '{table}'", indexName);
                        }
                        break;

                    case CheckTableConstraint:
                        // MariaDB and MySQL derive different names for an unnamed
                        // table-level CHECK — MariaDB uses CONSTRAINT_1, CONSTRAINT_2, …
                        // while MySQL uses <table>_chk_1 — and one DACPAC serves both, so
                        // the name cannot be predicted at build time. An unpredictable name
                        // would never match the one read back from the database and the
                        // constraint would re-diff on every deploy, so require an explicit
                        // one (issue #120).
                        if (constraintName is null)
                        {
                            AddError(new SqlSourceException(
                                $"The CHECK constraint on table '{table}' has no name, and "
                                + "MariaDB and MySQL derive different names for one. Name it "
                                + "explicitly with CONSTRAINT <name> CHECK (...).",
                                file.Name, line, column, SqlSourceException.InvalidConstraint));
                        }
                        else
                        {
                            CheckDuplicateCheckConstraintName(file, line, column, table, constraintName);
                        }
                        break;

                    case ForeignKeyTableConstraint fk:
                        CheckOwnColumns(file, line, column,
                            $"Foreign key on table '{table}'", table, columns,
                            fk.Columns.Select(c => c.Name));

                        var shapeIsValid = fk.ReferencedColumns.Count == 0
                            || fk.ReferencedColumns.Count == fk.Columns.Count;

                        if (!shapeIsValid)
                        {
                            AddError(new SqlSourceException(
                                $"Foreign key on table '{table}' has {fk.Columns.Count} referencing "
                                + $"column(s) but {fk.ReferencedColumns.Count} referenced column(s).",
                                file.Name, line, column, SqlSourceException.InvalidConstraint));
                        }

                        var referenced = fk.ReferencedTable.Name;
                        AddTableReference(new TableReference(
                            file.Name, line, column,
                            $"Foreign key on table '{table}'",
                            referenced, referenced,
                            fk.ReferencedColumns.Select(c => c.Name).ToList()));

                        // A foreign key whose shape is already wrong gets no uniqueness
                        // complaint on top — that would only obscure the actual problem.
                        if (shapeIsValid)
                        {
                            AddForeignKeyCheck(new ForeignKeyUniquenessCheck(
                                file.Name, line, column,
                                $"Foreign key on table '{table}'",
                                referenced, referenced,
                                fk.ReferencedColumns.Select(c => c.Name).ToList()));
                        }
                        break;
                }
            }

            foreach (var columnDefinition in createTable.Elements.OfType<ColumnDefinition>())
            {
                foreach (var columnConstraint in columnDefinition.Constraints)
                {
                    var (constraint, constraintName) = columnConstraint is NamedColumnConstraint named
                        ? (named.Constraint, named.Name)
                        : (columnConstraint, (string?)null);

                    var line = constraint.Line ?? createTable.Line;
                    var column = constraint.Column ?? createTable.Column;

                    if (constraint is GeneratedColumnConstraint
                        && columnDefinition.Constraints.Any(c =>
                            (c is NamedColumnConstraint n ? n.Constraint : c)
                                is NullableColumnConstraint { Nullable: false }))
                    {
                        // MySQL accepts `GENERATED ALWAYS AS (...) STORED NOT NULL` but
                        // MariaDB rejects it inside a CREATE TABLE, and one DACPAC serves
                        // both engines — so there is no portable spelling to generate
                        // (issue #120). Reject it at build time rather than emit DDL that
                        // fails to deploy on one of the two engines.
                        AddError(new SqlSourceException(
                            $"Generated column '{table}.{columnDefinition.Name.Name}' is "
                            + "declared NOT NULL, which MariaDB does not accept on a "
                            + "generated column in a CREATE TABLE. Remove the NOT NULL.",
                            file.Name, line, column, SqlSourceException.InvalidConstraint));
                    }
                    else if (constraint is CheckColumnConstraint)
                    {
                        // As with a table-level CHECK, the two engines derive different names
                        // for an unnamed inline one (MariaDB uses the column's name, MySQL
                        // <table>_chk_N), so an explicit name is required (issue #120).
                        if (constraintName is null)
                        {
                            AddError(new SqlSourceException(
                                $"The CHECK constraint on column "
                                + $"'{table}.{columnDefinition.Name.Name}' has no name, and "
                                + "MariaDB and MySQL derive different names for one. Name it "
                                + "explicitly with CONSTRAINT <name> CHECK (...).",
                                file.Name, line, column, SqlSourceException.InvalidConstraint));
                        }
                        else
                        {
                            CheckDuplicateCheckConstraintName(file, line, column, table, constraintName);
                        }
                    }
                    else if (constraint is PrimaryKeyColumnConstraint)
                    {
                        AddUniqueColumnSet(table, [columnDefinition.Name.Name], isPrimaryKey: true);
                    }
                    else if (constraint is UniqueKeyColumnConstraint
                             or SerialDefaultColumnConstraint)
                    {
                        // SERIAL DEFAULT VALUE expands to NOT NULL AUTO_INCREMENT UNIQUE, so it
                        // contributes a unique column set exactly as an inline UNIQUE does
                        // (issue #216): a foreign key may reference the column it creates.
                        AddUniqueColumnSet(table, [columnDefinition.Name.Name], isPrimaryKey: false);
                    }
                    else if (constraint is ForeignKeyColumnConstraint fk)
                    {
                        var referencedColumns = fk.ReferencedColumn is { } referencedColumn
                            ? new[] { referencedColumn.Name }
                            : Array.Empty<string>();

                        var referenced = fk.ReferencedTable.Name;
                        AddTableReference(new TableReference(
                            file.Name, line, column,
                            $"Foreign key on table '{table}'",
                            referenced, referenced,
                            referencedColumns));

                        AddForeignKeyCheck(new ForeignKeyUniquenessCheck(
                            file.Name, line, column,
                            $"Foreign key on table '{table}'",
                            referenced, referenced,
                            referencedColumns));
                    }
                }
            }
        }

        /// <summary>
        /// A routine's parameter names are part of its stored definition and are read back from
        /// the catalog, so an over-long one fails the same way the routine's own name would.
        /// </summary>
        private void CheckRoutineParameterNames(
            IFile file, int? line, int? column, string routine,
            IEnumerable<RoutineParameter> parameters)
        {
            foreach (var parameter in parameters)
            {
                CheckIdentifierLength(file, parameter.Line ?? line, parameter.Column ?? column,
                    $"Parameter of {routine}", parameter.Name.Name);
            }
        }

        public void AddCreateView(IFile file, CreateViewStatement createView)
        {
            CheckIdentifierLength(file, createView.Line, createView.Column,
                "View", createView.Name.Name);

            // An explicit column list names the view's columns outright. MySQL rejects an
            // over-long one (error 1166); MariaDB silently truncates it to 64 characters, so
            // the extracted name would never match the declared one and the view would
            // re-diff on every deploy — the worse of the two failures.
            foreach (var columnName in createView.ColumnNames)
            {
                CheckIdentifierLength(file, createView.Line, createView.Column,
                    $"Column of view '{createView.Name.Name}'", columnName.Name);
            }

            // Every table the view selects from must be declared in the project, so an
            // unresolved one is reported like any other unresolved reference.
            foreach (var sourceTable in createView.SourceTables)
            {
                AddTableReference(new TableReference(
                    file.Name, createView.Line, createView.Column,
                    $"View '{createView.Name.Name}'",
                    sourceTable.Name, sourceTable.Name, []));
            }
        }

        /// <summary>
        /// The columns declared on a table, or null when the table is not declared in the
        /// project. Used to expand a view's <c>SELECT *</c>; an unresolved table is already
        /// reported as an error by <see cref="SourceValidatorBase{TTableKey}.ThrowIfInvalid"/>.
        /// </summary>
        public IReadOnlyList<string>? GetDeclaredColumns(string table)
            => DeclaredColumnOrder.TryGetValue(table, out var columns) ? columns : null;

        public void AddCreateIndex(IFile file, CreateIndexStatement createIndex)
        {
            var table = createIndex.OnTable.Name;
            var columns = KeyColumnNames(createIndex.Columns).ToList();

            AddTableReference(new TableReference(
                file.Name, createIndex.Line, createIndex.Column,
                createIndex.Name is { } name ? $"Index '{name}'" : "Index",
                table, table,
                columns));

            CheckDuplicateIndexName(file, createIndex.Line, createIndex.Column,
                table, createIndex.Name);

            if (createIndex.Name is { } indexName)
            {
                CheckIdentifierLength(file, createIndex.Line, createIndex.Column,
                    $"Index on table '{table}'", indexName);
            }

            // Only a UNIQUE index backs a foreign key on both engines; MySQL rejects a
            // non-unique one even though MariaDB accepts it.
            if (createIndex.Unique)
            {
                AddUniqueColumnSet(table, columns, isPrimaryKey: false);
            }
        }

        /// <summary>
        /// Reports an index name already used on the same table. An index name must be unique
        /// within its table (not across the database, as in Postgres), so the table is part of
        /// the key. An unnamed index gets its name from MariaDB and cannot collide here.
        /// </summary>
        private void CheckDuplicateIndexName(IFile file,
            int? line,
            int? column,
            string table,
            string? indexName)
        {
            if (indexName is null)
            {
                return;
            }

            var key = (table.ToLowerInvariant(), indexName.ToLowerInvariant());

            if (_indexOrigins.TryGetValue(key, out var existing))
            {
                AddError(new SqlSourceException(
                    $"Index '{indexName}' on table '{table}' is already defined in "
                    + $"{DescribeOrigin(existing)}.",
                    file.Name, line, column, SqlSourceException.DuplicateDefinition));

                return;
            }

            _indexOrigins[key] = new Origin(file.Name, line);
        }

        // Unlike an index name, a CHECK constraint name is scoped to the database rather than
        // to its table in both engines, so a collision anywhere in the project is an error
        // (issue #120).
        private void CheckDuplicateCheckConstraintName(IFile file,
            int? line,
            int? column,
            string table,
            string constraintName)
        {
            var key = constraintName.ToLowerInvariant();

            if (_checkConstraintOrigins.TryGetValue(key, out var existing))
            {
                AddError(new SqlSourceException(
                    $"Check constraint '{constraintName}' on table '{table}' is already "
                    + $"defined in {DescribeOrigin(existing)}.",
                    file.Name, line, column, SqlSourceException.DuplicateDefinition));

                return;
            }

            _checkConstraintOrigins[key] = new Origin(file.Name, line);
        }
    }

    // A foreign key gathered while walking a CREATE TABLE, before it becomes an element (its
    // name may be explicit or derived from MariaDB's _ibfk_N convention).
    private sealed record ForeignKeySpec(
        string? ExplicitName,
        IReadOnlyList<string> Columns,
        QualifiedName ReferencedTable,
        IReadOnlyList<string> ReferencedColumns,
        ReferentialAction OnDelete,
        ReferentialAction OnUpdate);

    /// <summary>
    /// The table options that are carried into the model (issue #207). Everything else the
    /// grammar accepts is warned for instead, by <see cref="AddUnmodeledTableOptionWarnings"/>.
    /// </summary>
    private static readonly HashSet<string> ModeledTableOptions =
        new(StringComparer.Ordinal) { "ENGINE", "COLLATE", "COMMENT" };

    /// <summary>
    /// Records the three table options that survive a round trip on both engines: ENGINE,
    /// COLLATE and COMMENT (issue #207). Each follows the omit-when-default convention and is
    /// stored only when declared, so a table that writes no options records none and hash-matches
    /// one extracted from either engine, whose defaults for these differ.
    /// </summary>
    private static void AddTableOptions(Element tableElement,
        CreateTableStatement createTable,
        MariaDbFamilyDatabaseSchemaProvider schemaProvider)
    {
        // Read last-to-first so a repeated option keeps its final spelling, which is the one
        // both engines apply, while each property is still added at most once: the model holds
        // one value per name, and a second Property of the same name would be a second facet
        // rather than an overwrite.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var option in createTable.Options)
        {
            if (option.Value is { } value && ModeledTableOptions.Contains(option.Name))
            {
                values[option.Name] = value;
            }
        }

        // Recorded only when it is not the engine a table would get anyway. The extractor cannot
        // tell a declared default from an inherited one (the catalog names an engine for every
        // table), so declaring the default has to leave the same mark as declaring nothing, or
        // the two models would stop matching.
        if (values.TryGetValue("ENGINE", out var engine)
            && !string.Equals(engine, schemaProvider.DefaultStorageEngine, StringComparison.OrdinalIgnoreCase))
        {
            tableElement.Properties.Add(
                new Property(MariaDbPropertyNames.Engine, CanonicalEngineName(engine)));
        }

        // Skipped when it names the collation a table would inherit anyway, for the same reason
        // as the engine above: a table declaring its schema's default collation and one declaring
        // nothing are byte-identical in the catalog, so the extractor records neither and the
        // build has to match. The table is collated identically either way.
        if (values.TryGetValue("COLLATE", out var collation)
            && !string.Equals(collation, schemaProvider.DefaultCollation, StringComparison.OrdinalIgnoreCase))
        {
            tableElement.Properties.Add(
                new Property(MariaDbPropertyNames.Collation, CanonicalCollationName(collation)));
        }

        if (values.TryGetValue("COMMENT", out var comment))
        {
            tableElement.Properties.Add(new Property(MariaDbPropertyNames.TableComment, comment));
        }
    }

    /// <summary>
    /// Case-folds a storage engine name so a declared one matches an extracted one. See
    /// <see cref="MariaDbPropertyNames.Engine"/>: the catalog's own casing is arbitrary and
    /// differs between the engines, so folding both sides is the only comparison that holds.
    /// </summary>
    internal static string CanonicalEngineName(string engine) => engine.ToLowerInvariant();

    /// <summary>
    /// Case-folds a collation name. Measured, both engines report TABLE_COLLATION in lower case
    /// whatever casing was declared (<c>COLLATE=LATIN1_BIN</c> reads back <c>latin1_bin</c>), so
    /// the declared spelling is folded to match.
    /// </summary>
    internal static string CanonicalCollationName(string collation) => collation.ToLowerInvariant();

    private static IEnumerable<Element> MakeCreateTableElements(
        CreateTableStatement createTable, MariaDbFamilyDatabaseSchemaProvider schemaProvider)
    {
        var tableName = TableName(createTable.Name);

        var tableElement = MariaDbModelFactory.CreateTable(tableName);

        var primaryKeyColumns = new List<MariaDbModelFactory.IndexedColumn>();
        var foreignKeys = new List<ForeignKeySpec>();
        var uniqueIndexes = new List<(string? Name, IReadOnlyList<IndexColumn> Columns)>();
        var checkConstraints = new List<(string Name, string Expression)>();
        var specialIndexes = new List<(string Name, string Kind, IReadOnlyList<IndexColumn> Columns)>();

        AddTableOptions(tableElement, createTable, schemaProvider);

        AddColumns(schemaProvider, tableElement, tableName, createTable, primaryKeyColumns, foreignKeys,
            uniqueIndexes, checkConstraints);
        CollectTableLevelConstraints(createTable, tableName, primaryKeyColumns, foreignKeys,
            uniqueIndexes, checkConstraints, specialIndexes);

        yield return tableElement;

        // MariaDB always names the primary key 'PRIMARY'.
        if (primaryKeyColumns.Count > 0)
        {
            yield return MariaDbModelFactory.CreatePrimaryKey(
                tableName.Sibling("PRIMARY"), tableName, primaryKeyColumns);
        }

        // Foreign keys precede indexes, matching the database-extraction builder. A
        // standalone CREATE INDEX is a separate statement processed after the whole table,
        // so emitting a table's own indexes first would put them either side of the foreign
        // keys depending on how the index was declared — and the two builders would only
        // agree for one of the two spellings.
        //
        // MariaDB (InnoDB) names an unnamed foreign key <table>_ibfk_N, numbered in
        // declaration order starting at 1.
        var ibfkOrdinal = 1;

        foreach (var foreignKey in foreignKeys)
        {
            var fkName = foreignKey.ExplicitName is { } explicitName
                ? tableName.Sibling(explicitName)
                : tableName.Sibling($"{tableName.UnqualifiedName}_ibfk_{ibfkOrdinal}");

            if (foreignKey.ExplicitName is null)
            {
                ibfkOrdinal++;
            }

            yield return MakeForeignKeyElement(tableName, fkName, foreignKey);
        }

        // UNIQUE constraints/indexes become unique SqlIndex elements. MariaDB names a unique
        // index after its first column (uniquified with _2, _3, … on collision); with only
        // the common single-unique case in scope, use the first column's name. An unnamed one
        // leading with an expression key names no column and is rejected by the validator
        // before this runs (issue #209).
        foreach (var (explicitName, columns) in uniqueIndexes)
        {
            if (explicitName is null && columns[0].Column is null)
            {
                continue;
            }

            var indexName = tableName.Sibling(explicitName ?? columns[0].Column!.Name);

            var indexedColumns = columns.Select(c =>
                ToIndexedColumn(c, tableName, indexName, indexKind: null));

            yield return MariaDbModelFactory.CreateIndex(
                indexName, tableName, isUnique: true, indexMethod: "BTREE", indexedColumns);
        }

        // Inline FULLTEXT/SPATIAL indexes (issue #146). These become ordinary SqlIndex elements
        // carrying an IndexKind and no access method — the catalog reports none for them, and
        // `USING FULLTEXT` is a syntax error on both engines.
        foreach (var (name, kind, columns) in specialIndexes)
        {
            var specialIndexName = tableName.Sibling(name);

            var indexedColumns = columns.Select(c =>
                ToIndexedColumn(c, tableName, specialIndexName, kind));

            yield return MariaDbModelFactory.CreateIndex(
                specialIndexName, tableName, isUnique: false, indexMethod: null,
                indexedColumns, indexKind: kind);
        }

        // CHECK constraints come last, matching the DB-extraction builder's per-table order
        // (issue #120). Every one has an explicit name — an unnamed CHECK is a build error,
        // since MariaDB and MySQL derive different names for one.
        foreach (var (name, expression) in checkConstraints)
        {
            yield return MariaDbModelFactory.CreateCheckConstraint(
                tableName.Sibling(name), tableName, expression);
        }
    }

    private static Element MakeForeignKeyElement(SqlName tableName, SqlName fkName, ForeignKeySpec spec)
    {
        var referencedTable = TableName(spec.ReferencedTable);

        var columns = spec.Columns.Select(tableName.Child);
        var foreignColumns = spec.ReferencedColumns.Select(referencedTable.Child);

        return MariaDbModelFactory.CreateForeignKey(
            fkName,
            tableName,
            columns,
            referencedTable,
            foreignColumns,
            spec.OnDelete,
            spec.OnUpdate);
    }

    private static void CollectTableLevelConstraints(
        CreateTableStatement createTable,
        SqlName tableName,
        List<MariaDbModelFactory.IndexedColumn> primaryKeyColumns,
        List<ForeignKeySpec> foreignKeys,
        List<(string? Name, IReadOnlyList<IndexColumn> Columns)> uniqueIndexes,
        List<(string Name, string Expression)> checkConstraints,
        List<(string Name, string Kind, IReadOnlyList<IndexColumn> Columns)> specialIndexes)
    {
        foreach (var tableElement in createTable.Elements.OfType<TableConstraint>())
        {
            var (constraint, explicitName) = tableElement is NamedTableConstraint named
                ? (named.Constraint, named.Name)
                : (tableElement, (string?)null);

            switch (constraint)
            {
                case PrimaryKeyTableConstraint pk:
                    // A PK key carries a plain column with an optional prefix length; the
                    // catalog reports no direction for one, so none is modeled (issue #161).
                    // An expression key is rejected by the validator before this runs, so a
                    // key naming no column is skipped rather than dereferenced (issue #209).
                    foreach (var column in pk.Columns)
                    {
                        if (column.Column is not { } keyColumn)
                        {
                            continue;
                        }

                        primaryKeyColumns.Add(new MariaDbModelFactory.IndexedColumn(
                            tableName.Child(keyColumn.Name),
                            PrefixLength: column.PrefixLength));
                    }
                    break;

                case UniqueKeyTableConstraint unique:
                    uniqueIndexes.Add((explicitName ?? unique.IndexName, unique.Columns));
                    break;

                case ForeignKeyTableConstraint fk:
                    foreignKeys.Add(new ForeignKeySpec(
                        explicitName,
                        fk.Columns.Select(c => c.Name).ToList(),
                        fk.ReferencedTable,
                        fk.ReferencedColumns.Select(c => c.Name).ToList(),
                        fk.OnDelete ?? ReferentialAction.Restrict,
                        fk.OnUpdate ?? ReferentialAction.Restrict));
                    break;

                // A FULLTEXT/SPATIAL index declared inline (issue #146). Like an unnamed CHECK,
                // an unnamed one is rejected by the validator and skipped here rather than
                // modeled under a name only the engine can assign.
                case IndexTableConstraint { IndexKind: { } kind } special
                    when (explicitName ?? special.IndexName) is { } specialName:
                    specialIndexes.Add((specialName, kind, special.Columns));
                    break;

                // An unnamed CHECK is rejected by the validator, so it is skipped here
                // rather than modeled under a name that cannot be predicted.
                case CheckTableConstraint check when explicitName is not null:
                    checkConstraints.Add((explicitName, check.Expression));
                    break;
            }
        }
    }

    private static void AddColumns(
        MariaDbFamilyDatabaseSchemaProvider schemaProvider,
        Element tableElement,
        SqlName tableName,
        CreateTableStatement createTable,
        List<MariaDbModelFactory.IndexedColumn> primaryKeyColumns,
        List<ForeignKeySpec> foreignKeys,
        List<(string? Name, IReadOnlyList<IndexColumn> Columns)> uniqueIndexes,
        List<(string Name, string Expression)> checkConstraints)
    {
        var columns = new Relationship(MariaDbRelationshipNames.Columns);
        tableElement.Relationships.Add(columns);

        foreach (var columnDefinition in createTable.Elements.OfType<ColumnDefinition>())
        {
            var columnName = tableName.Child(columnDefinition.Name.Name);

            var element = new Element(MariaDbElementTypes.SqlSimpleColumn)
            {
                Name = columnName,
            };

            bool? isNullable = null;
            bool isAutoIncrement = false;
            var isInvisible = false;
            string? columnComment = null;
            var declaredCollation = columnDefinition.DataType.Collation;
            string? defaultValue = null;
            string? onUpdateCurrentTimestamp = null;
            string? generatedExpression = null;
            var generatedIsStored = false;

            foreach (var columnConstraint in columnDefinition.Constraints)
            {
                var (constraint, explicitName) = columnConstraint is NamedColumnConstraint named
                    ? (named.Constraint, named.Name)
                    : (columnConstraint, (string?)null);

                switch (constraint)
                {
                    case NullableColumnConstraint nullable:
                        isNullable = nullable.Nullable;
                        break;

                    case PrimaryKeyColumnConstraint:
                        primaryKeyColumns.Add(new MariaDbModelFactory.IndexedColumn(columnName));
                        // A PK column is implicitly NOT NULL.
                        isNullable = false;
                        break;

                    case UniqueKeyColumnConstraint:
                        // An inline UNIQUE names its own column and can carry neither a prefix
                        // length nor an expression, so the key is the bare column.
                        uniqueIndexes.Add((null, new[]
                        {
                            new IndexColumn(columnDefinition.Name, isAscending: null)
                        }));
                        break;

                    case AutoIncrementColumnConstraint:
                        isAutoIncrement = true;
                        break;

                    // SERIAL DEFAULT VALUE is shorthand for NOT NULL AUTO_INCREMENT UNIQUE
                    // (issue #216). Measured identically on MariaDB 12 and MySQL 9, so it is
                    // expanded here rather than modeled as a facet of its own: each of the three
                    // is separately visible in the deployed schema, and the unique index is a
                    // UNIQUE KEY rather than a primary key even though both engines report the
                    // column's COLUMN_KEY as 'PRI'.
                    case SerialDefaultColumnConstraint:
                        isAutoIncrement = true;
                        isNullable = false;
                        uniqueIndexes.Add((null, new[]
                        {
                            new IndexColumn(columnDefinition.Name, isAscending: null)
                        }));
                        break;

                    case CommentColumnConstraint comment:
                        columnComment = comment.Comment;
                        break;

                    // Both engines accept COLLATE either as part of the type specification or
                    // after the nullability suffix (measured), and the grammar routes the two
                    // spellings to different places: the leading one is absorbed into
                    // stringDataType, the trailing one arrives here. Both mean the same thing.
                    case CollateColumnConstraint collate:
                        declaredCollation = collate.Collation;
                        break;

                    case InvisibleColumnConstraint:
                        isInvisible = true;
                        break;

                    case DefaultColumnConstraint defaultConstraint:
                        defaultValue = MariaDbDefaultValue.FromSourceToken(
                            defaultConstraint.Token, schemaProvider);

                        // The canonical token carries any fractional-seconds precision through
                        // (issue #144), so ON UPDATE CURRENT_TIMESTAMP(3) deploys as written
                        // rather than being flattened to the whole-second form. Only the
                        // current-timestamp family is valid in this position on either engine.
                        onUpdateCurrentTimestamp =
                            MariaDbDefaultValue.CanonicalOnUpdate(
                                defaultConstraint.OnUpdateToken, schemaProvider);
                        break;

                    case ForeignKeyColumnConstraint fk:
                        foreignKeys.Add(new ForeignKeySpec(
                            explicitName,
                            new[] { columnDefinition.Name.Name },
                            fk.ReferencedTable,
                            fk.ReferencedColumn is { } refCol ? new[] { refCol.Name } : Array.Empty<string>(),
                            fk.OnDelete ?? ReferentialAction.Restrict,
                            fk.OnUpdate ?? ReferentialAction.Restrict));
                        break;

                    // An unnamed inline CHECK is rejected by the validator, so only a named
                    // one reaches the model (issue #120).
                    case CheckColumnConstraint check when explicitName is not null:
                        checkConstraints.Add((explicitName, check.Expression));
                        break;

                    case GeneratedColumnConstraint generated:
                        generatedExpression = generated.Expression;
                        generatedIsStored = generated.IsStored;
                        break;

                    // IgnoredColumnConstraint and others contribute nothing to the model.
                }
            }

            // Only a NOT NULL column stores IsNullable (=false); nullable is the default, so
            // an explicit NULL records no property — matching the DB extractor.
            if (isNullable is false)
            {
                element.Properties.Add(new Property(MariaDbPropertyNames.IsNullable, false));
            }

            if (isAutoIncrement)
            {
                element.Properties.Add(new Property(MariaDbPropertyNames.IsAutoIncrement, true));
            }

            // An empty COMMENT is what both engines report for a column that declared none, so
            // writing one records nothing rather than an empty string the extractor never emits.
            if (!string.IsNullOrEmpty(columnComment))
            {
                element.Properties.Add(
                    new Property(MariaDbPropertyNames.ColumnComment, columnComment));
            }

            // Only an INVISIBLE column records the property; VISIBLE is the default and reports
            // nothing in EXTRA, so a visible column must record nothing to match the extractor.
            if (isInvisible)
            {
                element.Properties.Add(new Property(MariaDbPropertyNames.IsInvisible, true));
            }

            // A column collation, from the type-level COLLATE suffix the grammar absorbs into
            // the data type. Recorded only when it differs from the collation the column would
            // have inherited anyway, because every string column reports a COLLATION_NAME
            // whether or not one was declared (issue #216, the same trap as the table-level
            // COLLATE in #207).
            //
            // What it inherits is the *table's* collation, not the engine's: in a
            // `COLLATE=latin1_general_ci` table every unqualified column reports
            // latin1_general_ci (measured). Comparing against the engine default here would
            // record nothing while the extractor recorded a collation on every such column.
            // The table element's property is read rather than the statement's option list, so
            // the two necessarily agree on what "the table's collation" is.
            var inheritedCollation =
                tableElement.GetProperty<string>(MariaDbPropertyNames.Collation)
                ?? schemaProvider.DefaultCollation;

            if (declaredCollation is not null
                && !string.Equals(
                    declaredCollation, inheritedCollation, StringComparison.OrdinalIgnoreCase))
            {
                element.Properties.Add(new Property(
                    MariaDbPropertyNames.Collation, CanonicalCollationName(declaredCollation)));
            }

            if (defaultValue != null)
            {
                element.Properties.Add(new Property(MariaDbPropertyNames.DefaultValue, defaultValue));
            }

            if (onUpdateCurrentTimestamp != null)
            {
                element.Properties.Add(new Property(
                    MariaDbPropertyNames.OnUpdateCurrentTimestamp, onUpdateCurrentTimestamp));
            }

            // Emitted last, matching the DB-extraction builder's property order.
            if (generatedExpression != null)
            {
                MariaDbModelFactory.AddGeneratedColumnProperties(
                    element, generatedExpression, generatedIsStored);
            }

            element.Relationships.Add(BuildTypeSpecifier(columnDefinition.DataType));

            columns.Add(element);
        }
    }

    private static Relationship BuildTypeSpecifier(DataType dataType)
    {
        // Canonicalize aliases the engines report under a different spelling — e.g.
        // `integer`->`int`, `numeric`/`dec`/`fixed`->`decimal` — so a column's parsed type
        // name matches the DATA_TYPE the DB reports and the two models hash-match (issue #97).
        var canonicalTypeName = MariaDbTypeNormalizer.Canonicalize(dataType.TypeName);

        var typeSpec = new Element(MariaDbElementTypes.SqlTypeSpecifier)
        {
            Relationships =
            {
                new Relationship(MariaDbRelationshipNames.Type)
                {
                    new Reference(canonicalTypeName)
                    {
                        ExternalSource = "BuiltIns",
                    }
                }
            }
        };

        // A character/binary type carries a single length modifier; a decimal type carries
        // precision and scale. These mirror the DB extractor so both sides hash-match.
        if (IsLengthType(canonicalTypeName) && dataType.Modifiers.Count == 1)
        {
            typeSpec.Properties.Add(new Property(MariaDbPropertyNames.Length, (int)dataType.Modifiers[0]));
        }
        else if (IsDecimalType(canonicalTypeName) && dataType.Modifiers.Count >= 1)
        {
            var precision = dataType.Modifiers[0];
            var scale = dataType.Modifiers.Count > 1 ? dataType.Modifiers[1] : 0;

            typeSpec.Properties.Add(new Property(MariaDbPropertyNames.Precision, precision));
            typeSpec.Properties.Add(new Property(MariaDbPropertyNames.Scale, scale));
        }
        else if (MariaDbTypeCategories.IsTemporalPrecisionType(canonicalTypeName)
                 && dataType.Modifiers.Count == 1)
        {
            // A fractional-seconds precision, e.g. datetime(3) (issue #144). Reuses the
            // Precision property — these types never carry a decimal precision, so there is no
            // ambiguity — and is omitted when 0, which both engines treat as no precision at
            // all: they report a `datetime(0)` column as plain `datetime`.
            var precision = dataType.Modifiers[0];

            if (precision > 0)
            {
                typeSpec.Properties.Add(new Property(MariaDbPropertyNames.Precision, precision));
            }
        }

        if (dataType.IsUnsigned)
        {
            typeSpec.Properties.Add(new Property(MariaDbPropertyNames.IsUnsigned, true));
        }

        // enum/set carry their value list. Store the parenthesized text verbatim, matching
        // exactly what information_schema.COLUMN_TYPE reports so the two sides hash-match.
        if (dataType.CollectionValues.Count > 0)
        {
            var values = $"({string.Join(",", dataType.CollectionValues)})";
            typeSpec.Properties.Add(new Property(MariaDbPropertyNames.CollectionValues, values));
        }

        return new Relationship(MariaDbRelationshipNames.TypeSpecifier) { typeSpec };
    }

    /// <summary>
    /// The ascending flag to model for one index column, matching what the catalog reports in
    /// <c>information_schema.STATISTICS.COLLATION</c> so a parsed index hash-matches an
    /// extracted one.
    ///
    /// A standard index column is recorded ascending ('A') when no direction is written, and so
    /// is a SPATIAL one. A FULLTEXT index is unordered — both engines report <c>COLLATION</c>
    /// NULL for its columns — so it must carry no flag at all (issue #146).
    /// </summary>
    private static bool? IndexColumnIsAscending(IndexColumn column, string? indexKind)
        => indexKind == "FULLTEXT" ? null : column.IsAscending ?? true;

    /// <summary>
    /// The names of the key columns that reference a column of the table, for the validation
    /// that every key names one of the table's own columns.
    ///
    /// A functional key names no column (issue #161), so it contributes nothing here — the
    /// columns it mentions live inside the expression, which is carried verbatim rather than
    /// resolved.
    /// </summary>
    private static IEnumerable<string> KeyColumnNames(IEnumerable<IndexColumn> columns)
        => columns.Select(c => c.Column?.Name).OfType<string>();

    /// <summary>
    /// One parsed index key as the model records it (issue #161): the column reference (or, for
    /// a functional key, the expression that replaces it), plus the sort direction and any
    /// declared prefix length.
    ///
    /// <para>
    /// An expression key names no column, so it takes <paramref name="ownerName"/> as its
    /// identity — matching the Postgres provider, whose expression keys are shaped the same way.
    /// </para>
    /// </summary>
    private static MariaDbModelFactory.IndexedColumn ToIndexedColumn(
        IndexColumn column, SqlName tableName, SqlName ownerName, string? indexKind)
        => column.KeyExpression is { } keyExpression
            ? new MariaDbModelFactory.IndexedColumn(
                ownerName,
                IndexColumnIsAscending(column, indexKind),
                KeyExpression: keyExpression)
            : new MariaDbModelFactory.IndexedColumn(
                tableName.Child(column.Column!.Name),
                IndexColumnIsAscending(column, indexKind),
                column.PrefixLength);

    private static Element MakeCreateIndexElement(CreateIndexStatement createIndex)
    {
        if (createIndex.Name is null)
        {
            throw new NotSupportedException("Unnamed CREATE INDEX statements are not supported.");
        }

        var tableName = TableName(createIndex.OnTable);
        var indexName = tableName.Sibling(createIndex.Name);

        // A FULLTEXT/SPATIAL index takes no USING method, and the catalog reports none for it
        // (issue #146). BTREE is the default index method for the common storage engines when
        // USING is omitted; defaulting to it matches the DB extractor, which reads BTREE from
        // information_schema for a standard index.
        var indexMethod = createIndex.IndexKind is not null
            ? null
            : createIndex.IndexMethod ?? "BTREE";

        var columns = createIndex.Columns.Select(c =>
            ToIndexedColumn(c, tableName, indexName, createIndex.IndexKind));

        return MariaDbModelFactory.CreateIndex(
            indexName, tableName, createIndex.Unique, indexMethod, columns,
            indexKind: createIndex.IndexKind);
    }

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
    private static Element MakeCreateViewElement(CreateViewStatement statement,
        SourceValidator validator,
        MariaDbFamilyDatabaseSchemaProvider schemaProvider)
    {
        if (statement.Body is not { } definition)
        {
            throw new InvalidOperationException("A view must declare a query");
        }

        var columnNames = statement.ColumnNames.Count > 0
            ? statement.ColumnNames.Select(i => i.Name).ToList()
            : DeriveViewColumnNames(statement, validator);

        if (columnNames.Count == 0)
        {
            throw new InvalidOperationException($"View '{statement.Name.Name}' exposes no columns");
        }

        // A view is not schema-scoped within a database, so a leading db qualifier is
        // dropped exactly as it is for a table.
        // Issue #208. UNDEFINED is the engine default and records nothing, matching what the
        // catalog reports for a view that declared no algorithm. On MySQL nothing is recorded
        // at all: its information_schema.VIEWS has no ALGORITHM column (measured), so a
        // modeled algorithm could never be read back and would re-diff on every deploy --
        // AddUnmodeledViewOptionWarnings warns there instead.
        var algorithm = schemaProvider.ReportsViewAlgorithm
            && statement.Algorithm is { } declared
            && declared != "UNDEFINED"
                ? declared
                : null;

        return MariaDbModelFactory.CreateView(
            SqlName.Object(statement.Name.Name), columnNames, definition,
            statement.CheckOption,
            // Only INVOKER is recorded: an explicit DEFINER is indistinguishable in the
            // catalog from declaring nothing (measured on both engines).
            isSecurityInvoker: statement.SecurityType == "INVOKER",
            algorithm);
    }

    /// <summary>
    /// Warns for the view clauses that are parsed but cannot be carried into the model
    /// (issue #208), so a declared one does not vanish without a word -- which is the failure
    /// that issue reported.
    /// </summary>
    private static void AddUnmodeledViewOptionWarnings(IFile file,
        CreateViewStatement statement,
        List<SqlSourceDiagnostic> warnings,
        MariaDbFamilyDatabaseSchemaProvider schemaProvider)
    {
        var view = statement.Name.Name;

        // Who owns an object is the broader question issue #221 covers. Modeling a DEFINER
        // here would also tie a project to one server's user list, so it is warned for rather
        // than carried.
        if (statement.Definer is not null)
        {
            warnings.Add(new SqlSourceDiagnostic(
                $"DEFINER on view '{view}' is not modeled and will not be deployed. The view "
                + "will be created with the deploying user as its definer.",
                file.Name, statement.Line, statement.Column));
        }

        // Only where the engine cannot report it back. On MariaDB it is modeled, so there is
        // nothing to warn about.
        if (statement.Algorithm is { } algorithm
            && algorithm != "UNDEFINED"
            && !schemaProvider.ReportsViewAlgorithm)
        {
            warnings.Add(new SqlSourceDiagnostic(
                $"ALGORITHM on view '{view}' is not modeled on MySQL, whose "
                + "information_schema.VIEWS does not report it, and will not be deployed.",
                file.Name, statement.Line, statement.Column));
        }
    }

    private static List<string> DeriveViewColumnNames(
        CreateViewStatement statement,
        SourceValidator validator)
    {
        var names = new List<string>();

        foreach (var column in statement.SelectColumns)
        {
            if (column.IsWildcard)
            {
                names.AddRange(ExpandWildcard(statement, column.Qualifier, validator));

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
            ? statement.SourceTables[0].Name
            : ResolveQualifier(statement, qualifier);

        var columns = validator.GetDeclaredColumns(table);

        if (columns is null)
        {
            // The unresolved table is already reported by the validator; this keeps the
            // view from being modeled with a wrong (empty) column list.
            throw new NotSupportedException(
                $"View cannot expand SELECT * because table '{table}' is not defined in the project.");
        }

        return columns;
    }

    private static string ResolveQualifier(CreateViewStatement statement, string qualifier)
    {
        // The qualifier on `t.*` is either a source table's own name or an alias for one.
        // Only the former can be resolved without modeling the FROM clause's aliases, which
        // is why an alias that does not match a table name is rejected.
        foreach (var sourceTable in statement.SourceTables)
        {
            if (string.Equals(sourceTable.Name, qualifier, StringComparison.OrdinalIgnoreCase))
            {
                return sourceTable.Name;
            }
        }

        throw new NotSupportedException(
            $"A view's SELECT {qualifier}.* refers to '{qualifier}', which is not one of the "
            + "tables it selects from; list the columns explicitly instead.");
    }

    private static Element MakeCreateProcedureElement(CreateProcedureStatement createProcedure)
    {
        if (createProcedure.Body is not { } body)
        {
            throw new InvalidOperationException("A procedure must declare a body");
        }

        // The parameter type is normalized rather than stored as written, because the two
        // engines report a routine parameter's type differently (MariaDB keeps an integer
        // display width, MySQL does not). See MariaDbTypeNormalizer.
        var parameters = createProcedure.Parameters.Select(i =>
            new MariaDbModelFactory.ProcedureParameter(
                RenderParameterMode(i.Mode),
                i.Name.Name,
                MariaDbTypeNormalizer.Normalize(
                    i.DataType.TypeName, i.DataType.Modifiers, i.DataType.IsUnsigned)));

        return MariaDbModelFactory.CreateProcedure(
            // A procedure is not schema-scoped within a database, so a leading db qualifier
            // is dropped exactly as it is for a table.
            SqlName.Object(createProcedure.Name.Name),
            body,
            parameters,
            createProcedure.IsDeterministic,
            createProcedure.SqlDataAccess,
            createProcedure.IsSecurityInvoker);
    }

    private static Element MakeCreateFunctionElement(CreateFunctionStatement createFunction)
    {
        if (createFunction.Body is not { } body)
        {
            throw new InvalidOperationException("A function must declare a body");
        }

        // Parameter and return types are normalized rather than stored as written, for the
        // same reason as a procedure's: the two engines report a routine type differently
        // (MariaDB keeps an integer display width, MySQL does not). See MariaDbTypeNormalizer.
        var parameters = createFunction.Parameters.Select(i =>
            new MariaDbModelFactory.ProcedureParameter(
                RenderParameterMode(i.Mode),
                i.Name.Name,
                MariaDbTypeNormalizer.Normalize(
                    i.DataType.TypeName, i.DataType.Modifiers, i.DataType.IsUnsigned)));

        var returnType = MariaDbTypeNormalizer.Normalize(
            createFunction.ReturnType.TypeName,
            createFunction.ReturnType.Modifiers,
            createFunction.ReturnType.IsUnsigned);

        return MariaDbModelFactory.CreateFunction(
            // A function is not schema-scoped within a database, so a leading db qualifier is
            // dropped exactly as it is for a table or procedure.
            SqlName.Object(createFunction.Name.Name),
            returnType,
            body,
            parameters,
            createFunction.IsDeterministic,
            createFunction.SqlDataAccess,
            createFunction.IsSecurityInvoker);
    }

    private static Element MakeCreateTriggerElement(CreateTriggerStatement createTrigger)
    {
        if (createTrigger.Body is not { } body)
        {
            throw new InvalidOperationException("A trigger must declare a body");
        }

        // A trigger is not schema-scoped within a database, so a leading db qualifier is
        // dropped from both the trigger's name and its table, exactly as for a table.
        return MariaDbModelFactory.CreateTrigger(
            SqlName.Object(createTrigger.Table.Name),
            createTrigger.Name.Name,
            createTrigger.Timing,
            createTrigger.Event,
            body);
    }

    private static Element MakeCreateEventElement(CreateEventStatement createEvent)
    {
        if (createEvent.Body is not { } body)
        {
            throw new InvalidOperationException("An event must declare a body");
        }

        ValidateEventSchedule(createEvent);

        // An event is not schema-scoped within a database, so a leading db qualifier is
        // dropped from its name, exactly as for a table or a trigger.
        return MariaDbModelFactory.CreateEvent(
            createEvent.Name.Name,
            createEvent.EventType,
            body,
            createEvent.ExecuteAt,
            createEvent.IntervalValue,
            createEvent.IntervalField,
            createEvent.Starts,
            createEvent.Ends,
            createEvent.Status,
            createEvent.PreserveOnCompletion,
            createEvent.Comment);
    }

    // Rejects the schedule forms the engines accept but Squill cannot model. Both engines
    // resolve a schedule against the wall clock when the event is created and store only the
    // resulting absolute timestamps, so any form that is not already constant would differ
    // from its declaration the moment it is deployed — and every later deploy would script a
    // spurious change. These are reported here rather than in the parser so the builder can
    // attach the source file and position (issue #122).
    private static void ValidateEventSchedule(CreateEventStatement createEvent)
    {
        var name = createEvent.Name.Name;

        if (createEvent.ExecuteAt is { } executeAt)
        {
            RequireConstantTimestamp(name, "AT", executeAt);

            return;
        }

        if (createEvent.IntervalValue is { } intervalValue
            && !IsConstantIntervalValue(intervalValue))
        {
            throw new NotSupportedException(
                $"Event '{name}' uses a computed EVERY interval ('{intervalValue}'), which "
                + "cannot be compared against a deployed event; write a constant interval "
                + "instead.");
        }

        // A recurring event with no STARTS is rejected even though both engines accept it:
        // they record the moment the event was created as its start, which moves on every
        // deploy, so the deployed event could never match the declaration again.
        if (createEvent.Starts is not { } starts)
        {
            throw new NotSupportedException(
                $"Event '{name}' has no STARTS clause. The server records the time the event "
                + "was created as its start, which changes on every deploy and so can never "
                + "match the declaration; add an explicit STARTS timestamp.");
        }

        RequireConstantTimestamp(name, "STARTS", starts);

        if (createEvent.Ends is { } ends)
        {
            RequireConstantTimestamp(name, "ENDS", ends);
        }
    }

    private static void RequireConstantTimestamp(string name, string clause, string value)
    {
        // The parser unquotes a constant timestamp to the value the catalog reports, and
        // keeps anything else verbatim — so a value that still looks like an expression
        // (CURRENT_TIMESTAMP, NOW(), or one carrying a + INTERVAL offset) is not constant.
        if (!value.Contains('+') && DateTime.TryParse(value, out _))
        {
            return;
        }

        throw new NotSupportedException(
            $"Event '{name}' uses a non-constant {clause} value ('{value}'), which the server "
            + "resolves when the event is created, so it cannot be compared against a deployed "
            + "event; write a constant timestamp instead.");
    }

    // A constant EVERY value is a plain count (1) or a compound interval the parser has
    // normalized to the catalog's space-separated form (2 3).
    private static bool IsConstantIntervalValue(string intervalValue)
        => intervalValue.Split(' ').All(i => i.Length > 0 && i.All(char.IsAsciiDigit));

    private static string RenderParameterMode(ParameterMode mode) => mode switch
    {
        ParameterMode.In => "IN",
        ParameterMode.Out => "OUT",
        ParameterMode.InOut => "INOUT",
        _ => throw new NotSupportedException($"Parameter mode {mode} is not supported"),
    };

    // Splits a (possibly db-qualified) table name into its bare table name. MariaDB objects
    // are not schema-scoped within a database, so a leading db qualifier is dropped: the
    // model is built for the connected database, mirroring the DB extractor which reads only
    // the current database's tables.
    private static SqlName TableName(QualifiedName qualifiedName)
        => SqlName.Object(qualifiedName.Name);

    // Types whose single modifier is a length: character types and binary types. `binary`
    // defaults to length 1 when omitted, but `varbinary` requires an explicit length, so the
    // length must be carried through to the generated DDL or the column fails to create.
    private static bool IsLengthType(string typeName)
        => MariaDbTypeCategories.IsCharacterType(typeName) || typeName is "binary" or "varbinary";

    private static bool IsDecimalType(string typeName)
        => MariaDbTypeCategories.IsDecimalType(typeName);
}
