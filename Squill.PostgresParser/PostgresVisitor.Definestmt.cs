using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // definestmt covers many DEFINE-family objects (AGGREGATE, OPERATOR, TYPE, TEXT SEARCH,
    // COLLATION). Only `CREATE TYPE name AS ENUM (...)` is modeled today; every other branch
    // is reported as unsupported rather than silently dropped.
    public override SyntaxNode VisitDefinestmt(PostgreSQLParser.DefinestmtContext context)
    {
        if (context.TYPE_P() is not null && context.ENUM_P() is not null)
        {
            return VisitCreateEnumType(context);
        }

        throw new NotImplementedException(
            "Only CREATE TYPE ... AS ENUM is supported among the DEFINE-family statements");
    }

    private CreateEnumTypeStatement VisitCreateEnumType(PostgreSQLParser.DefinestmtContext context)
    {
        // The enum alternative is `CREATE TYPE_P any_name AS ENUM_P ( opt_enum_val_list )`,
        // so any_name() yields a single name here.
        var name = ParseAnyName(context.any_name()[0]);

        var labels = new List<string>();

        if (context.opt_enum_val_list()?.enum_val_list() is { } enumValList)
        {
            foreach (var sconst in enumValList.sconst())
            {
                if (VisitSconst(sconst) is not LiteralExpression { Value: string label })
                {
                    throw new PostgresParseException("Unable to parse enum label");
                }

                labels.Add(label);
            }
        }

        return At(new CreateEnumTypeStatement(name, labels), context);
    }

    // any_name : colid attrs?  where attrs : (DOT attr_name)+
    // Parses a (possibly schema-qualified) object name into a QualifiedName, mirroring how
    // qualified_name and func_name are parsed elsewhere.
    private QualifiedName ParseAnyName(PostgreSQLParser.Any_nameContext context)
    {
        if (VisitColid(context.colid()) is not Identifier first)
        {
            throw new PostgresParseException("Unable to parse type name");
        }

        var segments = new List<Identifier> { first };

        if (context.attrs() is { } attrs)
        {
            foreach (var attrName in attrs.attr_name())
            {
                segments.Add(ParseNameSegment(attrName.collabel(), attrName));
            }
        }

        return new QualifiedName(segments);
    }
}
