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
/// </summary>
public class ParserWorkspaceModelBuilder : IWorkspaceModelBuilder
{
    private readonly Workspace _workspace;
    private readonly IMariaDbParser _parser;

    public ParserWorkspaceModelBuilder(Workspace workspace, IMariaDbParser parser)
    {
        _workspace = workspace;
        _parser = parser;
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

        SortTablesByName(model);

        // Views are added after the tables are sorted (so they are not dragged around as a
        // table's dependents) and only once every table is known: expanding a SELECT * needs
        // the referenced table's columns, which may be declared in a later file. This runs
        // before validation so a broken view is reported alongside every other source error
        // rather than on a later rebuild (issue #61).
        AddViews(model, validator, views);

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
    private static void AddViews(Model model, SourceValidator validator, List<PendingView> views)
    {
        // Ordinal, to match the database's byte-wise ordering of the same names.
        foreach (var view in views.OrderBy(i => i.Statement.Name.Name, StringComparer.Ordinal))
        {
            try
            {
                model.Elements.Add(MakeCreateViewElement(view.Statement, validator));
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
                        validator.AddCreateTable(file, createTable);

                        if (validator.IsDuplicateTable(createTable))
                        {
                            break;
                        }

                        AddUnmodeledTableWarnings(file, createTable, warnings);

                        foreach (var element in MakeCreateTableElements(createTable))
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

                    // Recognized but not modeled (CREATE VIEW, ALTER, …). Not fatal — the
                    // rest of the project still builds — but the construct will not reach
                    // the DACPAC, so say so rather than dropping it silently.
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

    /// <summary>
    /// Records a warning for every construct in a CREATE TABLE that is recognized but not
    /// carried into the model (issue #61): CHECK/COMMENT/COLLATE and other ignored
    /// constraints, and column defaults that are not constant literals (<c>CURRENT_TIMESTAMP</c>,
    /// <c>NOW()</c>, <c>DEFAULT NULL</c>) — see <see cref="MariaDbDefaultValue"/>.
    /// </summary>
    private static void AddUnmodeledTableWarnings(IFile file,
        CreateTableStatement createTable,
        List<SqlSourceDiagnostic> warnings)
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

                if (constraint is IgnoredColumnConstraint)
                {
                    warnings.Add(new SqlSourceDiagnostic(
                        $"A constraint on column '{table}.{columnDefinition.Name.Name}' "
                        + "(COMMENT, COLLATE, …) is not modeled and will not be "
                        + "deployed or compared.",
                        file.Name, line, column));
                }
                else if (constraint is DefaultColumnConstraint defaultConstraint
                    && MariaDbDefaultValue.FromSourceToken(defaultConstraint.Token) is null)
                {
                    warnings.Add(new SqlSourceDiagnostic(
                        $"DEFAULT on column '{table}.{columnDefinition.Name.Name}' is not a "
                        + "constant literal and is not modeled; it will not be deployed or compared.",
                        file.Name, line, column));
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
        public SourceValidator() : base(StringComparer.OrdinalIgnoreCase)
        {
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

        public void AddCreateTable(IFile file, CreateTableStatement createTable)
        {
            var table = createTable.Name.Name;

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var columnDefinition in createTable.Elements.OfType<ColumnDefinition>())
            {
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

            foreach (var tableConstraint in createTable.Elements.OfType<TableConstraint>())
            {
                var (constraint, constraintName) = tableConstraint is NamedTableConstraint named
                    ? (named.Constraint, named.Name)
                    : (tableConstraint, (string?)null);

                var line = constraint.Line ?? createTable.Line;
                var column = constraint.Column ?? createTable.Column;

                switch (constraint)
                {
                    case PrimaryKeyTableConstraint pk:
                        CheckOwnColumns(file, line, column,
                            $"Primary key on table '{table}'", table, columns,
                            pk.Columns.Select(c => c.Name));

                        AddUniqueColumnSet(table, pk.Columns.Select(c => c.Name), isPrimaryKey: true);
                        break;

                    case UniqueKeyTableConstraint unique:
                        CheckOwnColumns(file, line, column,
                            $"Unique constraint on table '{table}'", table, columns,
                            unique.Columns.Select(c => c.Name));

                        AddUniqueColumnSet(table, unique.Columns.Select(c => c.Name), isPrimaryKey: false);

                        // An inline UNIQUE KEY shares the table's index-name namespace with a
                        // standalone CREATE INDEX, so it has to be registered here too.
                        CheckDuplicateIndexName(file, line, column, table,
                            constraintName ?? unique.IndexName);
                        break;

                    case IndexTableConstraint index:
                        CheckOwnColumns(file, line, column,
                            $"Index on table '{table}'", table, columns,
                            index.Columns.Select(c => c.Column.Name));

                        // A plain KEY/INDEX is deliberately not recorded as a unique set:
                        // MariaDB would accept it as a foreign key's backing index, but MySQL
                        // would not, and the check enforces the stricter of the two.
                        CheckDuplicateIndexName(file, line, column, table,
                            constraintName ?? index.IndexName);
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
                    else if (constraint is UniqueKeyColumnConstraint)
                    {
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

        public void AddCreateView(IFile file, CreateViewStatement createView)
        {
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
            var columns = createIndex.Columns.Select(c => c.Column.Name).ToList();

            AddTableReference(new TableReference(
                file.Name, createIndex.Line, createIndex.Column,
                createIndex.Name is { } name ? $"Index '{name}'" : "Index",
                table, table,
                columns));

            CheckDuplicateIndexName(file, createIndex.Line, createIndex.Column,
                table, createIndex.Name);

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

    private static IEnumerable<Element> MakeCreateTableElements(CreateTableStatement createTable)
    {
        var tableName = TableName(createTable.Name);

        var tableElement = MariaDbModelFactory.CreateTable(tableName);

        var primaryKeyColumns = new List<MariaDbModelFactory.IndexedColumn>();
        var foreignKeys = new List<ForeignKeySpec>();
        var uniqueIndexes = new List<(string? Name, IReadOnlyList<string> Columns)>();
        var checkConstraints = new List<(string Name, string Expression)>();

        AddColumns(tableElement, tableName, createTable, primaryKeyColumns, foreignKeys,
            uniqueIndexes, checkConstraints);
        CollectTableLevelConstraints(createTable, tableName, primaryKeyColumns, foreignKeys,
            uniqueIndexes, checkConstraints);

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
        // the common single-unique case in scope, use the first column's name.
        foreach (var (explicitName, columns) in uniqueIndexes)
        {
            var indexName = explicitName ?? columns[0];

            var indexedColumns = columns.Select(c =>
                new MariaDbModelFactory.IndexedColumn(tableName.Child(c), IsAscending: true));

            yield return MariaDbModelFactory.CreateIndex(
                tableName.Sibling(indexName), tableName, isUnique: true, indexMethod: "BTREE", indexedColumns);
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
        List<(string? Name, IReadOnlyList<string> Columns)> uniqueIndexes,
        List<(string Name, string Expression)> checkConstraints)
    {
        foreach (var tableElement in createTable.Elements.OfType<TableConstraint>())
        {
            var (constraint, explicitName) = tableElement is NamedTableConstraint named
                ? (named.Constraint, named.Name)
                : (tableElement, (string?)null);

            switch (constraint)
            {
                case PrimaryKeyTableConstraint pk:
                    foreach (var column in pk.Columns)
                    {
                        primaryKeyColumns.Add(new MariaDbModelFactory.IndexedColumn(tableName.Child(column.Name)));
                    }
                    break;

                case UniqueKeyTableConstraint unique:
                    uniqueIndexes.Add((
                        explicitName ?? unique.IndexName,
                        unique.Columns.Select(c => c.Name).ToList()));
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

                // An unnamed CHECK is rejected by the validator, so it is skipped here
                // rather than modeled under a name that cannot be predicted.
                case CheckTableConstraint check when explicitName is not null:
                    checkConstraints.Add((explicitName, check.Expression));
                    break;
            }
        }
    }

    private static void AddColumns(
        Element tableElement,
        SqlName tableName,
        CreateTableStatement createTable,
        List<MariaDbModelFactory.IndexedColumn> primaryKeyColumns,
        List<ForeignKeySpec> foreignKeys,
        List<(string? Name, IReadOnlyList<string> Columns)> uniqueIndexes,
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
            string? defaultValue = null;
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
                        uniqueIndexes.Add((null, new[] { columnDefinition.Name.Name }));
                        break;

                    case AutoIncrementColumnConstraint:
                        isAutoIncrement = true;
                        break;

                    case DefaultColumnConstraint defaultConstraint:
                        defaultValue = MariaDbDefaultValue.FromSourceToken(defaultConstraint.Token);
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

            if (defaultValue != null)
            {
                element.Properties.Add(new Property(MariaDbPropertyNames.DefaultValue, defaultValue));
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

    private static Element MakeCreateIndexElement(CreateIndexStatement createIndex)
    {
        if (createIndex.Name is null)
        {
            throw new NotSupportedException("Unnamed CREATE INDEX statements are not supported.");
        }

        var tableName = TableName(createIndex.OnTable);
        var indexName = tableName.Sibling(createIndex.Name);

        // BTREE is the default index method for the common storage engines when USING is
        // omitted; defaulting to it matches the DB extractor, which reads BTREE from
        // information_schema for a standard index.
        var indexMethod = createIndex.IndexMethod ?? "BTREE";

        var columns = createIndex.Columns.Select(c =>
        {
            // MariaDB records a standard b-tree index column as ascending ('A') when no
            // direction is written; fill that in so a parsed index matches the extracted one.
            var isAscending = c.IsAscending ?? true;
            return new MariaDbModelFactory.IndexedColumn(tableName.Child(c.Column.Name), isAscending);
        });

        return MariaDbModelFactory.CreateIndex(
            indexName, tableName, createIndex.Unique, indexMethod, columns);
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
    private static Element MakeCreateViewElement(CreateViewStatement statement, SourceValidator validator)
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
        return MariaDbModelFactory.CreateView(
            SqlName.Object(statement.Name.Name), columnNames, definition);
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
