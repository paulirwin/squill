using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitQualified_name(PostgreSQLParser.Qualified_nameContext context)
    {
        if (VisitColid(context.colid()) is not Identifier first)
        {
            throw new PostgresParseException("Unable to parse qualified name identifier");
        }

        var segments = new List<Identifier>
        {
            first
        };

        // A dotted name (e.g. staging.event) carries its trailing segments as an
        // `indirection` of `.attr_name` elements. Each attr_name is a collabel, which for
        // an object name is an identifier — pull that out so schema-qualified names parse.
        if (context.indirection() is { } indirection)
        {
            foreach (var element in indirection.indirection_el())
            {
                if (element.DOT() is null || element.attr_name() is not { } attrName)
                {
                    throw new NotImplementedException(
                        "Only dotted attribute qualifiers are supported in a qualified name");
                }

                // A collabel is either a plain identifier (possibly quoted) or a keyword
                // (e.g. "event", an unreserved keyword usable as an object name). Parse the
                // identifier form for correct quote handling; for the keyword form, fold to
                // lower case as Postgres does for an unquoted identifier, so a mixed-case
                // segment matches the (lowercased) name the DB-extraction builder produces.
                var collabel = attrName.colLabel();

                var segment = collabel?.identifier() is { } identifierContext
                    && VisitIdentifier(identifierContext) is Identifier parsed
                        ? parsed
                        : new SimpleIdentifier((collabel?.GetText() ?? attrName.GetText()).ToLowerInvariant());

                segments.Add(segment);
            }
        }

        return new QualifiedName(segments);
    }
}