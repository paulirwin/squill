using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Encodes MariaDB element dependency rules for schema comparison. The shared classification
/// and foreign-key walk live in <see cref="DatabaseDependencyAnalyzerBase"/>; MariaDB has no
/// schema or extension objects, so it inherits the base's inert schema/extension behavior and
/// name-only dependent resolution, and supplies only the replaceable-type set and create-order
/// ranking.
/// </summary>
public class MariaDbDatabaseDependencyAnalyzer : DatabaseDependencyAnalyzerBase
{
    // A procedure or function's definition is replaced wholesale rather than altered facet by
    // facet. A view and a trigger are here too; all are scripted as DROP + CREATE, since
    // CREATE OR REPLACE is MariaDB-only syntax and this provider targets MySQL as well.
    // An event is replaceable for the same reason: MariaDB has ALTER EVENT but its clause
    // order is fixed and MySQL's partial-update semantics differ, so a changed event is
    // scripted as DROP + CREATE rather than altered facet by facet.
    public override bool IsReplaceableElementType(string type)
        => type is MariaDbElementTypes.SqlProcedure
            or MariaDbElementTypes.SqlFunction
            or MariaDbElementTypes.SqlView
            or MariaDbElementTypes.SqlTrigger
            or MariaDbElementTypes.SqlEvent;

    // MariaDB has no schema/extension objects that must precede tables, so every element sorts
    // to the same create order — except a view, a routine, and a trigger, which reference
    // tables. None is parsed for dependencies beyond its source/defining tables, so this
    // ordering is what makes one that reads a table in the same deploy work. A view selects
    // from tables, so it follows them; a procedure or function body may query either, so it
    // comes next; a trigger fires on a table and its body may touch any table or view, so it
    // comes last — as does an event, whose body may likewise touch anything.
    public override int GetCreateOrder(string type) => type switch
    {
        // A sequence must exist before a table whose column default calls NEXTVAL() on it, so
        // it sorts ahead of the default rank tables take (issue #218). Negative rather than
        // shifting everything else up, so the existing ranks keep the values their comments
        // describe. Drops invert this key, which is also what a sequence needs: it must be
        // dropped after the tables that referenced it.
        MariaDbElementTypes.SqlSequence => -1,
        MariaDbElementTypes.SqlView => 1,
        MariaDbElementTypes.SqlProcedure or MariaDbElementTypes.SqlFunction => 2,
        MariaDbElementTypes.SqlTrigger or MariaDbElementTypes.SqlEvent => 3,
        _ => 0,
    };
}
