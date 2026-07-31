using System.Collections;
using System.Reflection;
using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

/// <summary>
/// Enumerates an <see cref="Expression"/> and every expression nested inside it.
///
/// <para>
/// The traversal is driven by reflection over each node's properties rather than by a
/// hand-written switch over the ~26 expression types. A switch would have to be extended every
/// time a node is added, and the failure mode when someone forgets is silence: the new node's
/// children simply stop being visited, and whatever the caller was looking for stops being found
/// without any test necessarily noticing. Reflection cannot forget.
/// </para>
///
/// <para>
/// Traversal descends through any node in the <c>Syntax</c> namespace, not merely
/// <see cref="Expression"/>-typed properties, because operands are not always held directly: a
/// function call's arguments are <c>FunctionArgument</c> wrappers, which are not expressions
/// themselves but each hold one. Stopping at the first non-<see cref="Expression"/> would silently
/// skip everything inside <c>f(a, 0x19)</c>. Only the <see cref="Expression"/>s found along the
/// way are yielded.
/// </para>
/// </summary>
public static class ExpressionWalker
{
    // Reflection is not free and these shapes never change for a given type, so the property
    // list is resolved once per node type rather than once per node.
    private static readonly Dictionary<Type, PropertyInfo[]> ChildProperties = [];

    private static readonly Lock Gate = new();

    /// <summary>
    /// The expression and every expression nested within it, parents before children.
    /// </summary>
    public static IEnumerable<Expression> DescendantsAndSelf(Expression expression)
    {
        // A parse tree is a tree, but the walk is reference-tracked anyway: a node reached twice
        // would otherwise be reported twice, which for a diagnostic means a duplicate warning.
        return Walk(expression, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static IEnumerable<Expression> Walk(object node, HashSet<object> visited)
    {
        if (!visited.Add(node))
        {
            yield break;
        }

        if (node is Expression expression)
        {
            yield return expression;
        }

        foreach (var property in ChildPropertiesOf(node.GetType()))
        {
            object? value;

            try
            {
                value = property.GetValue(node);
            }
            catch (TargetInvocationException)
            {
                // A computed property that throws is not worth failing the whole walk over: the
                // caller is collecting diagnostics, not evaluating the tree.
                continue;
            }

            if (value is null)
            {
                continue;
            }

            foreach (var child in Children(value))
            {
                foreach (var descendant in Walk(child, visited))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static IEnumerable<object> Children(object value)
    {
        // A syntax node held directly. Checked before the enumerable case because a node could in
        // principle be both, and the node itself is what should be descended into.
        if (IsSyntax(value.GetType()))
        {
            yield return value;
            yield break;
        }

        // Otherwise a collection of them. The collection's OWN type is deliberately not required
        // to be a syntax type — it is a List<> or an IReadOnlyList<> from the framework — so it is
        // the elements that are filtered, not the container.
        if (value is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (var item in enumerable)
        {
            if (item is not null && IsSyntax(item.GetType()))
            {
                yield return item;
            }
        }
    }

    // Only the parser's own syntax types are descended into. Anything else reachable from a node
    // — a string, an enum, a name — cannot contain an expression, and following it would mean
    // reflecting over arbitrary framework types.
    //
    // An array is explicitly NOT one, even though Expression[] reports the syntax namespace as its
    // own: it is a container to be iterated, not a node to be visited. Treating it as a node made
    // the walk stop at `IN (1, 0x19)`, whose operands the visitor happens to build with ToArray()
    // while ARRAY[...] uses a List — so the same predicate was walked or skipped depending on
    // which spelling produced it.
    private static bool IsSyntax(Type type) =>
        !type.IsArray && type.Namespace == typeof(Expression).Namespace;

    private static PropertyInfo[] ChildPropertiesOf(Type type)
    {
        lock (Gate)
        {
            if (ChildProperties.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .Where(p => !p.PropertyType.IsPrimitive
                            && p.PropertyType != typeof(string)
                            && !p.PropertyType.IsEnum)
                .ToArray();

            ChildProperties[type] = properties;

            return properties;
        }
    }
}
