using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // columnref : colid indirection?
    //
    // The trailing `indirection` carries the dotted segments of a qualified reference
    // (`NEW.price`), and dropping it silently would rename the column being referenced: the
    // qualifier would be kept as the column, so `NEW.a` and `NEW.b` would become one and the
    // same reference (issue #214).
    public override SyntaxNode VisitColumnref(PostgreSQLParser.ColumnrefContext context)
    {
        if (VisitColid(context.colid()) is not Identifier identifier)
        {
            throw new PostgresParseException("Unable to parse column reference identifier");
        }

        if (context.indirection() is not { } indirection)
        {
            return new ColumnReferenceExpression(identifier);
        }

        var segments = new List<Identifier> { identifier };

        foreach (var element in indirection.indirection_el())
        {
            // Only the dotted-attribute form is a plain column reference. A subscript
            // (`a[1]`) or a `.*` wildcard is a different construct, and guessing at one
            // would mean comparing a predicate that says something else.
            if (element.DOT() is null || element.attr_name() is not { } attrName)
            {
                throw new NotImplementedException(
                    "Only dotted attribute qualifiers are supported in a column reference");
            }

            // An attr_name is a collabel: either an identifier (possibly quoted) or a
            // keyword usable as a name. Parse the identifier form so quoting is honored;
            // fold the keyword form to lower case as PostgreSQL does for an unquoted
            // identifier, so a source `NEW.a` matches the `new.a` the catalog reports back.
            var collabel = attrName.colLabel();

            segments.Add(
                collabel?.identifier() is { } identifierContext
                    && VisitIdentifier(identifierContext) is Identifier parsed
                        ? parsed
                        : new SimpleIdentifier(
                            (collabel?.GetText() ?? attrName.GetText()).ToLowerInvariant()));
        }

        return new ColumnReferenceExpression(segments);
    }
}
