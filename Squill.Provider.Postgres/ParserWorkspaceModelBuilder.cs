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
                var element = MakeCreateTableElement(createTableStatement);

                model.Elements.Add(element);
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

    private static Element MakeCreateTableElement(CreateTableStatement createTableStatement)
    {
        var element = new Element(PostgresElementTypes.SqlTable)
        {
            Name = FormatQualifiedName(createTableStatement.Name),
        };

        AddTableColumnsRelationship(element, createTableStatement);
        
        return element;
    }

    private static void AddTableColumnsRelationship(Element sqlTableElement, CreateTableStatement createTableStatement)
    {
        var columns = new Relationship(PostgresRelationshipNames.Columns);
        sqlTableElement.Relationships.Add(columns);

        foreach (var columnDefinition in createTableStatement.Elements.OfType<ColumnDefinition>())
        {
            var element = new Element(PostgresElementTypes.SqlSimpleColumn)
            {
                Name = FormatQualifiedName(createTableStatement.Name, columnDefinition.Name.Name),
            };

            foreach (var columnConstraint in columnDefinition.Constraints)
            {
                if (columnConstraint is NullableColumnConstraint nullableColumnConstraint)
                {
                    element.Properties.Add(new Property(PostgresPropertyNames.IsNullable, nullableColumnConstraint.Nullable));
                }
                else if (columnConstraint is PrimaryKeyColumnConstraint primaryKeyColumnConstraint)
                {
                    // HACK.PI: this seems a little fragile except for the happy path
                    // TODO.PI: determine anonymous PK naming convention for Postgres
                    var tableName = createTableStatement.Name.Segments[^1];
                    var pkName = $"PK_{tableName}";
                    var pkQualifiedName = new QualifiedName(createTableStatement.Name.Segments
                        .Take(createTableStatement.Name.Segments.Count - 1)
                        .Append(new SimpleIdentifier(pkName)));

                    // NOTE: since this is on a single column, we fortunately don't have to support multiple columns this way
                    var pkElement = new Element(PostgresElementTypes.SqlPrimaryKeyConstraint)
                    {
                        Name = FormatQualifiedName(pkQualifiedName),
                        Relationships =
                        {
                            new Relationship(PostgresRelationshipNames.ColumnSpecifications)
                            {
                                new Element(PostgresElementTypes.SqlIndexedColumnSpecification)
                                {
                                    Relationships =
                                    {
                                        new Relationship(PostgresRelationshipNames.Column)
                                        {
                                            new Reference(element.Name)
                                        }
                                    }
                                }
                            },
                            new Relationship(PostgresRelationshipNames.DefiningTable)
                            {
                                new Reference(FormatQualifiedName(createTableStatement.Name))
                            }
                        }
                    };

                    sqlTableElement.Annotations.Add(
                        new Annotation(PostgresAnnotationTypes.SqlInlineConstraintAnnotation)
                        {
                            AttachedElement = pkElement,
                        });

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

        var tableName = createIndexStatement.OnRelation.Name;

        // Indexes always live in the same schema as their table
        var indexQualifiedName = new QualifiedName(tableName.Segments
            .Take(tableName.Segments.Count - 1)
            .Append(createIndexStatement.Name));

        var element = new Element(PostgresElementTypes.SqlIndex)
        {
            Name = FormatQualifiedName(indexQualifiedName),
        };

        element.Properties.Add(new Property(PostgresPropertyNames.IsUnique, createIndexStatement.Unique));

        if (createIndexStatement.UsingMethod is not null)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IndexMethod, createIndexStatement.UsingMethod.Name));
        }

        // NOTE: CONCURRENTLY and IF NOT EXISTS affect how the index gets created, not the desired schema state

        var columnSpecifications = new Relationship(PostgresRelationshipNames.ColumnSpecifications);
        element.Relationships.Add(columnSpecifications);

        foreach (var indexElement in createIndexStatement.Elements)
        {
            if (indexElement.Expression is not ColumnReferenceExpression columnReference)
            {
                throw new NotImplementedException(
                    $"Index element expression type {indexElement.Expression.GetType()} to element mapping not implemented");
            }

            var columnSpecification = new Element(PostgresElementTypes.SqlIndexedColumnSpecification)
            {
                Relationships =
                {
                    new Relationship(PostgresRelationshipNames.Column)
                    {
                        new Reference(FormatQualifiedName(tableName, columnReference.Identifier.Name))
                    }
                }
            };

            if (indexElement.Direction is IndexElementDirection direction)
            {
                columnSpecification.Properties.Add(
                    new Property(PostgresPropertyNames.IsAscending, direction == IndexElementDirection.Asc));
            }

            if (indexElement.NullOrder is IndexElementNullOrder nullOrder)
            {
                columnSpecification.Properties.Add(
                    new Property(PostgresPropertyNames.NullsFirst, nullOrder == IndexElementNullOrder.NullsFirst));
            }

            columnSpecifications.Add(columnSpecification);
        }

        element.Relationships.Add(new Relationship(PostgresRelationshipNames.IndexedObject)
        {
            new Reference(FormatQualifiedName(tableName))
        });

        return element;
    }

    private static string FormatQualifiedName(QualifiedName qualifiedName, string? childElementName = null)
    {
        var name = ToSqlName(qualifiedName);

        if (childElementName is not null)
        {
            name = name.Child(childElementName);
        }

        return name;
    }

    private static SqlName ToSqlName(QualifiedName qualifiedName)
        => SqlName.Object(qualifiedName.Segments.Select(i => i.Name).ToArray());
}