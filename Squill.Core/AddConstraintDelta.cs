namespace Squill.Core;

/// <summary>
/// Adds a constraint to an already-created table, separately from the table's own CREATE.
///
/// Used to break a circular foreign key dependency: when two (or more) tables reference
/// each other, no create order lets every constraint be inline, because whichever table is
/// created first would reference one that does not exist yet. The constraints that close
/// the cycle are held back and added once every table exists.
/// </summary>
public class AddConstraintDelta : SchemaDelta
{
    public AddConstraintDelta(Element constraint, Element definingTable)
    {
        Constraint = constraint;
        DefiningTable = definingTable;
    }

    /// <summary>The constraint to add (a foreign key).</summary>
    public Element Constraint { get; }

    /// <summary>The table the constraint is defined on.</summary>
    public Element DefiningTable { get; }
}
