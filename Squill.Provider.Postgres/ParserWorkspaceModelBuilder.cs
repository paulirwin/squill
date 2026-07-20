using Squill.Core;
using Squill.PostgresParser;
using Squill.PostgresParser.Syntax;

namespace Squill.Provider.Postgres;

public class ParserWorkspaceModelBuilder : IDatabaseModelBuilder
{
    private readonly Workspace _workspace;
    private readonly IPostgresParser _postgresParser;

    public ParserWorkspaceModelBuilder(Workspace workspace, IPostgresParser postgresParser)
    {
        _workspace = workspace;
        _postgresParser = postgresParser;
    }

    public async Task<Model> ExtractModelAsync(CancellationToken cancellationToken = default)
    {
        var model = new Model();

        foreach (var file in _workspace.Files.Where(i => i.Kind == FileKind.Compile))
        {
            await ProcessFile(file, model, cancellationToken);
        }

        return model;
    }

    private async Task ProcessFile(IFile file, Model model, CancellationToken cancellationToken)
    {
        var text = await file.ReadAllTextAsync(cancellationToken);

        var root = _postgresParser.Parse(text);

        foreach (var statement in root.Statements)
        {
            if (statement is CreateTableStatement createTableStatement)
            {
                foreach (var element in MakeCreateTableElements(createTableStatement))
                {
                    model.Elements.Add(element);
                }
            }
            else if (statement is CreateIndexStatement createIndexStatement)
            {
                var element = MakeCreateIndexElement(createIndexStatement);

                model.Elements.Add(element);
            }
            else
            {
                throw new NotImplementedException(
                    $"Statement type {statement.GetType()} to Element transformation not yet implemented");
            }
        }
    }

    private static IEnumerable<Element> MakeCreateTableElements(CreateTableStatement createTableStatement)
    {
        var tableName = ToSqlName(createTableStatement.Name);

        var tableElement = new Element(PostgresElementTypes.SqlTable)
        {
            Name = tableName,
        };

        var primaryKeyColumns = new List<PostgresModelFactory.IndexedColumn>();

        AddTableColumnsRelationship(tableElement, tableName, createTableStatement, primaryKeyColumns);

        yield return tableElement;

        // Postgres names an inline primary key PK_<table>. The PK is emitted as its own
        // model element (not a table annotation) so both builders agree and so it is
        // visible to schema comparison and scripting.
        if (primaryKeyColumns.Count > 0)
        {
            var pkName = tableName.Sibling($"PK_{tableName.UnqualifiedName}");

            yield return PostgresModelFactory.CreatePrimaryKey(pkName, tableName, primaryKeyColumns);
        }
    }

    private static void AddTableColumnsRelationship(Element sqlTableElement,
        SqlName tableName,
        CreateTableStatement createTableStatement,
        List<PostgresModelFactory.IndexedColumn> primaryKeyColumns)
    {
        var columns = new Relationship(PostgresRelationshipNames.Columns);
        sqlTableElement.Relationships.Add(columns);

        foreach (var columnDefinition in createTableStatement.Elements.OfType<ColumnDefinition>())
        {
            var columnName = tableName.Child(columnDefinition.Name.Name);

            var element = new Element(PostgresElementTypes.SqlSimpleColumn)
            {
                Name = columnName,
            };

            foreach (var columnConstraint in columnDefinition.Constraints)
            {
                if (columnConstraint is NullableColumnConstraint nullableColumnConstraint)
                {
                    element.Properties.Add(new Property(PostgresPropertyNames.IsNullable, nullableColumnConstraint.Nullable));
                }
                else if (columnConstraint is PrimaryKeyColumnConstraint)
                {
                    primaryKeyColumns.Add(new PostgresModelFactory.IndexedColumn(columnName));

                    // PKs are not nullable
                    element.Properties.Add(new Property(PostgresPropertyNames.IsNullable, false));
                }
                else
                {
                    throw new NotImplementedException(
                        $"Column constraint type {columnConstraint.GetType()} to property mapping not implemented");
                }
            }

            if (columnDefinition.DataType is BuiltInDataType builtInDataType)
            {
                var typeSpec = new Element(PostgresElementTypes.SqlTypeSpecifier)
                {
                    Relationships =
                    {
                        new Relationship(PostgresRelationshipNames.Type)
                        {
                            new Reference(builtInDataType.Type.CanonicalName())
                            {
                                ExternalSource = "BuiltIns",
                            }
                        }
                    }
                };

                if (builtInDataType is { Type: PostgresBuiltInDataType.Varchar, Modifiers.Count: 0 })
                {
                    typeSpec.Properties.Add(new Property(PostgresPropertyNames.IsMax, true));
                }
                else if (builtInDataType.Modifiers.Count == 1)
                {
                    if (builtInDataType.Type is PostgresBuiltInDataType.Varchar or PostgresBuiltInDataType.Char)
                    {
                        if (builtInDataType.Modifiers[0] is not LiteralExpression { Value: long length})
                        {
                            throw new InvalidOperationException("Unexpected length modifier for varchar or character type");
                        }

                        // Store as int to match the DB-extraction builder and the script
                        // generator, which both use int for the Length property.
                        typeSpec.Properties.Add(new Property(PostgresPropertyNames.Length, (int)length));
                    }
                    else
                    {
                        throw new NotImplementedException(
                            $"Modifiers for built-in data type {builtInDataType.Type} not yet implemented");
                    }
                }
                else if (builtInDataType.Modifiers.Count > 1)
                {
                    if (builtInDataType.Type == PostgresBuiltInDataType.Decimal)
                    {
                        if (builtInDataType.Modifiers.Count != 2)
                        {
                            throw new InvalidOperationException("Expected only 2 modifiers for numeric/decimal type");
                        }

                        if (builtInDataType.Modifiers[0] is not LiteralExpression { Value: long precision }
                            || builtInDataType.Modifiers[1] is not LiteralExpression { Value: long scale })
                        {
                            throw new InvalidOperationException(
                                "Either precision or scale modifier for numeric/decimal type was not an integer");
                        }
                        
                        typeSpec.Properties.Add(new Property(PostgresPropertyNames.Precision, precision));
                        typeSpec.Properties.Add(new Property(PostgresPropertyNames.Scale, scale));
                    }
                    else
                    {
                        throw new NotImplementedException($"More than 1 modifier not yet implemented for built-in type {builtInDataType.Type}");
                    }
                }
                
                element.Relationships.Add(new Relationship(PostgresRelationshipNames.TypeSpecifier)
                {
                    typeSpec
                });
            }
            else
            {
                throw new NotImplementedException(
                    $"Data meta-type {columnDefinition.DataType.GetType()} to relationship mapping not implemented");
            }
            
            columns.Add(element);
        }
    }

    private static Element MakeCreateIndexElement(CreateIndexStatement createIndexStatement)
    {
        if (createIndexStatement.Name is null)
        {
            // TODO.PI: determine anonymous index naming convention for Postgres
            throw new NotImplementedException("Unnamed CREATE INDEX statements are not yet supported");
        }

        if (createIndexStatement.OnRelation.Only || createIndexStatement.OnRelation.Star)
        {
            throw new NotImplementedException("ONLY and descendant table syntax on CREATE INDEX are not yet supported");
        }

        var tableName = ToSqlName(createIndexStatement.OnRelation.Name);

        // Indexes always live in the same schema as their table, so the index name is
        // the table's qualifier with the index identifier as the final segment.
        var indexName = tableName.Sibling(createIndexStatement.Name.Name);

        var columns = new List<PostgresModelFactory.IndexedColumn>();

        foreach (var indexElement in createIndexStatement.Elements)
        {
            if (indexElement.Expression is not ColumnReferenceExpression columnReference)
            {
                throw new NotImplementedException(
                    $"Index element expression type {indexElement.Expression.GetType()} to element mapping not implemented");
            }

            bool? isAscending = indexElement.Direction is IndexElementDirection direction
                ? direction == IndexElementDirection.Asc
                : null;

            bool? nullsFirst = indexElement.NullOrder is IndexElementNullOrder nullOrder
                ? nullOrder == IndexElementNullOrder.NullsFirst
                : null;

            columns.Add(new PostgresModelFactory.IndexedColumn(
                tableName.Child(columnReference.Identifier.Name), isAscending, nullsFirst));
        }

        // NOTE: CONCURRENTLY and IF NOT EXISTS affect how the index gets created, not the desired schema state
        return PostgresModelFactory.CreateIndex(
            indexName,
            tableName,
            createIndexStatement.Unique,
            createIndexStatement.UsingMethod?.Name,
            columns);
    }

    private static SqlName ToSqlName(QualifiedName qualifiedName)
        => SqlName.Object(qualifiedName.Segments.Select(i => i.Name).ToArray());
}