using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitCreatestmt(PostgreSQLParser.CreatestmtContext context)
    {
        // TODO: support if not exists

        if (context.qualified_name().Length == 0)
        {
            throw new PostgresParseException("Expected CREATE TABLE statement to have a qualified name");
        }

        if (VisitQualified_name(context.qualified_name()[0]) is not QualifiedName qualifiedName)
        {
            throw new PostgresParseException("Unable to parse qualified name for CREATE TABLE statement");
        }

        var createTable = At(new CreateTableStatement(qualifiedName), context);

        // Carried as written (SourceText rather than GetText, so LOCAL TEMPORARY keeps the
        // space between its two words) for the provider to reject against the statement's
        // position; see CreateTableStatement.Persistence. Every opttemp alternative matches at
        // least one token, so a present context is always a real modifier.
        if (context.opttemp() is { } opttemp)
        {
            createTable.Persistence = SourceText(opttemp);
        }

        // Three shapes share this rule: the ordinary parenthesized element list, the typed
        // table (OF a_type), and the partition child (PARTITION OF parent FOR VALUES ...).
        // The latter two take their column set from the type or parent rather than declaring
        // one, so they are carried on the statement and reported unmodeled by the provider
        // (issue #143) rather than throwing here.
        if (context.PARTITION() is not null && context.OF() is not null)
        {
            // qualified_name[0] is the new table; [1] is the parent it partitions.
            if (context.qualified_name().Length < 2
                || VisitQualified_name(context.qualified_name()[1]) is not QualifiedName parent)
            {
                throw new PostgresParseException("Unable to parse PARTITION OF parent table name");
            }

            createTable.PartitionOf = parent;
            createTable.PartitionBound = SourceText(context.partitionboundspec());
        }
        else if (context.OF() is not null)
        {
            if (context.any_name() is not { } anyName)
            {
                throw new PostgresParseException("Unable to parse CREATE TABLE OF type name");
            }

            createTable.OfType = ParseAnyName(anyName);
        }

        if (context.opttableelementlist()?.tableelementlist() is { } tableelementlist)
        {
            foreach (var tableelementContext in tableelementlist.tableelement())
            {
                var tableElementNode = VisitTableelement(tableelementContext);

                if (tableElementNode is not ITableElement tableElement)
                {
                    throw new PostgresParseException("Unable to parse table element");
                }

                createTable.Elements.Add(tableElement);
            }
        }

        // A typed table and a partition child both use typedtableelementlist for the
        // constraints they add to inherited columns.
        if (context.opttypedtableelementlist()?.typedtableelementlist() is { } typedtableelementlist)
        {
            foreach (var typedelementContext in typedtableelementlist.typedtableelement())
            {
                var typedElementNode = VisitTypedtableelement(typedelementContext);

                if (typedElementNode is not ITableElement typedElement)
                {
                    throw new PostgresParseException("Unable to parse typed table element");
                }

                createTable.Elements.Add(typedElement);
            }
        }

        // PARTITION BY on the parent. This parsed before #143 but was never read, so a
        // partitioned table silently deployed unpartitioned; it is carried so the provider
        // can warn rather than lie.
        if (context.optpartitionspec() is { } optpartitionspec)
        {
            createTable.PartitionBy = SourceText(optpartitionspec);
        }

        if (context.optinherit() is { } optinherit)
        {
            foreach (var inheritQualName in optinherit.qualified_name_list().qualified_name())
            {
                if (VisitQualified_name(inheritQualName) is not QualifiedName inherits)
                {
                    throw new PostgresParseException("Unable to parse table INHERITS clause");
                }

                createTable.Inherits.Add(inherits);
            }
        }

        // The three trailing storage clauses, each of which parsed and was then never read, so it
        // vanished with no error or warning (issue #206). All three are carried rather than acted
        // on here, so the provider can reject or warn against the statement's own position.
        //
        // The fourth clause of the group, oncommitoption, is deliberately not carried. It is
        // legal only on a temporary table: PostgreSQL itself refuses it otherwise ("ON COMMIT can
        // only be used on temporary tables", measured on 18), and a temporary table is already
        // rejected by the build (issue #204), so no reachable declaration could act on it.
        if (context.table_access_method_clause()?.name() is { } accessMethodName)
        {
            if (VisitName(accessMethodName) is not Identifier accessMethod)
            {
                throw new PostgresParseException("Unable to parse USING access method for table");
            }

            createTable.AccessMethod = accessMethod;
        }

        // optwith : WITH reloptions | WITHOUT OIDS. Only the first alternative has parameters to
        // read; WITHOUT OIDS reaches here with a null reloptions() and correctly adds nothing.
        if (context.optwith()?.reloptions()?.reloption_list() is { } reloptionList)
        {
            AddStorageParameters(reloptionList, createTable.WithOptions);
        }

        if (context.opttablespace()?.name() is { } tablespaceName)
        {
            if (VisitName(tablespaceName) is not Identifier tablespace)
            {
                throw new PostgresParseException("Unable to parse TABLESPACE for table");
            }

            createTable.TableSpace = tablespace;
        }

        return createTable;
    }

    // typedtableelement : columnOptions | tableconstraint
    public override SyntaxNode VisitTypedtableelement(PostgreSQLParser.TypedtableelementContext context)
    {
        if (context.columnOptions() is { } columnOptions)
        {
            return VisitColumnOptions(columnOptions);
        }

        if (context.tableconstraint() is { } tableconstraint)
        {
            return VisitTableconstraint(tableconstraint);
        }

        throw new PostgresParseException("Unable to parse typed table element");
    }

    // columnOptions : colid (WITH OPTIONS)? colquallist
    //
    // A typed table's columns come from its composite type, so this only *constrains* an
    // already-existing column — there is no type to parse. The type is left unresolved
    // because the declaration genuinely does not state one; inventing a placeholder would be
    // a lie the model could act on. Nothing reads it today: a typed table is unmodeled.
    public override SyntaxNode VisitColumnOptions(PostgreSQLParser.ColumnOptionsContext context)
    {
        if (VisitColid(context.colid()) is not Identifier name)
        {
            throw new PostgresParseException("Unable to parse typed table column name");
        }

        var columnDef = At(new ColumnDefinition(name, new UnresolvedDataType(string.Empty)), context);

        AddColumnConstraints(columnDef, context.colquallist());

        return columnDef;
    }
}