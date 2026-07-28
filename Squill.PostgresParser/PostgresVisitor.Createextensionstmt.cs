using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitCreateextensionstmt(PostgreSQLParser.CreateextensionstmtContext context)
    {
        bool ifNotExists = context.EXISTS() is not null;

        if (VisitName(context.name()) is not Identifier name)
        {
            throw new PostgresParseException("Unable to parse extension name");
        }

        var statement = At(new CreateExtensionStatement(name, ifNotExists), context);

        foreach (var optItem in context.create_extension_opt_list().create_extension_opt_item())
        {
            if (optItem.SCHEMA() is not null)
            {
                if (VisitName(optItem.name()) is not Identifier schemaName)
                {
                    throw new PostgresParseException("Unable to parse extension SCHEMA name");
                }

                statement.Schema = schemaName;
            }
            else if (optItem.VERSION_P() is not null)
            {
                statement.Version = GetNonReservedWordOrSconstText(optItem.nonreservedword_or_sconst());
            }
            else if (optItem.FROM() is not null)
            {
                // Both FROM and CASCADE describe how the extension is installed rather than
                // what the installed extension is, so they are carried for fidelity but left
                // unmodeled — the provider warns (SQ1002) instead of throwing (issue #143).
                statement.FromVersion = GetNonReservedWordOrSconstText(optItem.nonreservedword_or_sconst());
            }
            else if (optItem.CASCADE() is not null)
            {
                statement.Cascade = true;
            }
        }

        return statement;
    }

    // A nonreservedword_or_sconst is either a bare word (VERSION 1.6) or a string
    // literal (VERSION '1.6'); return the underlying text either way.
    private string GetNonReservedWordOrSconstText(PostgreSQLParser.Nonreservedword_or_sconstContext context)
    {
        if (context.sconst() is { } sconst)
        {
            if (VisitSconst(sconst) is not LiteralExpression { Value: string stringValue })
            {
                throw new PostgresParseException("Unable to parse string constant");
            }

            return stringValue;
        }

        return context.GetText();
    }
}
