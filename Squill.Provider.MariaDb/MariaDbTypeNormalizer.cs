namespace Squill.Provider.MariaDb;

/// <summary>
/// Renders a routine parameter's type in one canonical spelling, so a model parsed from
/// source hash-matches one extracted from a live database — on either engine.
///
/// This is needed because the two engines disagree about how they report a routine
/// parameter's type. MariaDB retains an integer type's display width, MySQL 8+ does not:
/// the same <c>INT</c> parameter comes back as <c>int(11)</c> from MariaDB and <c>int</c>
/// from MySQL. A display width has no effect on the value a parameter accepts, so it is
/// discarded here and neither engine's spelling leaks into the model.
///
/// The two exceptions are deliberate. <c>tinyint(1)</c> is kept, because it is how both
/// engines record a <c>BOOL</c> parameter and dropping the width would make BOOL and
/// TINYINT indistinguishable. And <c>json</c> is folded to <c>longtext</c>, which is what
/// MariaDB stores a JSON parameter as — MySQL keeps a distinct <c>json</c> type, so
/// without this a JSON parameter could not round-trip on both engines.
/// </summary>
public static class MariaDbTypeNormalizer
{
    // Integer types whose display width (the "(11)" in "int(11)") carries no meaning and is
    // reported inconsistently between engines.
    private static readonly HashSet<string> DisplayWidthTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "tinyint",
            "smallint",
            "mediumint",
            "int",
            "integer",
            "bigint",
        };

    /// <summary>
    /// Builds the canonical type text from a bare type name and its declared modifiers —
    /// a length, or a precision and scale.
    /// </summary>
    public static string Normalize(string typeName, IEnumerable<long> declaredModifiers, bool isUnsigned)
    {
        var name = Canonicalize(typeName);
        var modifiers = declaredModifiers.ToList();

        // BOOL and BOOLEAN are spelled tinyint(1) by both engines, and are indistinguishable
        // from a written TINYINT(1) once stored — so a bare BOOL gains the width here.
        if (IsBooleanAlias(typeName) && modifiers.Count == 0)
        {
            modifiers = [1];
        }

        var keepModifiers = modifiers.Count > 0 && !IsDiscardedDisplayWidth(name, modifiers);

        var text = keepModifiers
            ? $"{name}({string.Join(",", modifiers)})"
            : name;

        return isUnsigned ? $"{text} unsigned" : text;
    }

    private static bool IsBooleanAlias(string typeName)
        => typeName.Equals("bool", StringComparison.OrdinalIgnoreCase)
            || typeName.Equals("boolean", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps a type name to its canonical spelling, without modifiers: the type the engines
    /// actually store, rather than the alias that was written. <c>integer</c> is an alias both
    /// engines report as <c>int</c>, MariaDB stores a JSON column or parameter as
    /// <c>longtext</c>, and the national-character and <c>real</c> aliases resolve to the
    /// <c>varchar</c>/<c>char</c>/<c>double</c> both engines were measured to record.
    /// </summary>
    public static string Canonicalize(string typeName)
    {
        var name = typeName.ToLowerInvariant();

        return name switch
        {
            "integer" => "int",
            "bool" or "boolean" => "tinyint",
            "json" => "longtext",
            "dec" or "fixed" or "numeric" => "decimal",
            // The national-character types are stored as ordinary varchar/char with the utf8
            // character set, which both engines were measured to report as DATA_TYPE varchar and
            // char (issue #162). Squill does not model a column's character set, so the name is
            // all that is left to fold — and folding it is what puts an nvarchar back among the
            // length-carrying types, without which the generated DDL is a bare `nvarchar` that
            // both engines reject as a syntax error.
            "nvarchar" => "varchar",
            "nchar" => "char",
            // CHARACTER is a plain synonym for CHAR; the VARYING spellings are resolved to
            // varchar/nvarchar earlier, by the parser.
            "character" => "char",
            // LONG and LONG VARCHAR are synonyms for MEDIUMTEXT, and LONG VARBINARY for
            // MEDIUMBLOB (measured on both engines, all supported majors). The parser resolves
            // the two-word spellings to these names; a bare LONG arrives as written. Without
            // the fold the type name was the bare first token, `long`, which neither engine
            // has, the generated DDL was rejected outright rather than merely re-diffing.
            "long" => "mediumtext",
            // REAL is a documented synonym for DOUBLE unless REAL_AS_FLOAT is set, which is not
            // the default on either engine — so a column written REAL comes back as double and
            // would otherwise re-diff on every deploy (issue #162).
            "real" => "double",
            _ => name,
        };
    }

    // A single modifier on an integer type is a display width, which is discarded — except
    // tinyint(1), which is how both engines record BOOL and so must be preserved.
    private static bool IsDiscardedDisplayWidth(string canonicalName, IReadOnlyList<long> modifiers)
        => modifiers.Count == 1
            && DisplayWidthTypes.Contains(canonicalName)
            && !(canonicalName == "tinyint" && modifiers[0] == 1);
}
