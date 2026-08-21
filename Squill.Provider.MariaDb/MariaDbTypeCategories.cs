namespace Squill.Provider.MariaDb;

/// <summary>
/// Type-name category predicates shared by the two MariaDB model builders
/// (<see cref="MariaDbDatabaseModelBuilder"/>, which reads canonical type names from
/// <c>information_schema</c>, and <see cref="ParserWorkspaceModelBuilder"/>, which reads the type
/// name as written in the source SQL). They decide whether a type carries a length modifier
/// (<c>char(20)</c>) or a precision/scale modifier (<c>decimal(10,2)</c>) worth capturing.
///
/// These used to be duplicated in each builder and had drifted — only the parser copy accepted the
/// <c>dec</c>/<c>fixed</c> synonyms for <c>decimal</c>. They are unified here so both builders agree.
/// </summary>
internal static class MariaDbTypeCategories
{
    // char/varchar carry a length modifier.
    public static bool IsCharacterType(string typeName)
        => typeName is "char" or "varchar";

    // decimal (and its dec/fixed/numeric synonyms) carry a precision/scale modifier.
    public static bool IsDecimalType(string typeName)
        => typeName is "decimal" or "numeric" or "dec" or "fixed";

    // The temporal types that carry a fractional-seconds precision, e.g. datetime(3)
    // (issue #144). information_schema reports it in DATETIME_PRECISION rather than
    // NUMERIC_PRECISION, so it is modeled separately from the decimal precision above.
    //
    // `date` is absent deliberately: it has no time part and takes no precision. `year` takes a
    // display-width modifier that is not a fractional-seconds precision, so it is absent too.
    public static bool IsTemporalPrecisionType(string typeName)
        => typeName is "datetime" or "timestamp" or "time";

    // A vector's dimension is part of its type: both engines report COLUMN_TYPE as `vector(3)`
    // and neither accepts a bare `vector` (issue #217).
    //
    // It is its own category rather than one of the length types above because the catalog does
    // not report the dimension as a length. Measured, a `VECTOR(3)` column reports
    // CHARACTER_MAXIMUM_LENGTH 12, the storage size in bytes, four per float, so reading it
    // the way a varchar's length is read would model `vector(12)` against a declared `vector(3)`
    // and re-diff on every deploy. The dimension is recovered from COLUMN_TYPE instead.
    public static bool IsVectorType(string typeName) => typeName is "vector";
}
