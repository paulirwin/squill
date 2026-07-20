namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>CREATE SCHEMA [IF NOT EXISTS] name</c> statement. Squill models a schema as a
/// declared, first-class object (like a table or extension), so the schema a table lives
/// in must be created explicitly rather than conjured implicitly at deploy time.
/// </summary>
public class CreateSchemaStatement : Statement
{
    public CreateSchemaStatement(Identifier name, bool ifNotExists)
    {
        Name = name;
        IfNotExists = ifNotExists;
    }

    public Identifier Name { get; }

    public bool IfNotExists { get; }
}
