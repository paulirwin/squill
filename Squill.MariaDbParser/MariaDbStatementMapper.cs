using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Squill.MariaDbParser.Syntax;

namespace Squill.MariaDbParser;

/// <summary>
/// Maps ANTLR parse-tree contexts for the statements Squill models (CREATE TABLE, CREATE
/// INDEX) into the focused <see cref="Syntax"/> tree. Unrecognized statements map to null.
/// </summary>
internal static class MariaDbStatementMapper
{
    public static Statement? Map(MariaDBParser.DdlStatementContext ddl)
    {
        if (ddl.createTable() is { } createTable)
        {
            return MapCreateTable(createTable);
        }

        if (ddl.createIndex() is { } createIndex)
        {
            return MapCreateIndex(createIndex);
        }

        if (ddl.createProcedure() is { } createProcedure)
        {
            return MapCreateProcedure(createProcedure);
        }

        // A function is parsed into a marker statement rather than dropped, so the model
        // builder can report it as unsupported at its source position.
        if (ddl.createFunction() is { } createFunction)
        {
            return At(new CreateFunctionStatement(MapQualifiedName(createFunction.fullId())),
                createFunction);
        }

        // Any other DDL (CREATE VIEW, ALTER, DROP, …) is not modeled.
        return null;
    }

    // ---- CREATE TABLE ----

    private static Statement? MapCreateTable(MariaDBParser.CreateTableContext createTable)
    {
        // Only the column-list form (CREATE TABLE t (...)) is modeled; CREATE TABLE ... AS
        // SELECT and CREATE TABLE ... LIKE describe no standalone column shape here.
        if (createTable is not MariaDBParser.ColumnCreateTableContext columnCreate)
        {
            return null;
        }

        var name = MapQualifiedName(columnCreate.tableName().fullId());
        var statement = At(new CreateTableStatement(name), columnCreate);

        foreach (var definition in columnCreate.createDefinitions().createDefinition())
        {
            switch (definition)
            {
                case MariaDBParser.ColumnDeclarationContext column:
                    statement.Elements.Add(MapColumn(column));
                    break;

                case MariaDBParser.ConstraintDeclarationContext constraint:
                    statement.Elements.Add(MapTableConstraint(constraint.tableConstraint()));
                    break;

                case MariaDBParser.IndexDeclarationContext index:
                    statement.Elements.Add(MapInlineIndex(index.indexColumnDefinition()));
                    break;

                // periodDeclaration and anything else is ignored.
            }
        }

        return statement;
    }

    private static ColumnDefinition MapColumn(MariaDBParser.ColumnDeclarationContext column)
    {
        var name = new Identifier(UidText(column.uid()));
        var definition = column.columnDefinition();
        var dataType = MapDataType(definition.dataType());

        var columnDefinition = new ColumnDefinition(name, dataType);

        foreach (var constraint in definition.columnConstraint())
        {
            columnDefinition.Constraints.Add(MapColumnConstraint(constraint));
        }

        return columnDefinition;
    }

    private static ColumnConstraint MapColumnConstraint(MariaDBParser.ColumnConstraintContext constraint)
    {
        switch (constraint)
        {
            case MariaDBParser.NullColumnConstraintContext nullConstraint:
                // nullNotnull is `NOT? NULL`; presence of NOT means NOT NULL.
                var notNull = nullConstraint.nullNotnull().NOT() != null;
                return new NullableColumnConstraint(!notNull);

            case MariaDBParser.PrimaryKeyColumnConstraintContext:
                return new PrimaryKeyColumnConstraint();

            case MariaDBParser.UniqueKeyColumnConstraintContext:
                return new UniqueKeyColumnConstraint();

            case MariaDBParser.AutoIncrementColumnConstraintContext autoInc
                when autoInc.AUTO_INCREMENT() != null:
                return new AutoIncrementColumnConstraint();

            case MariaDBParser.DefaultColumnConstraintContext defaultConstraint:
                return new DefaultColumnConstraint(defaultConstraint.defaultValue().GetText());

            case MariaDBParser.ReferenceColumnConstraintContext reference:
                return At(MapInlineForeignKey(reference.referenceDefinition()), reference);

            // COMMENT, COLLATE, VISIBLE, CHECK, generated columns, ON UPDATE, … are
            // recognized but not modeled.
            default:
                return new IgnoredColumnConstraint();
        }
    }

    private static ForeignKeyColumnConstraint MapInlineForeignKey(
        MariaDBParser.ReferenceDefinitionContext reference)
    {
        var referencedTable = MapQualifiedName(reference.tableName().fullId());

        Identifier? referencedColumn = null;
        if (reference.indexColumnNames() is { } columnNames)
        {
            var columns = MapIndexColumnNames(columnNames);
            referencedColumn = columns.Count > 0 ? columns[0].Column : null;
        }

        var (onDelete, onUpdate) = MapReferenceActions(reference.referenceAction());

        return new ForeignKeyColumnConstraint(referencedTable, referencedColumn, onDelete, onUpdate);
    }

    // ---- Table constraints ----

    private static ITableElement MapTableConstraint(MariaDBParser.TableConstraintContext constraint)
    {
        switch (constraint)
        {
            case MariaDBParser.PrimaryKeyTableConstraintContext pk:
            {
                var name = ConstraintName(pk.CONSTRAINT() != null, pk.uid());
                var columns = MapIndexColumnNames(pk.indexColumnNames()).Select(c => c.Column).ToList();
                return Wrap(name, At(new PrimaryKeyTableConstraint(columns), pk));
            }

            case MariaDBParser.UniqueKeyTableConstraintContext unique:
            {
                var name = ConstraintName(unique.CONSTRAINT() != null, unique.uid());
                var columns = MapIndexColumnNames(unique.indexColumnNames()).Select(c => c.Column).ToList();
                // The index name (if any) is the uid that is NOT the constraint name.
                var indexName = IndexNameFromUids(unique.CONSTRAINT() != null, unique.uid());
                return Wrap(name, At(new UniqueKeyTableConstraint(indexName, columns), unique));
            }

            case MariaDBParser.ForeignKeyTableConstraintContext fk:
            {
                var name = ConstraintName(fk.CONSTRAINT() != null, fk.uid());
                var columns = MapIndexColumnNames(fk.indexColumnNames()).Select(c => c.Column).ToList();
                var reference = fk.referenceDefinition();
                var referencedTable = MapQualifiedName(reference.tableName().fullId());
                var referencedColumns = reference.indexColumnNames() is { } refCols
                    ? MapIndexColumnNames(refCols).Select(c => c.Column).ToList()
                    : new List<Identifier>();
                var (onDelete, onUpdate) = MapReferenceActions(reference.referenceAction());

                return Wrap(name, At(new ForeignKeyTableConstraint(
                    columns, referencedTable, referencedColumns, onDelete, onUpdate), fk));
            }

            // CHECK and anything else is recognized but not modeled.
            default:
                return new IgnoredTableConstraint();
        }
    }

    private static ITableElement Wrap(string? name, TableConstraint constraint)
        => name is null
            ? constraint
            : new NamedTableConstraint(name, constraint) { Line = constraint.Line, Column = constraint.Column };

    // Stamps a node with the 1-based line/column where its source context starts, so later
    // phases (model building, reference validation) can report diagnostics that point back
    // into the source file (issue #53).
    private static T At<T>(T node, ParserRuleContext context) where T : SyntaxNode
    {
        node.Line = context.Start.Line;
        node.Column = context.Start.Column + 1;
        return node;
    }

    // The explicit CONSTRAINT name, if the constraint was written `CONSTRAINT <uid> ...`.
    // In the grammar the first uid after CONSTRAINT is the constraint name.
    private static string? ConstraintName(bool hasConstraintKeyword, MariaDBParser.UidContext[] uids)
        => hasConstraintKeyword && uids.Length > 0 ? UidText(uids[0]) : null;

    // The index name for a UNIQUE KEY: the trailing uid that names the index (distinct from
    // a leading CONSTRAINT name), or null when none is written.
    private static string? IndexNameFromUids(bool hasConstraintKeyword, MariaDBParser.UidContext[] uids)
    {
        var startIndex = hasConstraintKeyword ? 1 : 0;
        return uids.Length > startIndex ? UidText(uids[startIndex]) : null;
    }

    private static ITableElement MapInlineIndex(MariaDBParser.IndexColumnDefinitionContext index)
    {
        if (index is MariaDBParser.SimpleIndexDeclarationContext simple)
        {
            var indexName = simple.uid() is { } uid ? UidText(uid) : null;
            var method = MapIndexType(simple.indexType());
            var columns = MapIndexColumnNames(simple.indexColumnNames());
            return new IndexTableConstraint(indexName, method, columns);
        }

        // FULLTEXT / SPATIAL indexes are recognized but not modeled.
        return new IgnoredTableConstraint();
    }

    // ---- CREATE INDEX ----

    private static Statement MapCreateIndex(MariaDBParser.CreateIndexContext createIndex)
    {
        var name = UidText(createIndex.uid());
        var onTable = MapQualifiedName(createIndex.tableName().fullId());

        var statement = At(new CreateIndexStatement(name, onTable)
        {
            Unique = createIndex.UNIQUE() != null,
            IndexMethod = MapIndexType(createIndex.indexType()),
        }, createIndex);

        foreach (var column in MapIndexColumnNames(createIndex.indexColumnNames()))
        {
            statement.Columns.Add(column);
        }

        return statement;
    }

    // ---- Shared helpers ----

    private static (ReferentialAction? OnDelete, ReferentialAction? OnUpdate) MapReferenceActions(
        MariaDBParser.ReferenceActionContext? action)
    {
        if (action is null)
        {
            return (null, null);
        }

        // referenceAction is `ON DELETE ctl (ON UPDATE ctl)?` or `ON UPDATE ctl (ON DELETE
        // ctl)?`; the grammar assigns onDelete / onUpdate labels, but they are exposed as
        // ReferenceControlTypeContext[] with ON/DELETE/UPDATE tokens. Pair each control type
        // with whether a DELETE or UPDATE token precedes it.
        ReferentialAction? onDelete = null;
        ReferentialAction? onUpdate = null;

        var controls = action.referenceControlType();
        var onTokens = action.ON();
        var deleteToken = action.DELETE();

        // Determine ordering: if DELETE appears (it always does in arm 1) map the first
        // control to it; otherwise UPDATE comes first. We walk children in order to pair
        // each ON <kind> with its control type robustly.
        var kinds = new List<string>();
        for (var i = 0; i < action.ChildCount; i++)
        {
            var text = action.GetChild(i).GetText();
            if (string.Equals(text, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                kinds.Add("DELETE");
            }
            else if (string.Equals(text, "UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                kinds.Add("UPDATE");
            }
        }

        for (var i = 0; i < controls.Length && i < kinds.Count; i++)
        {
            var mapped = MapReferenceControl(controls[i]);
            if (kinds[i] == "DELETE")
            {
                onDelete = mapped;
            }
            else
            {
                onUpdate = mapped;
            }
        }

        return (onDelete, onUpdate);
    }

    private static ReferentialAction MapReferenceControl(MariaDBParser.ReferenceControlTypeContext control)
    {
        if (control.CASCADE() != null)
        {
            return ReferentialAction.Cascade;
        }

        if (control.SET() != null && control.NULL_LITERAL() != null)
        {
            return ReferentialAction.SetNull;
        }

        if (control.NO() != null && control.ACTION() != null)
        {
            // MariaDB treats NO ACTION as RESTRICT.
            return ReferentialAction.Restrict;
        }

        // RESTRICT (or the default).
        return ReferentialAction.Restrict;
    }

    private static IReadOnlyList<IndexColumn> MapIndexColumnNames(MariaDBParser.IndexColumnNamesContext columnNames)
    {
        var columns = new List<IndexColumn>();

        foreach (var columnName in columnNames.indexColumnName())
        {
            if (columnName.uid() is not { } uid)
            {
                // An expression-based index key (or a string-literal key) is not modeled;
                // skip it. This keeps functional-index parsing from throwing.
                continue;
            }

            bool? isAscending = columnName.ASC() != null ? true
                : columnName.DESC() != null ? false
                : null;

            columns.Add(new IndexColumn(new Identifier(UidText(uid)), isAscending));
        }

        return columns;
    }

    private static string? MapIndexType(MariaDBParser.IndexTypeContext? indexType)
    {
        if (indexType is null)
        {
            return null;
        }

        // indexType is `USING (BTREE | HASH | RTREE)`.
        if (indexType.BTREE() != null)
        {
            return "BTREE";
        }

        if (indexType.HASH() != null)
        {
            return "HASH";
        }

        if (indexType.RTREE() != null)
        {
            return "RTREE";
        }

        return null;
    }

    // ---- CREATE PROCEDURE ----

    private static Statement MapCreateProcedure(MariaDBParser.CreateProcedureContext createProcedure)
    {
        var statement = At(
            new CreateProcedureStatement(
                MapQualifiedName(createProcedure.fullId()),
                createProcedure.orReplace() is not null),
            createProcedure);

        foreach (var parameter in createProcedure.procedureParameter())
        {
            statement.Parameters.Add(At(
                new RoutineParameter(
                    new Identifier(UidText(parameter.uid())),
                    MapParameterMode(parameter.direction),
                    MapDataType(parameter.dataType())),
                parameter));
        }

        foreach (var option in createProcedure.routineOption())
        {
            ApplyRoutineOption(statement, option);
        }

        statement.Body = SourceText(createProcedure.routineBody());

        return statement;
    }

    private static ParameterMode MapParameterMode(IToken? direction)
        => direction?.Type switch
        {
            null => ParameterMode.In,
            MariaDBParser.OUT => ParameterMode.Out,
            MariaDBParser.INOUT => ParameterMode.InOut,
            _ => ParameterMode.In,
        };

    private static void ApplyRoutineOption(
        CreateProcedureStatement statement,
        MariaDBParser.RoutineOptionContext option)
    {
        switch (option)
        {
            case MariaDBParser.RoutineBehaviorContext behavior:
                // `NOT DETERMINISTIC` is the default; only a bare DETERMINISTIC sets it.
                statement.IsDeterministic = behavior.NOT() is null;
                break;

            case MariaDBParser.RoutineDataContext data:
                statement.SqlDataAccess = SqlDataAccessText(data);
                break;

            case MariaDBParser.RoutineSecurityContext security:
                statement.IsSecurityInvoker = security.context?.Type == MariaDBParser.INVOKER;
                break;

            // A COMMENT or LANGUAGE SQL clause does not participate in the model: LANGUAGE
            // SQL is the only language either engine supports, and a comment is not a
            // schema facet Squill tracks.
        }
    }

    // Renders a routine's data-access clause the way information_schema.ROUTINES spells it
    // (e.g. "READS SQL DATA"), so a parsed value compares equal to an extracted one. The
    // context's own text has the keywords concatenated without spaces.
    private static string SqlDataAccessText(MariaDBParser.RoutineDataContext data)
    {
        var words = new List<string>();

        for (var i = 0; i < data.ChildCount; i++)
        {
            if (data.GetChild(i) is ITerminalNode terminal)
            {
                words.Add(terminal.GetText().ToUpperInvariant());
            }
        }

        return string.Join(' ', words);
    }

    // The exact source text a context spans. Unlike GetText(), which concatenates tokens and
    // so discards all whitespace, this reads back from the input stream — required for a
    // routine body, which both engines return verbatim from ROUTINE_DEFINITION.
    private static string SourceText(ParserRuleContext context)
        => context.Start.InputStream.GetText(
            Interval.Of(context.Start.StartIndex, context.Stop.StopIndex));

    private static DataType MapDataType(MariaDBParser.DataTypeContext dataType)
    {
        var (typeName, dimensions, unsigned) = DataTypeDetails(dataType);

        var result = new DataType(typeName.ToLowerInvariant())
        {
            IsUnsigned = unsigned,
        };

        foreach (var dimension in dimensions)
        {
            result.Modifiers.Add(dimension);
        }

        return result;
    }

    // Extracts the canonical type name, any numeric dimensions (length, or precision/scale),
    // and whether the type is UNSIGNED, from a data-type context. Handles the data-type
    // alternatives Squill models; other alternatives fall back to the raw type name text.
    private static (string TypeName, IReadOnlyList<long> Dimensions, bool Unsigned) DataTypeDetails(
        MariaDBParser.DataTypeContext dataType)
    {
        switch (dataType)
        {
            case MariaDBParser.StringDataTypeContext s:
                return (s.typeName.Text, Dimensions(s.lengthOneDimension()), false);

            case MariaDBParser.DimensionDataTypeContext d:
            {
                var dims = Dimensions(d.lengthOneDimension())
                    .Concat(Dimensions(d.lengthTwoDimension()))
                    .Concat(Dimensions(d.lengthTwoOptionalDimension()))
                    .ToList();
                var unsigned = d.UNSIGNED().Length > 0;
                return (d.typeName.Text, dims, unsigned);
            }

            case MariaDBParser.SimpleDataTypeContext simple:
                return (simple.typeName.Text, Array.Empty<long>(), false);

            case MariaDBParser.NationalStringDataTypeContext n:
                return (n.typeName.Text, Dimensions(n.lengthOneDimension()), false);

            // Spatial, collection, national-varying, long-varchar, uuid, etc.: use the raw
            // text of the type, without modifiers. These are carried verbatim so the DB
            // extractor (which reads the same canonical name) can still hash-match for the
            // simple cases; complex ones are out of the modeled scope.
            default:
                return (FirstTypeToken(dataType), Array.Empty<long>(), false);
        }
    }

    private static string FirstTypeToken(MariaDBParser.DataTypeContext dataType)
    {
        // Fall back to the first terminal token's text as the type name.
        for (var i = 0; i < dataType.ChildCount; i++)
        {
            if (dataType.GetChild(i) is ITerminalNode terminal)
            {
                return terminal.GetText();
            }
        }

        return dataType.GetText();
    }

    private static IReadOnlyList<long> Dimensions(MariaDBParser.LengthOneDimensionContext? context)
        => context is null
            ? Array.Empty<long>()
            : new[] { ParseDecimal(context.decimalLiteral()) };

    private static IReadOnlyList<long> Dimensions(MariaDBParser.LengthTwoDimensionContext? context)
        => context is null
            ? Array.Empty<long>()
            : context.decimalLiteral().Select(ParseDecimal).ToList();

    private static IReadOnlyList<long> Dimensions(MariaDBParser.LengthTwoOptionalDimensionContext? context)
        => context is null
            ? Array.Empty<long>()
            : context.decimalLiteral().Select(ParseDecimal).ToList();

    private static long ParseDecimal(MariaDBParser.DecimalLiteralContext context)
        => long.Parse(context.GetText());

    private static QualifiedName MapQualifiedName(MariaDBParser.FullIdContext fullId)
    {
        // fullId is `uid (DOT_ID | '.' uid)?` — one or two segments. A DOT_ID token carries
        // a leading dot (e.g. ".table") that must be stripped.
        var segments = new List<Identifier> { new(UidText(fullId.uid()[0])) };

        if (fullId.DOT_ID() is { } dotId)
        {
            segments.Add(new Identifier(TrimIdentifier(dotId.GetText().TrimStart('.'))));
        }
        else if (fullId.uid().Length > 1)
        {
            segments.Add(new Identifier(UidText(fullId.uid()[1])));
        }

        return new QualifiedName(segments);
    }

    // The unquoted text of a uid. MariaDB identifiers may be backtick-quoted; strip the
    // backticks and unescape doubled backticks.
    private static string UidText(MariaDBParser.UidContext uid) => TrimIdentifier(uid.GetText());

    private static string TrimIdentifier(string text)
    {
        if (text.Length >= 2 && text[0] == '`' && text[^1] == '`')
        {
            return text[1..^1].Replace("``", "`");
        }

        return text;
    }
}
