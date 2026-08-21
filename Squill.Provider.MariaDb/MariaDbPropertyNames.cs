using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The <see cref="Squill.Core.Property"/> keys for the MariaDB provider. Inherits the shared
/// <see cref="SqlPropertyNames"/> vocabulary (the column/index/routine/view/trigger facets
/// MariaDB shares with Postgres) and adds the MariaDB-only properties: auto-increment (rather
/// than Postgres identity), unsigned integers, enum/set collection values, and the routine
/// characteristics MariaDB exposes.
/// </summary>
public sealed class MariaDbPropertyNames : SqlPropertyNames
{
    // Table options declared on a CREATE TABLE (issue #207). The whole tableOption clause used
    // to be dropped on both sides of the round trip, so a table declaring ENGINE/COLLATE/COMMENT
    // deployed and compared as if none of it were written.
    //
    // Only these three of the 40-odd options in the grammar are modeled, because only these
    // three were measured to survive the trip intact on both engines. All follow the
    // omit-when-default convention: a table that declares nothing records nothing, which matters
    // more here than elsewhere because the defaults genuinely differ between the two engines (a
    // bare table extracts as utf8mb4_uca1400_ai_ci on MariaDB 12 and utf8mb4_0900_ai_ci on
    // MySQL 9), so resolving an absent clause to a default would make one source build to two
    // different models.
    //
    // The rest of the clause is warned rather than modeled, for two distinct reasons. Options
    // like ROW_FORMAT persist but are reported for a table that never declared them (a bare
    // CREATE TABLE extracts ROW_FORMAT `Dynamic`), so a declared default cannot be told apart
    // from an absent clause. AUTO_INCREMENT is worse than unmodelable: it is a live counter
    // rather than a schema facet, and a table declared AUTO_INCREMENT=100 reports 103 after
    // three inserts, so modeling it would re-diff against any table that has ever been written.

    // The storage engine, from information_schema.TABLES.ENGINE. Stored case-folded on both
    // sides, because the catalog's casing follows no rule the parse side could reproduce:
    // measured, both engines accept any spelling on input and report back their own, and the two
    // disagree about what that is (MariaDB 12 reports MRG_MyISAM where MySQL 9 reports
    // MRG_MYISAM, and MySQL reports a lower-case ndbcluster). Folding is what lets a declared
    // engine hash-match an extracted one without a hardcoded name table that would be wrong on
    // one engine or the other.
    public const string Engine = nameof(Engine);

    // A collation: the table's default (information_schema.TABLES.TABLE_COLLATION) or, on a
    // column element, that column's own (COLUMNS.COLLATION_NAME, issue #216). One constant
    // serves both because the two never appear on the same element and mean the same thing.
    //
    // Held as the collation alone rather than a charset/collation pair because
    // information_schema.TABLES reports only TABLE_COLLATION: a table declared
    // `DEFAULT CHARSET=latin1` reads back latin1_swedish_ci, so the charset is recoverable from
    // the collation but not the reverse, and a separate charset property would be one the
    // extractor could never fill.
    //
    // At either level a declared collation equal to the target's default records nothing: every
    // string column reports a COLLATION_NAME whether one was written or not, so the extractor
    // cannot tell a declared default from an absent clause. See
    // MariaDbFamilyDatabaseSchemaProvider.DefaultCollation.
    public const string Collation = nameof(Collation);

    // The table's COMMENT, reported by information_schema.TABLES.TABLE_COMMENT. Named apart
    // from the event-level Comment below because the two are read from different catalog views
    // and neither element type carries the other's facet; sharing one constant would tie a
    // table's comment to an event's by name alone.
    public const string TableComment = nameof(TableComment);

    // A column's COMMENT, from information_schema.COLUMNS.COLUMN_COMMENT (issue #216). Named
    // apart from the table-level TableComment above for the same reason that one is named apart
    // from an event's: the two are read from different catalog views, and sharing a constant
    // would tie a column's comment to a table's by name alone.
    public const string ColumnComment = nameof(ColumnComment);

    // Whether a column is INVISIBLE, i.e. omitted from SELECT * (issue #216). Both engines
    // accept the keyword and report it in information_schema.COLUMNS.EXTRA, so it round-trips.
    // Recorded only when true: VISIBLE is the default and reports nothing, so a visible column
    // must record no property to match one read back from the catalog.
    public const string IsInvisible = nameof(IsInvisible);

    public const string IsUnsigned = nameof(IsUnsigned);
    public const string IsAutoIncrement = nameof(IsAutoIncrement);

    // A FULLTEXT or SPATIAL index (issue #146), held as the keyword itself. Both engines report
    // it in information_schema.STATISTICS.INDEX_TYPE, where it appears in place of the
    // BTREE/HASH access method — but it is not one: `USING FULLTEXT` is a syntax error on both,
    // and the kind must be written as a prefix (`CREATE FULLTEXT INDEX …`). So it is modeled
    // apart from IndexMethod rather than sharing its slot. Omitted for an ordinary index, per
    // the omit-when-default convention.
    public const string IndexKind = nameof(IndexKind);

    // NOTE: an index COMMENT (issue #211) reuses the general-purpose Comment name declared
    // below with the event properties, rather than adding an index-specific one: it is the same
    // concept on a different element type, and the model keys properties per element. Both
    // engines report it in information_schema.STATISTICS.INDEX_COMMENT, which is the empty
    // string rather than NULL when none was written, so the extractor maps empty to absent and
    // it follows the omit-when-default convention.

    // Whether the optimizer should ignore this index (issue #211): MySQL's INVISIBLE and
    // MariaDB's IGNORED. One property for what the two engines spell differently, because it is
    // one concept and an index means the same thing on either, the engine-specific keyword and
    // the catalog column it is read from are chosen by
    // MariaDbFamilyDatabaseSchemaProvider.IndexVisibility. Stored only when true, matching both
    // catalogs' default of a visible/not-ignored index.
    public const string IsHiddenFromOptimizer = nameof(IsHiddenFromOptimizer);

    // The prefix length of one index key — the 20 in `Brand(20)` (issue #161). Both engines
    // report it in information_schema.STATISTICS.SUB_PART, which is NULL for a whole-column
    // key, so this follows the omit-when-default convention and is stored only when declared.
    //
    // Not an optimization detail: a prefix is mandatory for indexing TEXT/BLOB on MySQL (which
    // rejects the DDL with error 1170 without one, where MariaDB silently substitutes 768), and
    // inside a PRIMARY KEY it decides which rows the table accepts as unique.
    public const string PrefixLength = nameof(PrefixLength);

    // The expression of a functional index key — the `a + b` in `CREATE INDEX ix ON t ((a + b))`
    // (issue #161). Held in place of the Column relationship, since such a key names no column.
    // MySQL-only: MariaDB has no functional indexes and rejects the syntax at the server.
    //
    // Carried as the raw/canonical pair (issue #156) rather than one string, because MySQL
    // rewrites what it is given: measured, a key declared `(a + b)` is stored and reported as
    // `` (`a` + `b`) ``. Only the canonical form participates in identity; the raw text is kept
    // for scripting.
    public const string KeyExpression = nameof(KeyExpression);
    public const string NormalizedKeyExpression = nameof(NormalizedKeyExpression);

    // ON UPDATE CURRENT_TIMESTAMP (issue #124): a timestamp/datetime column the engine
    // refreshes to the current time on every row update. Both engines report it in
    // information_schema.COLUMNS.EXTRA, though with different spellings.
    //
    // Holds the canonical token rather than a flag (issue #144), because the clause may carry a
    // fractional-seconds precision — "CURRENT_TIMESTAMP" or "CURRENT_TIMESTAMP(3)" — and the two
    // are not interchangeable: MySQL rejects an ON UPDATE precision that disagrees with its
    // column's. Omitted when absent, per the omit-when-default convention.
    public const string OnUpdateCurrentTimestamp = nameof(OnUpdateCurrentTimestamp);

    // The parenthesized value list of an enum/set column, e.g. ('G','PG'). Stored verbatim
    // so it can be reproduced when scripting, and read identically from both the parser and
    // the DB extractor (information_schema.COLUMN_TYPE) so the two sides hash-match.
    public const string CollectionValues = nameof(CollectionValues);

    // Stored procedures (issue #41) / functions (issue #74). Unlike PostgreSQL there is no
    // Language (SQL is the only one) and no ArgumentTypes (neither engine allows overloading,
    // so a routine's name alone identifies it). These are the extra routine characteristics
    // MariaDB records in information_schema.ROUTINES.
    public const string IsDeterministic = nameof(IsDeterministic);
    public const string SqlDataAccess = nameof(SqlDataAccess);
    // Also a view facet (issue #208): SQL SECURITY INVOKER on a CREATE VIEW, which
    // information_schema.VIEWS.SECURITY_TYPE reports in the same DEFINER/INVOKER terms as it
    // does for a routine. Shared rather than given a view-specific name because it is the same
    // question with the same two answers, and stored only when INVOKER for the same
    // omit-when-default reason: measured on both engines, an explicitly written
    // SQL SECURITY DEFINER is indistinguishable in the catalog from declaring nothing, so
    // recording it would make that view re-diff on every deploy. (PostgreSQL is the opposite
    // and does record its explicit default: see PostgresPropertyNames.SecurityInvoker.)
    public const string IsSecurityInvoker = nameof(IsSecurityInvoker);

    // View execution facets (issue #208).
    //
    // CheckOption is CASCADED or LOCAL, absent when the view declares none. A bare
    // WITH CHECK OPTION is recorded as CASCADED because that is what the catalog reports for
    // it on both engines (measured), the same normalization PostgreSQL applies.
    //
    // ViewAlgorithm is MERGE or TEMPTABLE, and is only modeled where the engine can report it
    // back: MariaDB has an ALGORITHM column in information_schema.VIEWS, MySQL has none
    // (measured), which is why it is gated on a schema-provider capability rather than always
    // recorded. UNDEFINED is the default and is never stored.
    public const string CheckOption = nameof(CheckOption);
    public const string ViewAlgorithm = nameof(ViewAlgorithm);

    // Triggers (issue #100). A trigger fires at a Timing (shared) for an Event
    // (INSERT/UPDATE/DELETE) — which is what both engines return from
    // information_schema.TRIGGERS (EVENT_MANIPULATION).
    public const string Event = nameof(Event);

    // Scheduled events (issue #122). These mirror the columns information_schema.EVENTS
    // reports, so a declared event hash-matches an extracted one. EventType is
    // "ONE TIME" or "RECURRING" and decides which of the other schedule facets apply:
    // ExecuteAt for a one-shot, IntervalValue/IntervalField/Starts/Ends for a recurring one.
    //
    // Status, PreserveOnCompletion and Comment follow the omit-when-default convention used
    // throughout: the catalog always reports them with defaults filled in, so a facet equal
    // to its default (ENABLED, NOT PRESERVE, no comment) is never stored.
    public const string EventType = nameof(EventType);
    public const string ExecuteAt = nameof(ExecuteAt);
    public const string IntervalValue = nameof(IntervalValue);
    public const string IntervalField = nameof(IntervalField);
    public const string Starts = nameof(Starts);
    public const string Ends = nameof(Ends);
    public const string Status = nameof(Status);
    public const string PreserveOnCompletion = nameof(PreserveOnCompletion);
    public const string Comment = nameof(Comment);

    // Sequences (issue #218). Names match the Postgres provider's, since both model the same
    // concept and SSDT's SqlSequence uses them, so the two providers stay readable side by
    // side. The values they carry are engine-specific, though: MariaDB's defaults are not
    // Postgres's (CACHE is 1000 here, 1 there), which is why the defaults live in
    // MariaDbSequenceDefaults rather than being shared.
    //
    // Omit-when-default as everywhere else: a sequence's backing table always reports every
    // option with its defaults filled in, so an option equal to its default is not stored or
    // the parsed model could not hash-match the extracted one.
    public const string StartValue = nameof(StartValue);
    public const string Increment = nameof(Increment);
    public const string MinValue = nameof(MinValue);
    public const string MaxValue = nameof(MaxValue);
    public const string CacheSize = nameof(CacheSize);
    public const string IsCycling = nameof(IsCycling);

    // Trigger firing order (issue #215). ActionOrder is the trigger's 1-based position among
    // the triggers sharing its table, timing and event — which is exactly what both engines
    // report as information_schema.TRIGGERS.ACTION_ORDER (measured; they agree).
    //
    // The position is modeled rather than the FOLLOWS/PRECEDES clause that produced it: neither
    // engine reports that clause back, so a model carrying it could never hash-match an
    // extracted one. Omit-when-default as elsewhere — a lone trigger in its group is always
    // position 1, so only a group of two or more records the property.
    public const string ActionOrder = nameof(ActionOrder);

    // The trigger this one fires immediately after, for scripting (issue #215). A trigger
    // added to a group that already exists on the server cannot rely on creation order to land
    // in the right place, so its CREATE needs a FOLLOWS clause naming the trigger before it.
    //
    // Derived from ActionOrder rather than from the source's own FOLLOWS/PRECEDES: it names
    // whichever trigger ends up at the preceding position, which is what the clause has to say.
    // It takes no part in the element's identity, since ActionOrder already carries the
    // ordering and naming the neighbour twice would make a trigger re-diff when an unrelated
    // sibling was renamed.
    public const string FollowsTrigger = nameof(FollowsTrigger);

    // The DEFINER account (issue #215), as `user@host` or a bare user, matching the form both
    // engines report in information_schema (DEFINER on ROUTINES/TRIGGERS/EVENTS/VIEWS).
    //
    // Absent means "whoever deploys". Measured on both engines: writing DEFINER = CURRENT_USER
    // and omitting DEFINER entirely are indistinguishable in the catalog, so both are modeled
    // as no definer rather than as two states the catalog could not tell apart.
    public const string Definer = nameof(Definer);
}
