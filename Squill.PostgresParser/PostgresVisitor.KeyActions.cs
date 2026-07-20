using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // key_actions is optional and, per the grammar, may contain an ON UPDATE and/or
    // an ON DELETE clause in either order. Returns the two actions (null = clause
    // absent) rather than a syntax node, since both FK constraint forms consume them.
    private (ReferentialAction? OnDelete, ReferentialAction? OnUpdate) ParseKeyActions(
        PostgreSQLParser.Key_actionsContext? context)
    {
        if (context is null)
        {
            return (null, null);
        }

        ReferentialAction? onDelete = context.key_delete() is { } delete
            ? ParseKeyAction(delete.key_action())
            : null;

        ReferentialAction? onUpdate = context.key_update() is { } update
            ? ParseKeyAction(update.key_action())
            : null;

        return (onDelete, onUpdate);
    }

    private ReferentialAction ParseKeyAction(PostgreSQLParser.Key_actionContext context)
    {
        if (context.CASCADE() is not null)
        {
            return ReferentialAction.Cascade;
        }

        if (context.RESTRICT() is not null)
        {
            return ReferentialAction.Restrict;
        }

        if (context.NO() is not null && context.ACTION() is not null)
        {
            return ReferentialAction.NoAction;
        }

        if (context.SET() is not null)
        {
            if (context.NULL_P() is not null)
            {
                return ReferentialAction.SetNull;
            }

            if (context.DEFAULT() is not null)
            {
                return ReferentialAction.SetDefault;
            }
        }

        throw new PostgresParseException($"Unable to parse referential key action: {context.GetText()}");
    }

    // opt_column_list is an optional parenthesized columnlist; absent -> empty list.
    private IReadOnlyList<Identifier> ParseOptColumnList(PostgreSQLParser.Opt_column_listContext? context)
        => context?.columnlist() is { } columnlist
            ? ParseColumnList(columnlist)
            : Array.Empty<Identifier>();

    private IReadOnlyList<Identifier> ParseColumnList(PostgreSQLParser.ColumnlistContext context)
    {
        var identifiers = new List<Identifier>();

        foreach (var columnElem in context.columnElem())
        {
            if (VisitColid(columnElem.colid()) is not Identifier identifier)
            {
                throw new PostgresParseException("Unable to parse column in column list");
            }

            identifiers.Add(identifier);
        }

        return identifiers;
    }
}
