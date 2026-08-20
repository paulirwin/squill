using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // createtrigstmt
    //   : CREATE TRIGGER name triggeractiontime triggerevents ON qualified_name
    //     triggerreferencing? triggerforspec? triggerwhen?
    //     EXECUTE function_or_procedure func_name OPEN_PAREN triggerfuncargs CLOSE_PAREN
    //   | CREATE CONSTRAINT TRIGGER name AFTER triggerevents ON qualified_name
    //     optconstrfromtable? constraintattributespec FOR EACH ROW triggerwhen?
    //     EXECUTE function_or_procedure func_name OPEN_PAREN triggerfuncargs CLOSE_PAREN
    //
    // Both alternatives are read (issue #214). They share a rule, so the CONSTRAINT form is
    // told apart by its CONSTRAINT token; it has no triggeractiontime or triggerforspec of its
    // own because PostgreSQL fixes it at AFTER ... FOR EACH ROW.
    public override SyntaxNode VisitCreatetrigstmt(PostgreSQLParser.CreatetrigstmtContext context)
    {
        var isConstraintTrigger = context.CONSTRAINT() is not null;

        var name = context.name().GetText();

        if (VisitQualified_name(context.qualified_name()) is not QualifiedName table)
        {
            throw new PostgresParseException("Unable to parse the trigger's table name");
        }

        var statement = At(new CreateTriggerStatement(name, table), context);

        statement.IsConstraintTrigger = isConstraintTrigger;

        if (isConstraintTrigger)
        {
            // A constraint trigger is AFTER ... FOR EACH ROW by definition: the grammar spells
            // both as literal tokens rather than as the optional clauses the plain form uses,
            // so they are recorded here rather than parsed.
            statement.Timing = TriggerTiming.After;
            statement.Level = TriggerLevel.Row;

            ApplyConstraintAttributes(statement, context.constraintattributespec());

            // SET CONSTRAINTS addresses a deferred trigger through the constraint it creates,
            // and FROM ties that constraint to another table. Refused rather than dropped: it
            // is the referenced table that would silently go unrecorded.
            if (context.optconstrfromtable() is not null)
            {
                throw new NotImplementedException(
                    "FROM on CREATE CONSTRAINT TRIGGER is not supported");
            }
        }
        else
        {
            statement.Timing = ParseTriggerTiming(context.triggeractiontime());
            statement.Level = ParseTriggerLevel(context.triggerforspec());

            ApplyTransitionTables(statement, context.triggerreferencing());
        }

        statement.Events = ParseTriggerEvents(context.triggerevents(), statement.UpdateOfColumns);

        if (context.triggerwhen()?.a_expr() is { } whenExpression)
        {
            if (VisitA_expr(whenExpression) is not Expression condition)
            {
                throw new PostgresParseException(
                    "Unable to parse the trigger's WHEN condition");
            }

            statement.WhenCondition = condition;
        }

        statement.FunctionName = ParseFunctionName(context.func_name());

        foreach (var argument in ParseTriggerFunctionArguments(context.triggerfuncargs()))
        {
            statement.FunctionArguments.Add(argument);
        }

        return statement;
    }

    // constraintattributespec : constraintattributeElem*
    // Only DEFERRABLE / INITIALLY are meaningful on a trigger; PostgreSQL rejects the rest
    // (NOT VALID, NO INHERIT) here, so nothing else needs handling.
    private static void ApplyConstraintAttributes(
        CreateTriggerStatement statement,
        PostgreSQLParser.ConstraintattributespecContext? spec)
    {
        if (spec is null)
        {
            return;
        }

        bool? deferrable = null;
        bool? initiallyDeferred = null;

        foreach (var elem in spec.constraintattributeElem())
        {
            // Gated on the distinguishing keyword rather than on NOT, which several
            // alternatives share.
            if (elem.DEFERRABLE() is not null)
            {
                deferrable = elem.NOT() is null;
            }
            else if (elem.INITIALLY() is not null)
            {
                initiallyDeferred = elem.DEFERRED() is not null;
            }
        }

        // INITIALLY DEFERRED implies DEFERRABLE, exactly as on a table constraint: PostgreSQL
        // rejects the combination without it and reports tgdeferrable true for it, so both
        // spellings reduce to one answer here.
        statement.IsDeferrable = deferrable ?? initiallyDeferred ?? false;
        statement.IsInitiallyDeferred = initiallyDeferred ?? false;
    }

    // triggerreferencing : REFERENCING triggertransitions
    // triggertransition  : transitionoldornew transitionrowortable as_? transitionrelname
    //
    // Only the TABLE form exists in practice: PostgreSQL accepts the ROW spelling in the
    // grammar but rejects it ("ROW variable naming in the REFERENCING clause is not
    // supported"), so it is refused rather than modeled as though it worked.
    private static void ApplyTransitionTables(
        CreateTriggerStatement statement,
        PostgreSQLParser.TriggerreferencingContext? context)
    {
        if (context?.triggertransitions() is not { } transitions)
        {
            return;
        }

        foreach (var transition in transitions.triggertransition())
        {
            if (transition.transitionrowortable()?.TABLE() is null)
            {
                throw new NotImplementedException(
                    "REFERENCING ... ROW AS on CREATE TRIGGER is not supported");
            }

            var relationName = transition.transitionrelname().GetText();

            if (transition.transitionoldornew()?.NEW() is not null)
            {
                statement.NewTransitionTable = relationName;
            }
            else
            {
                statement.OldTransitionTable = relationName;
            }
        }
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
    //
    // UPDATE OF names the columns whose modification fires the trigger. The event is still
    // UPDATE; the column list narrows it, so it is collected alongside rather than folded into
    // the event set (issue #214).
    private TriggerEvents ParseTriggerEvents(
        PostgreSQLParser.TriggereventsContext context, IList<Identifier> updateOfColumns)
    {
        var events = TriggerEvents.None;

        foreach (var oneEvent in context.triggeroneevent())
        {
            if (oneEvent.OF() is not null)
            {
                foreach (var column in oneEvent.columnlist().columnElem())
                {
                    if (VisitColid(column.colid()) is not Identifier columnName)
                    {
                        throw new PostgresParseException(
                            "Unable to parse an UPDATE OF column name");
                    }

                    updateOfColumns.Add(columnName);
                }

                events |= TriggerEvents.Update;
                continue;
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
            else if (argument.colLabel() is { } collabel)
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
