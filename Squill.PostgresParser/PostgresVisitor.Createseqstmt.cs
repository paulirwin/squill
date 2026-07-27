using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // createseqstmt : CREATE opttemp SEQUENCE (IF_P NOT EXISTS)? qualified_name optseqoptlist
    //
    // A standalone sequence (issue #122). The option list reuses the same `seqoptelem` grammar
    // as an identity column's parenthesized option list, so the option handling mirrors
    // VisitColconstraintelem's — but the set of *acceptable* options differs: a declared
    // sequence may say AS <type>, while OWNED BY and RESTART are rejected (see below).
    public override SyntaxNode VisitCreateseqstmt(PostgreSQLParser.CreateseqstmtContext context)
    {
        // A TEMP/UNLOGGED sequence is session- or crash-scoped, so it can never be part of a
        // declared schema that a deploy converges on.
        if (context.opttemp() is { } temp && !string.IsNullOrEmpty(temp.GetText()))
        {
            throw new NotImplementedException(
                "A TEMPORARY or UNLOGGED sequence is not supported; only a persistent "
                + "CREATE SEQUENCE is part of a declared schema");
        }

        if (VisitQualified_name(context.qualified_name()) is not QualifiedName name)
        {
            throw new PostgresParseException("Unable to parse sequence name");
        }

        var statement = At(new CreateSequenceStatement(name, context.EXISTS() is not null), context);

        if (context.optseqoptlist()?.seqoptlist() is { } seqoptlist)
        {
            foreach (var option in seqoptlist.seqoptelem())
            {
                ApplySequenceOption(statement, option);
            }
        }

        return statement;
    }

    private void ApplySequenceOption(CreateSequenceStatement statement,
        PostgreSQLParser.SeqoptelemContext option)
    {
        if (option.NO() is not null)
        {
            // NO MINVALUE / NO MAXVALUE select the default bound — the same meaning as
            // omitting the option — so only NO CYCLE is recorded.
            if (option.CYCLE() is not null)
            {
                statement.IsCycling = false;
            }

            return;
        }

        if (option.CYCLE() is not null)
        {
            statement.IsCycling = true;
        }
        else if (option.START() is not null)
        {
            statement.StartValue = ParseSequenceNumber(option);
        }
        else if (option.INCREMENT() is not null)
        {
            statement.Increment = ParseSequenceNumber(option);
        }
        else if (option.MINVALUE() is not null)
        {
            statement.MinValue = ParseSequenceNumber(option);
        }
        else if (option.MAXVALUE() is not null)
        {
            statement.MaxValue = ParseSequenceNumber(option);
        }
        else if (option.CACHE() is not null)
        {
            statement.CacheSize = ParseSequenceNumber(option);
        }
        else if (option.AS() is not null)
        {
            // PostgreSQL accepts only smallint/integer/bigint here — the AS clause sets the
            // sequence's bounds, not an arbitrary type — so the name is taken as written and
            // normalized, rather than run through the full typename visitor (which parses a
            // TypenameContext; seqoptelem exposes only the bare simpletypename).
            statement.DataType = ParseSequenceDataType(option.simpletypename());
        }
        else if (option.OWNED() is not null)
        {
            // OWNED BY ties the sequence's lifetime to a column — which is exactly how the
            // sequence behind a serial column is created. In pg_depend both appear as an
            // auto ('a') dependency on a column, so an extracted model cannot tell a declared
            // OWNED BY sequence from the implicit one behind a serial column. Accepting it
            // would produce a schema that never converges: the sequence would be extracted
            // (or not) inconsistently with how it was declared. Rejecting it in source keeps
            // the failure at build time, where it names the fix.
            throw new NotImplementedException(
                "OWNED BY is not supported on a declared sequence, because it cannot be told "
                + "apart from the sequence implicitly created by a serial or identity column; "
                + "declare the column as serial or GENERATED AS IDENTITY instead");
        }
        else if (option.RESTART() is not null)
        {
            // RESTART repositions an existing sequence's counter. It is a runtime operation,
            // not part of a declaration, and has no meaning in a desired-state model.
            throw new NotImplementedException(
                "RESTART is not supported on a declared sequence; it is a runtime operation "
                + "on an existing sequence rather than part of its declaration");
        }
        else
        {
            // SEQUENCE NAME is an internal option pg_dump emits; nothing else is left.
            throw new NotImplementedException(
                $"Sequence option '{option.GetText()}' is not supported");
        }
    }

    // The AS clause of a CREATE SEQUENCE bounds the sequence, and PostgreSQL accepts only the
    // three integer types there. Anything else is rejected rather than carried into the model,
    // so an unsupported spelling fails at build time instead of at deploy.
    private static DataType ParseSequenceDataType(PostgreSQLParser.SimpletypenameContext? context)
    {
        var text = context?.GetText()
            ?? throw new PostgresParseException("Expected a type name after the sequence AS");

        var type = text.ToLowerInvariant() switch
        {
            "smallint" or "int2" => PostgresBuiltInDataType.SmallInt,
            "integer" or "int" or "int4" => PostgresBuiltInDataType.Integer,
            "bigint" or "int8" => PostgresBuiltInDataType.BigInt,
            _ => throw new NotImplementedException(
                $"A sequence may only be declared AS smallint, integer or bigint, not '{text}'"),
        };

        return new BuiltInDataType(type, text);
    }
}
