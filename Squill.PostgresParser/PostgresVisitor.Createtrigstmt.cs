using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // createtrigstmt
    //   : CREATE TRIGGER name triggeractiontime triggerevents ON qualified_name
    //     triggerreferencing triggerforspec triggerwhen
    //     EXECUTE function_or_procedure func_name OPEN_PAREN triggerfuncargs CLOSE_PAREN
    //   | CREATE CONSTRAINT TRIGGER ...   (the second alternative — not modeled)
    //
    // Only the plain (non-CONSTRAINT) form is modeled. REFERENCING transition tables and a
    // WHEN (...) condition are recognized by the grammar but reported as unsupported rather
    // than silently dropped, since they change the trigger's behavior.
    public override SyntaxNode VisitCreatetrigstmt(PostgreSQLParser.CreatetrigstmtContext context)
    {
        // The CONSTRAINT-trigger alternative carries a CONSTRAINT token; it is a distinct,
        // rarely-used feature (deferrable constraint triggers) and is not modeled.
        if (context.CONSTRAINT() is not null)
        {
            throw new NotImplementedException(
                "CREATE CONSTRAINT TRIGGER is not supported");
        }

        var name = context.name().GetText();

        if (VisitQualified_name(context.qualified_name()) is not QualifiedName table)
        {
            throw new PostgresParseException("Unable to parse the trigger's table name");
        }

        var statement = At(new CreateTriggerStatement(name, table), context);

        statement.Timing = ParseTriggerTiming(context.triggeractiontime());
        statement.Events = ParseTriggerEvents(context.triggerevents());
        statement.Level = ParseTriggerLevel(context.triggerforspec());

        if (context.triggerreferencing()?.REFERENCING() is not null)
        {
            throw new NotImplementedException(
                "A REFERENCING (transition table) clause on CREATE TRIGGER is not supported");
        }

        if (context.triggerwhen()?.WHEN() is not null)
        {
            throw new NotImplementedException(
                "A WHEN (...) condition on CREATE TRIGGER is not supported");
        }

        statement.FunctionName = ParseFunctionName(context.func_name());

        foreach (var argument in ParseTriggerFunctionArguments(context.triggerfuncargs()))
        {
            statement.FunctionArguments.Add(argument);
        }

        return statement;
    }

    // triggeractiontime : BEFORE | AFTER | INSTEAD OF
    private static TriggerTiming ParseTriggerTiming(PostgreSQLParser.TriggeractiontimeContext context)
    {
        if (context.BEFORE() is not null)
        {
            return TriggerTiming.Before;
        }

        if (context.AFTER() is not null)
        {
            return TriggerTiming.After;
        }

        return TriggerTiming.InsteadOf;
    }

    // triggerevents : triggeroneevent (OR triggeroneevent)*
    // triggeroneevent : INSERT | DELETE | UPDATE | UPDATE OF columnlist | TRUNCATE
    private static TriggerEvents ParseTriggerEvents(PostgreSQLParser.TriggereventsContext context)
    {
        var events = TriggerEvents.None;

        foreach (var oneEvent in context.triggeroneevent())
        {
            if (oneEvent.OF() is not null)
            {
                throw new NotImplementedException(
                    "UPDATE OF column on CREATE TRIGGER is not supported");
            }

            events |= oneEvent.INSERT() is not null ? TriggerEvents.Insert
                : oneEvent.DELETE_P() is not null ? TriggerEvents.Delete
                : oneEvent.UPDATE() is not null ? TriggerEvents.Update
                : oneEvent.TRUNCATE() is not null ? TriggerEvents.Truncate
                : throw new PostgresParseException("Unable to parse a trigger event");
        }

        return events;
    }

    // triggerforspec : (FOR EACH? (ROW | STATEMENT))?
    // When the clause is absent PostgreSQL defaults to FOR EACH STATEMENT.
    private static TriggerLevel ParseTriggerLevel(PostgreSQLParser.TriggerforspecContext context)
        => context.triggerfortype()?.ROW() is not null ? TriggerLevel.Row : TriggerLevel.Statement;

    // triggerfuncargs : (triggerfuncarg (COMMA triggerfuncarg)*)?
    // triggerfuncarg : iconst | fconst | sconst | collabel
    // Each argument is normalized to the plain string PostgreSQL stores in pg_trigger.tgargs:
    // a string literal loses its quotes, a numeric or identifier arg is taken verbatim.
    private IEnumerable<string> ParseTriggerFunctionArguments(
        PostgreSQLParser.TriggerfuncargsContext context)
    {
        foreach (var argument in context.triggerfuncarg())
        {
            if (argument.sconst() is { } sconst)
            {
                yield return GetRoutineBodyText(sconst);
            }
            else if (argument.collabel() is { } collabel)
            {
                yield return collabel.GetText();
            }
            else
            {
                // iconst / fconst — a numeric literal, taken verbatim.
                yield return argument.GetText();
            }
        }
    }
}
